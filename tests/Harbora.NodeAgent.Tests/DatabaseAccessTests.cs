using System.Text;
using FluentAssertions;
using Harbora.NodeAgent.Auditing;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Database;
using Harbora.NodeAgent.Identity;
using Harbora.NodeAgent.Observability;
using Harbora.NodeAgent.Runtime;
using Harbora.NodeAgent.Security;
using Harbora.NodeAgent.State;
using Harbora.NodeAgent.Tests.Fakes;
using Harbora.NodeAgent.Tunnels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.NodeAgent.Tests;

/// <summary>
/// Section 10: temporary access that expires on its own, persistent access that has to be asked
/// for explicitly, IP allowlists, revocation, credential rotation, and recovery after a restart.
/// </summary>
public sealed class DatabaseAccessTests : IDisposable
{
    private readonly TempAgent _agent = new(o => o.TunnelGatewayUrl = "gw.harbora.test:8443");
    private readonly TestCertificateAuthority _ca = new();
    private readonly FakeContainerRuntime _runtime = new();
    private readonly FakeHostFacts _host = new();
    private readonly SecretRedactor _redactor = new();
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private readonly NodeIdentityStore _identities;
    private readonly WorkloadRegistry _workloads;
    private readonly LocalSecretVault _vault;
    private readonly StubTunnels _tunnels = new();
    private readonly List<NodeEvent> _events = [];

    public DatabaseAccessTests()
    {
        _identities = new NodeIdentityStore(_agent.Options.IdentityDirectory);
        var csr = _identities.CreateSigningRequest("test-node", newKey: true);
        _identities.StoreCertificate(_ca.Sign(csr, _clock.GetUtcNow(), _clock.GetUtcNow().AddDays(90)), _ca.CertificatePem);

        _workloads = new WorkloadRegistry(TestFactories.Store<WorkloadRegistryState>(_agent, "workloads.json"));
        _vault = new LocalSecretVault(_identities);

        TestFactories.Store<NodeState>(_agent, "node.json").Save(TestFactories.EnrolledState());

        SeedDatabaseWorkload();
    }

    /// <summary>A deployed PostgreSQL whose admin password is in its own spec, as a real one is.</summary>
    private void SeedDatabaseWorkload()
    {
        var spec = TestFactories.Workload(workloadId: "wl-pg", container: c =>
        {
            c.Name = "db";
            c.Secrets.Add(new SecretSpec { Name = "POSTGRES_PASSWORD", Value = "admin-password-from-panel" });
        });

        var withEnv = spec with
        {
            Containers =
            [
                spec.Containers[0] with
                {
                    Env = new Dictionary<string, string> { ["POSTGRES_USER"] = "harbora", ["POSTGRES_DB"] = "appdb" },
                },
            ],
        };

        _workloads.Save(new WorkloadRecord
        {
            WorkloadId = "wl-pg",
            TenantId = "tenant-1",
            Name = "test-app",
            Spec = withEnv,
            ReleaseId = "rel00001",
            SpecFingerprint = "x",
            DeployedAt = _clock.GetUtcNow(),
        });
    }

    private TunnelSupervisor? _supervisor;

    private TunnelSupervisor Tunnels() => _supervisor ??= new TunnelSupervisor(
        _agent.Wrapped, _tunnels.Gateway(), new FakeLocalDialer(), new NodeMetrics(_clock),
        _clock, NullLoggerFactory.Instance, TestFactories.Log<TunnelSupervisor>());

    private DatabaseAccessManager Manager() => new(
        _agent.Wrapped,
        TestFactories.Store<GrantStoreState>(_agent, "grants.json"),
        TestFactories.Store<NodeState>(_agent, "node.json"),
        _workloads,
        new DatabaseEngineOperations(_runtime, NullLogger<DatabaseEngineOperations>.Instance),
        Tunnels(),
        _identities,
        _vault,
        _redactor,
        TestFactories.Audit(_agent, _redactor),
        new NodeMetrics(_clock),
        new CollectingEvents(_events),
        _clock,
        TestFactories.Log<DatabaseAccessManager>());

