using System.Text;
using FluentAssertions;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Identity;
using Harbora.NodeAgent.Runtime;
using Harbora.NodeAgent.Tests.Fakes;
using Harbora.NodeAgent.Tunnels;
using Harbora.NodeAgent.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.NodeAgent.Tests;

/// <summary>
/// Section 10's transport: the node dials out to the gateway and multiplexes every client session
/// for a grant over that one connection.
/// </summary>
public sealed class TunnelProtocolTests : IDisposable
{
    private readonly TempAgent _agent = new();
    private readonly TestCertificateAuthority _ca = new();
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private readonly FakeLocalDialer _dialer = new();

    private NodeIdentity Identity()
    {
        var store = new NodeIdentityStore(_agent.Options.IdentityDirectory);
        var csr = store.CreateSigningRequest("test-node", newKey: true);
        store.StoreCertificate(_ca.Sign(csr, _clock.GetUtcNow(), _clock.GetUtcNow().AddDays(30)), _ca.CertificatePem);
        return store.Load()!;
    }

    private static TunnelRegistration Registration() => new()
    {
        NodeId = "node-1",
        TunnelId = "tun-1",
        GrantId = "gr-1",
        TenantId = "tenant-1",
        IpAllowlist = ["203.0.113.44/32"],
        MaxConnections = 5,
    };

    // --- framing ---

    [Fact]
    public async Task Frames_round_trip_through_the_framer()
    {
        var (a, b) = DuplexStream.CreatePair();
        var writer = new TunnelFramer(a);
        var reader = new TunnelFramer(b);

        var payload = Encoding.UTF8.GetBytes("SELECT 1;");
        await writer.WriteAsync(new TunnelFrame(7, TunnelFrameType.Data, payload), CancellationToken.None);

        var frame = (await reader.ReadAsync(CancellationToken.None))!.Value;

        frame.StreamId.Should().Be(7u);
        frame.Type.Should().Be(TunnelFrameType.Data);
        Encoding.UTF8.GetString(frame.Payload.Span).Should().Be("SELECT 1;");
    }

    [Fact]
    public async Task An_empty_payload_is_a_valid_frame()
    {
        var (a, b) = DuplexStream.CreatePair();
        await new TunnelFramer(a).WriteAsync(
            new TunnelFrame(3, TunnelFrameType.Close, ReadOnlyMemory<byte>.Empty), CancellationToken.None);

        var frame = (await new TunnelFramer(b).ReadAsync(CancellationToken.None))!.Value;

        frame.Type.Should().Be(TunnelFrameType.Close);
        frame.Payload.Length.Should().Be(0);
    }

