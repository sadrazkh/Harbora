using FluentAssertions;
using Harbora.NodeAgent.Auditing;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Database;
using Harbora.NodeAgent.Hosting;
using Harbora.NodeAgent.Identity;
using Harbora.NodeAgent.Inventory;
using Harbora.NodeAgent.Observability;
using Harbora.NodeAgent.Runtime;
using Harbora.NodeAgent.Security;
using Harbora.NodeAgent.State;
using Harbora.NodeAgent.Tests.Fakes;
using Harbora.NodeAgent.Transport;
using Harbora.NodeAgent.Tunnels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.NodeAgent.Tests;

/// <summary>
/// What the node actually says about itself every thirty seconds.
///
/// <para>
/// There was no test of a heartbeat's contents at all until this file, which is exactly how
/// <c>activeDatabaseGrants</c> and <c>activeTunnels</c> came to be declared in the contract, read by
/// the panel, rendered on the Nodes page — and never once written by the agent. Every node reported
/// zero for both, forever, and nothing was red.
/// </para>
/// </summary>
public sealed class HeartbeatTests : IDisposable
{
    private readonly TempAgent _agent = new(o => o.TunnelGatewayUrl = "gw.harbora.test:8443");
    private readonly TestCertificateAuthority _ca = new();
    private readonly InMemoryTransportPair _pair = new();
    private readonly FakeContainerRuntime _runtime = new();
    private readonly FakeHostFacts _host = new();
    private readonly SecretRedactor _redactor = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));
    private readonly AcceptingTunnelGateway _gateway = new();
    private readonly NodeMetrics _metrics;
    private readonly JsonFileStore<NodeState> _state;
    private readonly NodeIdentityStore _identities;
    private readonly WorkloadRegistry _workloads;
    private readonly LocalSecretVault _vault;
    private readonly List<NodeEvent> _events = [];
    private readonly CollectingEvents _publisher;

    private TunnelSupervisor? _tunnels;
    private ControlChannel? _channel;
    private HeartbeatReporter? _reporter;

    public HeartbeatTests()
    {
        _publisher = new CollectingEvents(_events);
        _metrics = new NodeMetrics(_clock);
        _state = TestFactories.Store<NodeState>(_agent, "node.json");
        _state.Save(TestFactories.EnrolledState());

        _identities = new NodeIdentityStore(_agent.Options.IdentityDirectory);
        var csr = _identities.CreateSigningRequest("test-node", newKey: true);
        _identities.StoreCertificate(
            _ca.Sign(csr, _clock.GetUtcNow(), _clock.GetUtcNow().AddDays(90)), _ca.CertificatePem);

        _workloads = new WorkloadRegistry(TestFactories.Store<WorkloadRegistryState>(_agent, "workloads.json"));
        _vault = new LocalSecretVault(_identities);

        SeedDatabaseWorkload();
    }

    // --- the contract's two gauges ---

    [Fact]
    public async Task A_heartbeat_reports_the_grants_and_tunnels_this_node_is_actually_serving()
    {
        var channel = await ConnectedChannelAsync();

        // One temporary grant, minted through the real manager, published through the real
        // supervisor: one active grant and one connected tunnel on this node.
        await Manager().CreateAsync(GrantSpec(), CancellationToken.None);

        await Reporter(channel).SendAsync(Identity(), credentialRevoked: false, CancellationToken.None);

        var heartbeat = await NextHeartbeatAsync();

        heartbeat.ActiveDatabaseGrants.Should().Be(1, "the panel renders this number on the Nodes page");
        heartbeat.ActiveTunnels.Should().Be(1, "the grant is published through an outbound tunnel");
    }

    [Fact]
    public async Task A_node_serving_nothing_reports_zero_rather_than_omitting_the_gauges()
    {
        var channel = await ConnectedChannelAsync();

        await Reporter(channel).SendAsync(Identity(), credentialRevoked: false, CancellationToken.None);

        var heartbeat = await NextHeartbeatAsync();

        heartbeat.ActiveDatabaseGrants.Should().Be(0);
        heartbeat.ActiveTunnels.Should().Be(0);
    }

    [Fact]
    public async Task A_revoked_grant_stops_being_counted()
    {
        var channel = await ConnectedChannelAsync();
        var manager = Manager();

        await manager.CreateAsync(GrantSpec(), CancellationToken.None);
        await manager.RevokeAsync("gr-1", "tenant-1", "no longer needed", true, CancellationToken.None);

        await Reporter(channel).SendAsync(Identity(), credentialRevoked: false, CancellationToken.None);

        var heartbeat = await NextHeartbeatAsync();

        // A count the node cannot stand behind is worse than no count: the tunnel is closed and the
        // engine user is dropped, so reporting one open door would be a lie an operator acts on.
        heartbeat.ActiveDatabaseGrants.Should().Be(0);
        heartbeat.ActiveTunnels.Should().Be(0);
    }

    /// <summary>The rest of the frame, pinned now that anything reads it at all.</summary>
    [Fact]
    public async Task A_heartbeat_carries_the_nodes_identity_load_and_certificate_expiry()
    {
        var channel = await ConnectedChannelAsync();
        var identity = Identity();

        await Reporter(channel).SendAsync(identity, credentialRevoked: false, CancellationToken.None);

        var heartbeat = await NextHeartbeatAsync();

        heartbeat.NodeId.Should().Be("node-test-1");
        heartbeat.AgentVersion.Should().Be(AgentVersion.Current);
        heartbeat.Health.Should().Be(NodeHealthState.Healthy);
        heartbeat.Load1.Should().Be(_host.Load.One);
        heartbeat.FreeMemoryBytes.Should().Be(_host.FreeMemoryBytes);
        heartbeat.FreeDiskBytes.Should().Be(_host.DiskSpace.FreeBytes);
        heartbeat.Draining.Should().BeFalse();
        heartbeat.CertificateExpiresAt.Should().Be(identity.NotAfter);
    }

    /// <summary>
    /// The two gauges reach the panel by name, over JSON, with nothing at compile time joining the
    /// schema to the record. Populating them is worth little if a rename can quietly un-populate
    /// them again.
    /// </summary>
    [Fact]
    public void The_gauges_are_spelt_on_the_wire_the_way_the_schema_spells_them()
    {
        var json = NodeContract.Serialize(new NodeHeartbeat
        {
            NodeId = "node-1",
            AgentVersion = AgentVersion.Current,
            Health = NodeHealthState.Healthy,
            ActiveDatabaseGrants = 2,
            ActiveTunnels = 3,
        });

        var schema = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepoPaths.ContractV1, "node-agent.v1.schema.json")));

        var declared = schema.RootElement.GetProperty("$defs").GetProperty("nodeHeartbeat")
            .GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();

        declared.Should().Contain(["activeDatabaseGrants", "activeTunnels"]);

        var sent = System.Text.Json.JsonDocument.Parse(json).RootElement;
        sent.GetProperty("activeDatabaseGrants").GetInt32().Should().Be(2);
        sent.GetProperty("activeTunnels").GetInt32().Should().Be(3);
    }

    // --- the events the heartbeat loop is the only observer of ---

    [Fact]
    public async Task A_node_running_out_of_disk_says_so_once_rather_than_every_thirty_seconds()
    {
        var channel = await ConnectedChannelAsync();
        var reporter = Reporter(channel);

        await reporter.SendAsync(Identity(), credentialRevoked: false, CancellationToken.None);

        _host.DiskSpace = new DiskSpace(100_000_000_000, 1_000_000_000);

        await reporter.SendAsync(Identity(), credentialRevoked: false, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(30));
        await reporter.SendAsync(Identity(), credentialRevoked: false, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(30));
        await reporter.SendAsync(Identity(), credentialRevoked: false, CancellationToken.None);

        _events.Should().ContainSingle(e => e.Kind == NodeEventKinds.DiskPressure);
    }

    [Fact]
    public async Task A_container_that_falls_over_between_heartbeats_is_reported()
    {
        var channel = await ConnectedChannelAsync();
        var reporter = Reporter(channel);

        Deploy("shop-app-r1", "running");
        await reporter.SendAsync(Identity(), credentialRevoked: false, CancellationToken.None);

        // Nothing deployed it and nothing stopped it: a crash is the one container transition no
        // other code path on this node reports.
        Deploy("shop-app-r1", "exited");
        await reporter.SendAsync(Identity(), credentialRevoked: false, CancellationToken.None);

        var reported = _events.Should().ContainSingle(e => e.Kind == NodeEventKinds.ContainerStateChanged).Subject;
        reported.WorkloadId.Should().Be("wl-pg");
        reported.Message.Should().Contain("exited");
    }

    [Fact]
    public async Task A_helper_container_the_agent_ran_for_itself_is_not_node_news()
    {
        var channel = await ConnectedChannelAsync();
        var reporter = Reporter(channel);

        // The volume archiver labels its throwaway busybox as managed. It carries no workload.
        _runtime.Containers["helper"] = Container("harbora-snapshot-helper", "running", workloadId: null);
        await reporter.SendAsync(Identity(), credentialRevoked: false, CancellationToken.None);

        _runtime.Containers["helper"] = Container("harbora-snapshot-helper", "exited", workloadId: null);
        await reporter.SendAsync(Identity(), credentialRevoked: false, CancellationToken.None);

        _events.Should().NotContain(e => e.Kind == NodeEventKinds.ContainerStateChanged);
    }

    [Fact]
    public async Task A_first_heartbeat_after_a_restart_announces_nothing_it_had_already_said()
    {
        // The node comes up already under pressure, with the tunnel already open. Both were true
        // before the restart, both were published then, and the outbox is what carried them.
        _host.DiskSpace = new DiskSpace(100_000_000_000, 1_000_000_000);

        var channel = await ConnectedChannelAsync();
        await Manager().CreateAsync(GrantSpec(), CancellationToken.None);

        await Reporter(channel).SendAsync(Identity(), credentialRevoked: false, CancellationToken.None);

        _events.Should().NotContain(e => e.Kind == NodeEventKinds.DiskPressure);

        // …but the heartbeat itself still reports the state, so nothing is actually hidden.
        var heartbeat = await NextHeartbeatAsync();
        heartbeat.Health.Should().Be(NodeHealthState.Degraded);
        heartbeat.ActiveDatabaseGrants.Should().Be(1);
    }

    [Fact]
    public async Task A_tunnel_that_is_taken_down_is_not_announced_twice()
    {
        var channel = await ConnectedChannelAsync();
        var reporter = Reporter(channel);
        var manager = Manager();

        await manager.CreateAsync(GrantSpec(), CancellationToken.None);
        await reporter.SendAsync(Identity(), credentialRevoked: false, CancellationToken.None);

        _events.Clear();
        await manager.RevokeAsync("gr-1", "tenant-1", "done", true, CancellationToken.None);
        await reporter.SendAsync(Identity(), credentialRevoked: false, CancellationToken.None);

        // database-grant.revoked already says the socket closed; a tunnel event beside it is the
        // same news twice.
        _events.Should().OnlyContain(e => e.Kind == NodeEventKinds.DatabaseGrantRevoked);
    }

    [Fact]
    public async Task A_tunnel_that_drops_between_heartbeats_is_reported()
    {
        // The one adapter in this path with any logic in it is TunnelSupervisor.ByKey(); the tracker
        // tests prove the rule, and this proves the rule is actually being fed.
        _agent.Options.Reconnect.Jitter = false;
        _agent.Options.Reconnect.InitialDelayMs = 1;

        var channel = await ConnectedChannelAsync();
        var reporter = Reporter(channel);

        await Manager().CreateAsync(GrantSpec(), CancellationToken.None);
        Tunnels().ByKey()["gr-1"].Status.Should().Be(TunnelStatus.Connected);

        await reporter.SendAsync(Identity(), credentialRevoked: false, CancellationToken.None);

        // The gateway hangs up and then refuses the retry, the way it does for a grant it no longer
        // knows. The supervisor keeps the entry either way, so the same key is present in both
        // observations under two statuses — which is the case the panel's feed exists for.
        _gateway.Accept = false;
        _gateway.Drop();
        await WaitUntilAsync(() => Tunnels().ByKey()["gr-1"].Status == TunnelStatus.Failed);

        await reporter.SendAsync(Identity(), credentialRevoked: false, CancellationToken.None);

        var reported = _events.Should().ContainSingle(e => e.Kind == NodeEventKinds.TunnelStateChanged).Subject;
        reported.Data!["tunnel"].Should().Be("gr-1");
        reported.Data!["previous"].Should().Be("connected");
    }

    // --- the two ways an announcement can go wrong ---

    [Fact]
    public async Task An_event_that_could_not_be_recorded_is_announced_again_next_heartbeat()
    {
        var channel = await ConnectedChannelAsync();
        var reporter = Reporter(channel);

        await reporter.SendAsync(Identity(), credentialRevoked: false, CancellationToken.None);

        // A full disk is what fails the outbox write, and a full disk is what the event is about.
        _publisher.Refuse = true;
        _host.DiskSpace = new DiskSpace(100_000_000_000, 1_000_000_000);

        await reporter.SendAsync(Identity(), credentialRevoked: false, CancellationToken.None);
        _events.Should().BeEmpty("nothing was recorded, so nothing was announced");

        _publisher.Refuse = false;
        await reporter.SendAsync(Identity(), credentialRevoked: false, CancellationToken.None);

        _events.Should().ContainSingle(e => e.Kind == NodeEventKinds.DiskPressure,
            "a transition the control plane never heard is still news");
    }

    [Fact]
    public async Task Two_heartbeat_loops_at_once_announce_a_transition_once()
    {
        // RunSessionAsync waits only five bounded seconds for a heartbeat task before reconnecting,
        // so a heartbeat blocked on a sick daemon leaves one loop running as the next one starts.
        var channel = await ConnectedChannelAsync();
        var reporter = Reporter(channel);

        await reporter.SendAsync(Identity(), credentialRevoked: false, CancellationToken.None);

        _host.DiskSpace = new DiskSpace(100_000_000_000, 1_000_000_000);
        _publisher.BlockNextPublish();

        var first = reporter.SendAsync(Identity(), credentialRevoked: false, CancellationToken.None);
        await _publisher.WaitUntilBlockedAsync();

        // The second loop starts while the first is still mid-publish, with the baseline not yet
        // moved. Unserialised, it would compute the same transition and announce it a second time.
        var second = reporter.SendAsync(Identity(), credentialRevoked: false, CancellationToken.None);

        _publisher.ReleaseBlocked();
        await Task.WhenAll(first, second);

        _events.Should().ContainSingle(e => e.Kind == NodeEventKinds.DiskPressure);
    }

    // --- helpers ---

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        throw new TimeoutException("The condition never became true.");
    }

    private void Deploy(string containerName, string state) =>
        _runtime.Containers[containerName] = Container(containerName, state, "wl-pg");

    private static RuntimeContainer Container(string name, string state, string? workloadId)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal) { [NodeLabels.Managed] = "true" };
        if (workloadId is not null) labels[NodeLabels.Workload] = workloadId;

        return new RuntimeContainer(
            name, name, "registry.test/app:1.0.0", null, state, state, true, 0, null,
            labels, new Dictionary<int, int>(), new Dictionary<string, string>());
    }

    private async Task<ControlChannel> ConnectedChannelAsync()
    {
        _channel = new ControlChannel(
            _agent.Wrapped,
            new InMemoryTransportFactory(_pair),
            new ChannelOutbox(TestFactories.Store<OutboxState>(_agent, "outbox.json"), TestFactories.Log<ChannelOutbox>()),
            _state,
            TestFactories.Inventory(_agent, _host, _runtime),
            _clock,
            TestFactories.Log<ControlChannel>());

        _pair.PushToNode(ControlFrame.Create(ControlFrames.HelloAck, new ControlHelloAck
        {
            ProtocolVersion = NodeContract.ProtocolVersion,
            ResumeToken = "resume-1",
            ServerTime = _clock.GetUtcNow(),
            HeartbeatIntervalSeconds = 30,
            GrantedScopes = NodeScopes.Default,
        }));

        await _channel.OpenAsync(Identity(), CancellationToken.None);
        return _channel;
    }

    /// <summary>
    /// One instance per test, deliberately: the reporter is where the previous observation lives, so
    /// a fresh one every call would be a node that had just restarted before each heartbeat.
    /// </summary>
    private HeartbeatReporter Reporter(ControlChannel channel) => _reporter ??= new HeartbeatReporter(
        _agent.Wrapped, channel, _runtime, _host, _state, Manager(), Tunnels(), _metrics,
        _publisher, _clock, TestFactories.Log<HeartbeatReporter>());

    private TunnelSupervisor Tunnels() => _tunnels ??= new TunnelSupervisor(
        _agent.Wrapped, _gateway, new FakeLocalDialer(), _metrics,
        _clock, NullLoggerFactory.Instance, TestFactories.Log<TunnelSupervisor>());

    private DatabaseAccessManager Manager() => new(
        _agent.Wrapped,
        TestFactories.Store<GrantStoreState>(_agent, "grants.json"),
        _state,
        _workloads,
        new DatabaseEngineOperations(_runtime, NullLogger<DatabaseEngineOperations>.Instance),
        Tunnels(),
        _identities,
        _vault,
        _redactor,
        TestFactories.Audit(_agent, _redactor),
        _metrics,
        _publisher,
        _clock,
        TestFactories.Log<DatabaseAccessManager>());

    private NodeIdentity Identity() => _identities.Load()!;

    /// <summary>The next heartbeat frame the node put on the wire, past the handshake traffic.</summary>
    private async Task<NodeHeartbeat> NextHeartbeatAsync()
    {
        while (await _pair.NextFromNodeAsync(TimeSpan.FromSeconds(5)) is { } frame)
            if (frame.Type == NodeFrames.Heartbeat)
                return frame.PayloadAs<NodeHeartbeat>()!;

        throw new InvalidOperationException("The node sent no heartbeat.");
    }

    private static DatabaseAccessGrantSpec GrantSpec() => new()
    {
        GrantId = "gr-1",
        TenantId = "tenant-1",
        WorkloadId = "wl-pg",
        Engine = DatabaseEngines.PostgreSql,
        TargetContainer = "db",
        DatabaseName = "appdb",
        Mode = DatabaseAccessMode.Temporary,
        TtlSeconds = 3600,
        IpAllowlist = ["203.0.113.44/32"],
        Audit = new AuditMetadata { ActorName = "support@example.com", TenantId = "tenant-1" },
    };

    private void SeedDatabaseWorkload()
    {
        var spec = TestFactories.Workload(workloadId: "wl-pg", container: c =>
        {
            c.Name = "db";
            c.Secrets.Add(new SecretSpec { Name = "POSTGRES_PASSWORD", Value = "admin-password-from-panel" });
        });

        _workloads.Save(new WorkloadRecord
        {
            WorkloadId = "wl-pg",
            TenantId = "tenant-1",
            Name = "test-app",
            Spec = spec with
            {
                Containers =
                [
                    spec.Containers[0] with
                    {
                        Env = new Dictionary<string, string> { ["POSTGRES_USER"] = "harbora", ["POSTGRES_DB"] = "appdb" },
                    },
                ],
            },
            ReleaseId = "rel00001",
            SpecFingerprint = "x",
            DeployedAt = _clock.GetUtcNow(),
        });
    }

    public void Dispose()
    {
        _tunnels?.StopAllAsync().GetAwaiter().GetResult();
        _ca.Dispose();
        _agent.Dispose();
    }

    /// <summary>
    /// Collects what was published, and can be made to behave like the two ways publishing goes
    /// wrong: refusing outright (the outbox write failed) and stalling mid-publish.
    /// </summary>
    private sealed class CollectingEvents(List<NodeEvent> sink) : INodeEventPublisher
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _blockOnce;

        public bool Refuse { get; set; }

        public void BlockNextPublish() => Interlocked.Exchange(ref _blockOnce, 1);

        public Task WaitUntilBlockedAsync() => _blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public void ReleaseBlocked() => _gate.TrySetResult();

        public async Task<bool> PublishAsync(NodeEvent nodeEvent, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _blockOnce, 0) == 1)
            {
                _blocked.TrySetResult();
                await _gate.Task;
            }

            if (Refuse) return false;

            lock (sink) sink.Add(nodeEvent);
            return true;
        }
    }
}