    private static DatabaseAccessGrantSpec Spec(
        DatabaseAccessMode mode = DatabaseAccessMode.Temporary,
        int? ttlSeconds = 3600,
        IReadOnlyList<string>? allowlist = null,
        bool confirmed = false,
        string engine = DatabaseEngines.PostgreSql,
        bool readOnly = false) => new()
    {
        GrantId = "gr-1",
        TenantId = "tenant-1",
        WorkloadId = "wl-pg",
        Engine = engine,
        TargetContainer = "db",
        DatabaseName = "appdb",
        Mode = mode,
        TtlSeconds = ttlSeconds,
        IpAllowlist = allowlist ?? ["203.0.113.44/32"],
        OperatorConfirmed = confirmed,
        ReadOnly = readOnly,
        Audit = new AuditMetadata { ActorName = "support@example.com", TenantId = "tenant-1", Reason = "INC-2291" },
    };

    // --- creation ---

    [Fact]
    public async Task A_temporary_grant_mints_a_credential_and_publishes_an_endpoint()
    {
        var state = await Manager().CreateAsync(Spec(), CancellationToken.None);

        state.State.Should().Be(DatabaseAccessState.Active);
        state.Username.Should().StartWith("harbora_tmp_");
        state.Password.Should().NotBeNullOrEmpty();
        state.Endpoint.Should().Be("gw.harbora.test:41000");
        state.ExpiresAt.Should().Be(_clock.GetUtcNow().AddHours(1));
    }

    [Fact]
    public async Task The_password_is_returned_once_and_never_again()
    {
        var manager = Manager();
        var created = await manager.CreateAsync(Spec(), CancellationToken.None);

        var status = manager.StatusOf("gr-1", "tenant-1")!;

        created.Password.Should().NotBeNullOrEmpty();
        status.Password.Should().BeNull("a status read is not a credential read");
        status.Username.Should().Be(created.Username);
    }

    [Fact]
    public async Task The_stored_password_is_encrypted_at_rest()
    {
        var created = await Manager().CreateAsync(Spec(), CancellationToken.None);

        var raw = await File.ReadAllTextAsync(
            Path.Combine(_agent.Options.StateDirectory, "grants.json"));

        raw.Should().NotContain(created.Password!);
        raw.Should().Contain("protectedPassword");
    }

    [Fact]
    public async Task The_engine_user_is_created_through_the_engines_own_client()
    {
        await Manager().CreateAsync(Spec(), CancellationToken.None);

        var exec = _runtime.Execs.Should().ContainSingle().Subject;
        exec.Argv[0].Should().Be("psql");
        exec.Stdin.Should().Contain("CREATE ROLE");
    }

    [Fact]
    public async Task No_password_ever_appears_on_a_command_line()
    {
        // A process's command line is world-readable in /proc, so `mysql -p<secret>` publishes the
        // credential to every account on the machine.
        await Manager().CreateAsync(Spec(engine: DatabaseEngines.PostgreSql), CancellationToken.None);

        var exec = _runtime.Execs.Single();

        exec.Argv.Should().NotContain(arg => arg.Contains("admin-password-from-panel"));
        exec.Argv.Should().NotContain(arg => arg.StartsWith("-p") && arg.Length > 2);
    }

    [Fact]
    public async Task A_read_only_grant_asks_the_engine_for_read_only_privileges()
    {
        await Manager().CreateAsync(Spec(readOnly: true), CancellationToken.None);

        _runtime.Execs.Single().Stdin.Should().Contain("GRANT SELECT ON ALL TABLES");
        _runtime.Execs.Single().Stdin.Should().NotContain("ALL PRIVILEGES");
    }

    [Fact]
    public async Task The_created_grant_is_audited_with_the_actor_and_the_reason()
    {
        var audit = TestFactories.Audit(_agent, _redactor);
        await Manager().CreateAsync(Spec(), CancellationToken.None);

        var entry = audit.Read().Should().ContainSingle().Subject;
        entry.Action.Should().Be("database-access.create");
        entry.ActorName.Should().Be("support@example.com");
        entry.Reason.Should().Be("INC-2291");
        entry.Detail.Should().Contain("203.0.113.44/32");
    }

    [Fact]
    public async Task The_audit_entry_does_not_contain_the_password()
    {
        var audit = TestFactories.Audit(_agent, _redactor);
        var created = await Manager().CreateAsync(Spec(), CancellationToken.None);

        var raw = await File.ReadAllTextAsync(_agent.Options.AuditLogPath);
        raw.Should().NotContain(created.Password!);
    }