    [Fact]
    public async Task An_oversized_payload_is_refused_before_it_is_written()
    {
        var (a, _) = DuplexStream.CreatePair();
        var oversized = new byte[TunnelFramer.MaxPayloadBytes + 1];

        var act = async () => await new TunnelFramer(a).WriteAsync(
            new TunnelFrame(1, TunnelFrameType.Data, oversized), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_frame_declaring_an_absurd_length_is_rejected_rather_than_buffered()
    {
        // What keeps a hostile or broken peer from asking the node to allocate a gigabyte.
        var (a, b) = DuplexStream.CreatePair();

        var header = new byte[TunnelFramer.HeaderBytes];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, 1);
        header[4] = (byte)TunnelFrameType.Data;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(5), int.MaxValue);

        await a.WriteAsync(header);
        await a.FlushAsync();

        var act = async () => await new TunnelFramer(b).ReadAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task A_clean_close_reads_as_null_rather_than_an_error()
    {
        var (a, b) = DuplexStream.CreatePair();
        a.Dispose();

        (await new TunnelFramer(b).ReadAsync(CancellationToken.None)).Should().BeNull();
    }

    // --- registration and forwarding ---

    [Fact]
    public async Task A_tunnel_registers_then_forwards_bytes_both_ways()
    {
        var (nodeSide, gatewaySide) = DuplexStream.CreatePair();
        var tunnel = new GatewayTunnel(
            new FakeTunnelConnectionFactory(nodeSide), _dialer, _clock, NullLogger<GatewayTunnel>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var run = tunnel.RunAsync(
            new Uri("tcp://gw.harbora.test:8443"), Identity(), Registration(),
            new FixedTunnelTarget(new TunnelTarget("db", 5432)), cts.Token);

        var gateway = new TunnelFramer(gatewaySide);
        var registration = await ReadLineAsync(gatewaySide, cts.Token);

        NodeContract.Deserialize<TunnelRegistration>(registration)!.GrantId.Should().Be("gr-1");

        await WriteLineAsync(gatewaySide, NodeContract.Serialize(new TunnelRegistrationResponse
        {
            Accepted = true, PublicEndpoint = "gw.harbora.test:41823", PublicPort = 41823,
        }), cts.Token);

        // A client connects at the gateway.
        await gateway.WriteAsync(new TunnelFrame(1, TunnelFrameType.Open, ReadOnlyMemory<byte>.Empty), cts.Token);
        await gateway.WriteAsync(
            new TunnelFrame(1, TunnelFrameType.Data, Encoding.UTF8.GetBytes("ping")), cts.Token);

        await WaitUntilAsync(() => _dialer.Dialled.Count > 0, cts.Token);
        _dialer.Dialled.Single().Should().Be(("db", 5432));

        // The database answers; the node frames it back.
        var database = _dialer.LastTarget!;
        var received = new byte[4];
        await ReadExactlyAsync(database, received, cts.Token);
        Encoding.UTF8.GetString(received).Should().Be("ping");

        await database.WriteAsync(Encoding.UTF8.GetBytes("pong"), cts.Token);
        await database.FlushAsync(cts.Token);

        var back = (await gateway.ReadAsync(cts.Token))!.Value;
        back.StreamId.Should().Be(1u);
        Encoding.UTF8.GetString(back.Payload.Span).Should().Be("pong");

        tunnel.State.Status.Should().Be(TunnelStatus.Connected);
        tunnel.State.PublicEndpoint.Should().Be("gw.harbora.test:41823");

        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ContinueWith(_ => { });
    }

    [Fact]
    public async Task A_gateway_that_refuses_the_registration_ends_the_tunnel()
    {
        var (nodeSide, gatewaySide) = DuplexStream.CreatePair();
        var tunnel = new GatewayTunnel(
            new FakeTunnelConnectionFactory(nodeSide), _dialer, _clock, NullLogger<GatewayTunnel>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var run = tunnel.RunAsync(
            new Uri("tcp://gw.harbora.test:8443"), Identity(), Registration(),
            new FixedTunnelTarget(new TunnelTarget("db", 5432)), cts.Token);

        await ReadLineAsync(gatewaySide, cts.Token);

        await WriteLineAsync(gatewaySide, NodeContract.Serialize(new TunnelRegistrationResponse
        {
            Accepted = false,
            Error = NodeError.From(NodeErrorCode.TunnelRejected, "no such grant"),
        }), cts.Token);

        await run;

        tunnel.State.Status.Should().Be(TunnelStatus.Failed);
        tunnel.State.LastError!.Code.Should().Be(NodeErrorCode.TunnelRejected);
        _dialer.Dialled.Should().BeEmpty("nothing local should be touched for a rejected tunnel");
    }

    [Fact]
    public async Task A_database_that_refuses_a_connection_closes_the_stream_instead_of_hanging_the_client()
    {
        _dialer.RefuseConnections = true;

        var (nodeSide, gatewaySide) = DuplexStream.CreatePair();
        var tunnel = new GatewayTunnel(
            new FakeTunnelConnectionFactory(nodeSide), _dialer, _clock, NullLogger<GatewayTunnel>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var run = tunnel.RunAsync(
            new Uri("tcp://gw.harbora.test:8443"), Identity(), Registration(),
            new FixedTunnelTarget(new TunnelTarget("db", 5432)), cts.Token);

        var gateway = new TunnelFramer(gatewaySide);
        await ReadLineAsync(gatewaySide, cts.Token);
        await WriteLineAsync(gatewaySide, NodeContract.Serialize(
            new TunnelRegistrationResponse { Accepted = true, PublicEndpoint = "gw:41823" }), cts.Token);

        await gateway.WriteAsync(new TunnelFrame(1, TunnelFrameType.Open, ReadOnlyMemory<byte>.Empty), cts.Token);

        var frame = (await gateway.ReadAsync(cts.Token))!.Value;
        frame.Type.Should().Be(TunnelFrameType.Close);
        frame.StreamId.Should().Be(1u);

        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ContinueWith(_ => { });
    }

    [Theory]
    [InlineData("gw.harbora.test:8443", "gw.harbora.test", 8443)]
    [InlineData("tcp://gw.harbora.test:8443", "gw.harbora.test", 8443)]
    [InlineData("https://gw.harbora.test:443", "gw.harbora.test", 443)]
    public void A_gateway_address_is_accepted_in_either_form(string input, string host, int port)
    {
        var uri = TunnelSupervisor.ParseGateway(input);

        uri.Host.Should().Be(host);
        uri.Port.Should().Be(port);
    }

    [Fact]
    public void An_unusable_gateway_address_is_rejected_loudly()
    {
        var act = () => TunnelSupervisor.ParseGateway("not-an-address");
        act.Should().Throw<ArgumentException>();
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new List<byte>();
        var single = new byte[1];

        while (await stream.ReadAsync(single, ct) == 1 && single[0] != (byte)'\n') buffer.Add(single[0]);

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task WriteLineAsync(Stream stream, string line, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);
        await stream.FlushAsync(ct);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = await stream.ReadAsync(buffer[read..], ct);
            if (chunk == 0) throw new EndOfStreamException();
            read += chunk;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(20, ct);
        }
    }

    public void Dispose()
    {
        _ca.Dispose();
        _agent.Dispose();
    }
}

/// <summary>
/// Section 11: the Docker Ready App. The host's Docker socket is never shared, and the whole path
/// is gated behind its own flag plus a node-admin command.
/// </summary>
public sealed class DockerWorkspaceTests : IDisposable
{
    private readonly TempAgent _agent = new();

    private DockerWorkspaceProvisioner Provisioner(bool enabled = true)
    {
        _agent.Options.Security.AllowIsolatedDockerWorkspace = enabled;
        return TestFactories.Workspaces(_agent);
    }

    private static WorkloadSpec WorkspaceSpec(Action<TestFactories.ContainerSpecBuilder>? container = null)
    {
        var spec = TestFactories.Workload(container: container);
        return spec with { AppId = DockerWorkspaceProvisioner.AppId };
    }

    [Fact]
    public void An_ordinary_workload_is_not_treated_as_a_workspace()
    {
        DockerWorkspaceProvisioner.IsWorkspace(TestFactories.Workload()).Should().BeFalse();
        DockerWorkspaceProvisioner.IsWorkspace(WorkspaceSpec()).Should().BeTrue();
    }

    [Fact]
    public void A_workspace_is_refused_when_the_feature_flag_is_off()
    {
        var decision = Provisioner(enabled: false).Evaluate(WorkspaceSpec(), hasNodeAdminScope: true);

        decision.Allowed.Should().BeFalse();
        decision.Violations.Should().Contain(v => v.Code == NodeErrorCode.PolicyDenied);
    }

    [Fact]
    public void A_workspace_is_refused_without_node_admin_scope()
    {
        var decision = Provisioner().Evaluate(WorkspaceSpec(), hasNodeAdminScope: false);

        decision.Allowed.Should().BeFalse();
        decision.Violations.Should().Contain(v => v.Code == NodeErrorCode.Unauthorized);
    }

    [Fact]
    public void The_workspace_flag_is_separate_from_the_general_privileged_flag()
    {
        // An operator who enabled privileged mode for an internal workload has not thereby agreed
        // to run untrusted tenant code in a nested daemon.
        _agent.Options.Security.AllowPrivilegedWorkloads = true;
        _agent.Options.Security.AllowIsolatedDockerWorkspace = false;

        TestFactories.Workspaces(_agent).Evaluate(WorkspaceSpec(), hasNodeAdminScope: true)
            .Allowed.Should().BeFalse();
    }

    [Theory]
    [InlineData("/var/run/docker.sock")]
    [InlineData("/var/run/../run/docker.sock")]
    [InlineData("/var/lib/docker")]
    [InlineData("/")]
    [InlineData("/etc")]
    public void A_workspace_may_never_mount_the_hosts_docker_socket_or_system_paths(string path)
    {
        // Handing a tenant the host socket is handing them root on the machine and every other
        // tenant's containers with it; the socket has no notion of who is asking.
        var spec = WorkspaceSpec(c =>
        {
            c.Mounts.Clear();
            c.Mounts.Add(new MountSpec { VolumeName = path, MountPath = "/host" });
        });

        var decision = Provisioner().Evaluate(spec, hasNodeAdminScope: true);

        decision.Allowed.Should().BeFalse();
        decision.Violations.Should().Contain(v => v.Code == NodeErrorCode.PolicyDenied);
    }

    [Fact]
    public void A_workspace_may_not_use_host_networking_or_the_host_pid_namespace()
    {
        Provisioner().Evaluate(WorkspaceSpec(c => c.HostNetwork = true), true)
            .Allowed.Should().BeFalse();

        Provisioner().Evaluate(WorkspaceSpec(c => c.HostPidNamespace = true), true)
            .Allowed.Should().BeFalse();
    }

    [Fact]
    public void An_allowed_workspace_is_hardened_rather_than_deployed_as_asked()
    {
        var decision = Provisioner().Evaluate(WorkspaceSpec(), hasNodeAdminScope: true);

        decision.Allowed.Should().BeTrue();

        var hardened = decision.Hardened!;
        var container = hardened.Containers.Single();

        container.Privileged.Should().BeTrue("a nested daemon needs it, which is why the path is gated");
        container.HostNetwork.Should().BeFalse();
        container.HostPidNamespace.Should().BeFalse();

        container.Resources.CpuCores.Should().Be(_agent.Options.Workspace.CpuCores);
        container.Resources.MemoryBytes.Should().Be(_agent.Options.Workspace.MemoryBytes);
        container.Resources.PidsLimit.Should().Be(_agent.Options.Workspace.PidsLimit);

        container.Mounts.Should().ContainSingle()
            .Which.MountPath.Should().Be(DockerWorkspaceProvisioner.WorkspacePath);

        hardened.Networks.Should().ContainSingle().Which.Internal.Should().BeTrue();
        hardened.Labels.Should().ContainKey(Harbora.NodeAgent.Inventory.NodeLabels.Workspace);
    }

    [Fact]
    public void A_workspace_cannot_publish_a_port_on_the_customers_server()
    {
        var spec = WorkspaceSpec(c =>
        {
            c.Ports.Clear();
            c.Ports.Add(new PortMapping { ContainerPort = 2375, HostPort = 30_100, PublishToHost = true });
        });

        var hardened = Provisioner().Evaluate(spec, hasNodeAdminScope: true).Hardened!;

        hardened.Containers.Single().Ports.Should().OnlyContain(p => !p.PublishToHost && p.HostPort == null);
    }

    [Fact]
    public void The_limits_come_from_the_node_not_from_the_spec()
    {
        // A workspace that could ask for its own caps could ask for none.
        _agent.Options.Workspace.CpuCores = 1.5;
        _agent.Options.Workspace.MemoryBytes = 1024 * 1024 * 1024;

        var spec = TestFactories.Workload() with { AppId = DockerWorkspaceProvisioner.AppId };
        var greedy = spec with
        {
            Containers = [spec.Containers[0] with { Resources = new ResourceLimits { CpuCores = 64, MemoryBytes = long.MaxValue } }],
        };

        var hardened = Provisioner().Evaluate(greedy, hasNodeAdminScope: true).Hardened!;

        hardened.Containers.Single().Resources.CpuCores.Should().Be(1.5);
        hardened.Containers.Single().Resources.MemoryBytes.Should().Be(1024 * 1024 * 1024);
    }

    [Fact]
    public void Provisioning_a_workspace_is_audited()
    {
        var audit = TestFactories.Audit(_agent);
        _agent.Options.Security.AllowIsolatedDockerWorkspace = true;

        new DockerWorkspaceProvisioner(_agent.Wrapped, audit, TestFactories.Log<DockerWorkspaceProvisioner>())
            .Evaluate(WorkspaceSpec(), hasNodeAdminScope: true);

        audit.Read().Should().ContainSingle()
            .Which.Action.Should().Be("workspace.provision");
    }

    public void Dispose() => _agent.Dispose();
}