    // --- validation ---

    [Fact]
    public async Task A_persistent_grant_without_explicit_confirmation_is_refused()
    {
        // A persistent grant is an open door with a name on it. The node refusing to create one
        // nobody confirmed is the last check there is.
        var act = async () => await Manager().CreateAsync(
            Spec(DatabaseAccessMode.Persistent, ttlSeconds: null, confirmed: false), CancellationToken.None);

        (await act.Should().ThrowAsync<DatabaseAccessManager.GrantException>())
            .Which.Message.Should().Contain("explicit operator confirmation");
    }

    [Fact]
    public async Task A_persistent_grant_without_an_ip_allowlist_is_refused()
    {
        var act = async () => await Manager().CreateAsync(
            Spec(DatabaseAccessMode.Persistent, ttlSeconds: null, allowlist: [], confirmed: true),
            CancellationToken.None);

        (await act.Should().ThrowAsync<DatabaseAccessManager.GrantException>())
            .Which.Message.Should().Contain("IP allowlist");
    }

    [Fact]
    public async Task A_confirmed_persistent_grant_with_an_allowlist_is_created()
    {
        var state = await Manager().CreateAsync(
            Spec(DatabaseAccessMode.Persistent, ttlSeconds: null, confirmed: true), CancellationToken.None);

        state.State.Should().Be(DatabaseAccessState.Active);
        state.ExpiresAt.Should().BeNull();
        state.Username.Should().StartWith("harbora_ext_");
    }

    [Fact]
    public async Task A_temporary_grant_without_a_ttl_is_refused()
    {
        var act = async () => await Manager().CreateAsync(Spec(ttlSeconds: null), CancellationToken.None);

        (await act.Should().ThrowAsync<DatabaseAccessManager.GrantException>())
            .Which.Message.Should().Contain("better branding");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(60 * 60 * 24 * 30)]
    public async Task A_ttl_outside_the_permitted_range_is_refused(int ttl)
    {
        var act = async () => await Manager().CreateAsync(Spec(ttlSeconds: ttl), CancellationToken.None);

        await act.Should().ThrowAsync<DatabaseAccessManager.GrantException>();
    }

    [Theory]
    [InlineData("203.0.113.44")]
    [InlineData("203.0.113.0/24")]
    [InlineData("2001:db8::1")]
    [InlineData("2001:db8::/48")]
    public void A_real_address_or_block_is_accepted(string entry) =>
        DatabaseAccessManager.IsUsableAllowlistEntry(entry).Should().BeTrue();

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("everyone")]
    [InlineData("203.0.113.0/33")]
    [InlineData("")]
    public void An_allowlist_that_restricts_nothing_is_refused(string entry)
    {
        // "0.0.0.0/0" is a field the panel can point at while nothing is actually restricted.
        DatabaseAccessManager.IsUsableAllowlistEntry(entry).Should().BeFalse();
    }

    [Fact]
    public async Task An_unsupported_engine_is_refused()
    {
        var act = async () => await Manager().CreateAsync(Spec(engine: "cassandra"), CancellationToken.None);

        (await act.Should().ThrowAsync<DatabaseAccessManager.GrantException>())
            .Which.Code.Should().Be(NodeErrorCode.UnsupportedDatabaseEngine);
    }

    [Fact]
    public async Task A_grant_for_a_workload_that_is_not_here_is_refused()
    {
        var act = async () => await Manager().CreateAsync(
            Spec() with { WorkloadId = "wl-elsewhere" }, CancellationToken.None);

        (await act.Should().ThrowAsync<DatabaseAccessManager.GrantException>())
            .Which.Code.Should().Be(NodeErrorCode.GrantNotFound);
    }

    [Fact]
    public async Task A_grant_for_another_tenants_workload_is_refused()
    {
        var act = async () => await Manager().CreateAsync(
            Spec() with { TenantId = "tenant-b" }, CancellationToken.None);

        (await act.Should().ThrowAsync<DatabaseAccessManager.GrantException>())
            .Which.Code.Should().Be(NodeErrorCode.GrantNotFound);
    }

    // --- expiry, revocation, rotation ---

    [Fact]
    public async Task A_grant_closes_itself_when_the_ttl_runs_out()
    {
        var manager = Manager();
        await manager.CreateAsync(Spec(ttlSeconds: 900), CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(16));
        await manager.SweepAsync(CancellationToken.None);

        manager.StatusOf("gr-1", "tenant-1")!.State.Should().Be(DatabaseAccessState.Expired);
        Tunnels().StateFor("gr-1").Should().BeNull("the socket is what actually ends access");
        _runtime.Execs.Should().Contain(e => e.Stdin != null && e.Stdin.Contains("DROP ROLE"));
        _events.Should().Contain(e => e.Kind == NodeEventKinds.DatabaseGrantExpired);
    }

    [Fact]
    public async Task A_grant_that_has_not_expired_survives_a_sweep()
    {
        var manager = Manager();
        await manager.CreateAsync(Spec(ttlSeconds: 3600), CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(30));
        await manager.SweepAsync(CancellationToken.None);

        manager.StatusOf("gr-1", "tenant-1")!.State.Should().Be(DatabaseAccessState.Active);
    }

    [Fact]
    public async Task Revocation_closes_the_tunnel_before_it_drops_the_user()
    {
        // Dropping the user first would leave an open session working on some engines; the socket
        // is what actually ends access.
        var manager = Manager();
        await manager.CreateAsync(Spec(), CancellationToken.None);
        _runtime.Execs.Clear();

        var state = await manager.RevokeAsync("gr-1", "tenant-1", "no longer needed", true, CancellationToken.None);

        state.State.Should().Be(DatabaseAccessState.Revoked);
        state.RevokedReason.Should().Be("no longer needed");
        state.Endpoint.Should().BeNull();
        Tunnels().StateFor("gr-1").Should().BeNull();
        _runtime.Execs.Should().ContainSingle().Which.Stdin.Should().Contain("DROP ROLE");
    }

    [Fact]
    public async Task Revoking_a_grant_that_is_not_here_is_not_an_error_for_the_handler()
    {
        var manager = Manager();

        var act = async () => await manager.RevokeAsync("gr-nope", "tenant-1", null, true, CancellationToken.None);

        (await act.Should().ThrowAsync<DatabaseAccessManager.GrantException>())
            .Which.Code.Should().Be(NodeErrorCode.GrantNotFound);
    }

    [Fact]
    public async Task A_revoked_grant_cannot_be_rotated()
    {
        var manager = Manager();
        await manager.CreateAsync(Spec(), CancellationToken.None);
        await manager.RevokeAsync("gr-1", "tenant-1", null, true, CancellationToken.None);

        var act = async () => await manager.RotateAsync("gr-1", "tenant-1", 0, CancellationToken.None);

        (await act.Should().ThrowAsync<DatabaseAccessManager.GrantException>())
            .Which.Code.Should().Be(NodeErrorCode.GrantRevoked);
    }

    [Fact]
    public async Task Rotation_issues_a_new_password_and_keeps_the_user_and_endpoint()
    {
        var manager = Manager();
        var created = await manager.CreateAsync(Spec(), CancellationToken.None);

        var rotated = await manager.RotateAsync("gr-1", "tenant-1", 0, CancellationToken.None);

        rotated.Password.Should().NotBe(created.Password);
        rotated.Username.Should().Be(created.Username);
        rotated.Endpoint.Should().Be(created.Endpoint);
        _runtime.Execs.Last().Stdin.Should().Contain("ALTER ROLE");
    }

    // --- restart recovery ---

    [Fact]
    public async Task A_grant_that_expired_while_the_node_was_off_is_closed_on_the_way_back_up()
    {
        await Manager().CreateAsync(Spec(ttlSeconds: 900), CancellationToken.None);

        // The node is off for an hour.
        _clock.Advance(TimeSpan.FromHours(1));

        var afterRestart = Manager();
        await afterRestart.RestoreAsync(CancellationToken.None);

        afterRestart.StatusOf("gr-1", "tenant-1")!.State.Should().Be(DatabaseAccessState.Expired);
    }

    [Fact]
    public async Task A_grant_that_is_still_valid_is_republished_after_a_restart()
    {
        await Manager().CreateAsync(Spec(ttlSeconds: 3600), CancellationToken.None);
        await Tunnels().StopAllAsync();
        _tunnels.Started.Clear();

        _clock.Advance(TimeSpan.FromMinutes(5));

        var afterRestart = Manager();
        await afterRestart.RestoreAsync(CancellationToken.None);

        afterRestart.StatusOf("gr-1", "tenant-1")!.State.Should().Be(DatabaseAccessState.Active);
        _tunnels.Started.Should().Contain("gr-1");
    }

    // --- vault ---

    [Fact]
    public void The_vault_round_trips_and_rejects_tampering()
    {
        var blob = _vault.Protect("correct-horse-battery-staple");

        _vault.Unprotect(blob).Should().Be("correct-horse-battery-staple");

        var bytes = Convert.FromBase64String(blob);
        bytes[^1] ^= 0xFF;

        var act = () => _vault.Unprotect(Convert.ToBase64String(bytes));
        act.Should().Throw<System.Security.Cryptography.CryptographicException>(
            "an authenticated cipher's whole value is that a modified blob is an error, not different plaintext");
    }

    [Fact]
    public void Every_encryption_uses_a_fresh_nonce()
    {
        _vault.Protect("same-value").Should().NotBe(_vault.Protect("same-value"));
    }

    [Fact]
    public void Erasing_the_identity_makes_stored_secrets_unreadable()
    {
        // A re-enrolled node cannot read what the previous one held. That is the correct outcome.
        var blob = _vault.Protect("secret");
        _identities.Erase();

        var act = () => _vault.Unprotect(blob);
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Generated_credentials_have_no_characters_that_need_escaping()
    {
        for (var i = 0; i < 50; i++)
        {
            LocalSecretVault.GeneratePassword().Should().MatchRegex("^[A-Za-z0-9]{32}$");
            LocalSecretVault.GenerateUsername().Should().MatchRegex("^[a-z]+_[a-f0-9]{12}$");
        }
    }

    public void Dispose()
    {
        _ca.Dispose();
        _agent.Dispose();
    }

    private sealed class CollectingEvents(List<NodeEvent> sink) : INodeEventPublisher
    {
        public Task PublishAsync(NodeEvent nodeEvent, CancellationToken ct)
        {
            lock (sink) sink.Add(nodeEvent);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// The real supervisor, driven by a gateway that always accepts. The tunnel's own framing and
    /// forwarding are covered by TunnelProtocolTests; using the real supervisor here means the
    /// manager's start/stop bookkeeping is exercised rather than stubbed past.
    /// </summary>
    private sealed class StubTunnels
    {
        public List<string> Started { get; } = [];

        internal void Record(string grantId) => Started.Add(grantId);

        /// <summary>
        /// Answers the registration immediately with a published port, so
        /// <see cref="TunnelSupervisor.StartAsync"/> observes a connected tunnel.
        /// </summary>
        public ITunnelConnectionFactory Gateway() => new AlwaysConnected(this);

        private sealed class AlwaysConnected(StubTunnels owner) : ITunnelConnectionFactory
        {
            public Task<Stream> ConnectAsync(Uri gateway, NodeIdentity identity, CancellationToken ct)
            {
                var (node, remote) = DuplexStream.CreatePair();

                _ = Task.Run(async () =>
                {
                    var framer = new TunnelFramer(remote);

                    // Read the registration line, then answer it.
                    var buffer = new List<byte>();
                    var single = new byte[1];

                    while (await remote.ReadAsync(single, ct) == 1 && single[0] != (byte)'\n')
                        buffer.Add(single[0]);

                    var registration = NodeContract.Deserialize<TunnelRegistration>(
                        Encoding.UTF8.GetString(buffer.ToArray()))!;

                    var response = NodeContract.Serialize(new TunnelRegistrationResponse
                    {
                        Accepted = true,
                        PublicEndpoint = $"{gateway.Host}:41000",
                        PublicPort = 41000,
                    }) + "\n";

                    await remote.WriteAsync(Encoding.UTF8.GetBytes(response), ct);
                    await remote.FlushAsync(ct);

                    // Key rather than GrantId: an ingress registration carries no grant, so the
                    // tunnel's own name is what identifies it on both ends. For a database tunnel
                    // the two are the same string.
                    owner.Record(registration.Key);
                    _ = framer;
                }, ct);

                return Task.FromResult<Stream>(node);
            }
        }
    }
}
