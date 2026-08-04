using System.Net.Sockets;
using Harbora.Data;
using Harbora.Domain.Nodes;
using Harbora.Domain.Servers;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Nodes;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The panel's end of HTTP ingress for a node behind NAT: a port bound here per published port
/// there, and the routing decision that sends an app's traffic through it.
/// </summary>
public sealed class NodeIngressTests : IDisposable
{
    private readonly HarboraDbContext _db;
    private readonly NodeIngressRegistry _registry = TestIngress.Registry(47100, 47199);

    public NodeIngressTests() =>
        _db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("ingress-" + Guid.NewGuid()).Options);

    public void Dispose()
    {
        foreach (var binding in _registry.Bindings()) _registry.Release(binding.PanelPort);
        _db.Dispose();
    }

    // --- binding ---

    [Fact]
    public void Binding_a_node_port_yields_a_panel_port_in_the_configured_range()
    {
        var port = _registry.Bind("node-1", nodeHostPort: 32017, preferredPort: null);

        port.Should().BeInRange(47100, 47199);
        _registry.Bindings().Should().ContainSingle()
            .Which.Should().Be(("node-1", port, 32017));
    }

    [Fact]
    public void Binding_the_same_pair_twice_returns_the_same_panel_port()
    {
        var first = _registry.Bind("node-1", 32017, null);
        var second = _registry.Bind("node-1", 32017, null);

        // A retried deploy must not consume a second listener — and must not move the port a route
        // already names.
        second.Should().Be(first);
        _registry.BoundPorts.Should().Be(1);
    }

    [Fact]
    public void Two_ports_on_one_node_get_two_listeners()
    {
        var a = _registry.Bind("node-1", 32017, null);
        var b = _registry.Bind("node-1", 32018, null);

        b.Should().NotBe(a);
        _registry.BoundPorts.Should().Be(2);
    }

    /// <summary>
    /// The whole of restart recovery. Traefik's routes already name the panel port; binding a
    /// different one would leave every app on the node pointing at nothing.
    /// </summary>
    [Fact]
    public void A_previously_recorded_port_is_bound_again()
    {
        _registry.Bind("node-1", 32017, preferredPort: 47150).Should().Be(47150);
    }

    [Fact]
    public void A_preferred_port_that_is_taken_falls_back_rather_than_failing()
    {
        var held = _registry.Bind("node-1", 32017, preferredPort: 47150);

        var other = _registry.Bind("node-2", 32017, preferredPort: 47150);

        held.Should().Be(47150);
        other.Should().NotBe(47150);
        other.Should().BeInRange(47100, 47199);
    }

    [Fact]
    public void Releasing_frees_the_port_for_another_node()
    {
        var port = _registry.Bind("node-1", 32017, null);
        _registry.Release(port);

        _registry.BoundPorts.Should().Be(0);
        _registry.Bind("node-2", 32017, preferredPort: port).Should().Be(port);
    }

    [Fact]
    public void Releasing_a_port_that_was_never_bound_is_not_an_error()
    {
        var release = () => _registry.Release(47199);
        release.Should().NotThrow();
    }

    [Fact]
    public void An_exhausted_range_is_a_named_failure_rather_than_a_silent_one()
    {
        var tiny = TestIngress.Registry(47200, 47201);

        try
        {
            tiny.Bind("node-1", 1, null);
            tiny.Bind("node-1", 2, null);

            var third = () => tiny.Bind("node-1", 3, null);

            third.Should().Throw<InvalidOperationException>().WithMessage("*IngressPortStart*");
        }
        finally
        {
            foreach (var binding in tiny.Bindings()) tiny.Release(binding.PanelPort);
        }
    }

    /// <summary>
    /// A listener with no tunnel behind it refuses. That is what a proxy reads as "upstream is
    /// down"; a connection held open until it times out looks to the user like the app is hanging.
    /// </summary>
    [Fact]
    public async Task A_bound_port_with_no_tunnel_refuses_the_connection()
    {
        var port = _registry.Bind("node-1", 32017, null);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port, CancellationToken.None);

        var buffer = new byte[1];
        var read = await client.GetStream().ReadAsync(buffer, CancellationToken.None);

        read.Should().Be(0, "the listener closes a connection it has no tunnel to carry");
    }

    [Fact]
    public void A_node_is_connected_only_once_its_tunnel_attaches()
    {
        _registry.IsConnected("node-1").Should().BeFalse();

        var channel = new StubChannel();
        _registry.Attach("node-1", channel);
        _registry.IsConnected("node-1").Should().BeTrue();

        _registry.Detach("node-1", channel);
        _registry.IsConnected("node-1").Should().BeFalse();
    }

    /// <summary>
    /// A reconnect that races a disconnect: the old socket's teardown must not delete the new
    /// socket's registration, or the node would be attached and reported gone.
    /// </summary>
    [Fact]
    public void A_late_detach_from_a_replaced_tunnel_is_ignored()
    {
        var old = new StubChannel();
        var fresh = new StubChannel();

        _registry.Attach("node-1", old);
        _registry.Attach("node-1", fresh);

        _registry.Detach("node-1", old);

        _registry.IsConnected("node-1").Should().BeTrue();
    }

    [Fact]
    public void Listeners_survive_a_tunnel_dropping()
    {
        var channel = new StubChannel();
        _registry.Attach("node-1", channel);
        var port = _registry.Bind("node-1", 32017, null);

        _registry.Detach("node-1", channel);

        // Unbinding on every blip would mean rewriting every route on the node twice per blip.
        _registry.Bindings().Should().ContainSingle().Which.PanelPort.Should().Be(port);
    }

    // --- routing ---

    [Fact]
    public async Task A_direct_node_routes_straight_at_its_own_address()
    {
        var (server, _) = await NodeOnAsync(NodeIngressMode.Direct);

        var upstream = await Router().ResolveAsync(server, Guid.CreateVersion7(), 1, 32017, CancellationToken.None);

        upstream.Should().Be(new UpstreamTarget("203.0.113.9", 32017, Tunnelled: false));
        _registry.BoundPorts.Should().Be(0);
    }

    [Fact]
    public async Task A_server_with_no_node_routes_straight_at_its_own_address()
    {
        var server = new Server { Name = "legacy", Hostname = "10.0.0.5", AgentEndpoint = "https://10.0.0.5:9700" };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(CancellationToken.None);

        var upstream = await Router().ResolveAsync(server, Guid.CreateVersion7(), 1, 32017, CancellationToken.None);

        upstream.Tunnelled.Should().BeFalse();
        upstream.Host.Should().Be("10.0.0.5");
    }

    [Fact]
    public async Task A_tunnelled_node_routes_at_the_panel_and_binds_a_listener()
    {
        var (server, _) = await NodeOnAsync(NodeIngressMode.Tunnel);
        var appId = Guid.CreateVersion7();

        var upstream = await Router().ResolveAsync(server, appId, 1, 32017, CancellationToken.None);

        upstream.Tunnelled.Should().BeTrue();
        upstream.Host.Should().Be("harbora-panel");
        upstream.Port.Should().BeInRange(47100, 47199);

        _registry.Bindings().Should().ContainSingle()
            .Which.Should().Be(("node-1", upstream.Port, 32017));
    }

    /// <summary>
    /// Recorded next to the node's port, because a restart binds from this row and nothing rewrites
    /// the routes that name it.
    /// </summary>
    [Fact]
    public async Task The_panel_port_is_recorded_on_the_reservation()
    {
        var (server, _) = await NodeOnAsync(NodeIngressMode.Tunnel);
        var appId = Guid.CreateVersion7();

        var allocator = Allocator();
        var nodePort = await allocator.AllocateAsync(server.Id, appId, 1, CancellationToken.None);

        var upstream = await Router().ResolveAsync(server, appId, 1, nodePort, CancellationToken.None);

        var row = await _db.HostPortAllocations
            .FirstAsync(a => a.AppId == appId, CancellationToken.None);

        row.IngressPort.Should().Be(upstream.Port);
    }

    [Fact]
    public async Task Redeploying_the_same_deployment_keeps_the_same_panel_port()
    {
        var (server, _) = await NodeOnAsync(NodeIngressMode.Tunnel);
        var appId = Guid.CreateVersion7();

        var first = await Router().ResolveAsync(server, appId, 1, 32017, CancellationToken.None);
        var second = await Router().ResolveAsync(server, appId, 1, 32017, CancellationToken.None);

        second.Port.Should().Be(first.Port);
        _registry.BoundPorts.Should().Be(1);
    }

    // --- the reservation lifecycle ---

    [Fact]
    public async Task Releasing_a_reservation_closes_its_ingress_listener()
    {
        var (server, _) = await NodeOnAsync(NodeIngressMode.Tunnel);
        var appId = Guid.CreateVersion7();

        var allocator = Allocator();
        var nodePort = await allocator.AllocateAsync(server.Id, appId, 1, CancellationToken.None);
        await Router().ResolveAsync(server, appId, 1, nodePort, CancellationToken.None);

        _registry.BoundPorts.Should().Be(1);

        await allocator.ReleaseAsync(server.Id, appId, 1, CancellationToken.None);

        // A number freed without closing the socket would leave the panel accepting requests for a
        // container that is gone.
        _registry.BoundPorts.Should().Be(0);
    }

    [Fact]
    public async Task A_cutover_closes_the_retired_deployments_listener_and_keeps_the_live_one()
    {
        var (server, _) = await NodeOnAsync(NodeIngressMode.Tunnel);
        var appId = Guid.CreateVersion7();

        var allocator = Allocator();
        var router = Router();

        var oldPort = await allocator.AllocateAsync(server.Id, appId, 1, CancellationToken.None);
        var old = await router.ResolveAsync(server, appId, 1, oldPort, CancellationToken.None);

        var newPort = await allocator.AllocateAsync(server.Id, appId, 2, CancellationToken.None);
        var live = await router.ResolveAsync(server, appId, 2, newPort, CancellationToken.None);

        await allocator.ReleaseAllButAsync(server.Id, appId, keepDeploymentNumber: 2, CancellationToken.None);

        _registry.Bindings().Select(b => b.PanelPort).Should().BeEquivalentTo([live.Port]);
        live.Port.Should().NotBe(old.Port);
    }

    [Fact]
    public async Task Deleting_an_app_closes_every_listener_it_held()
    {
        var (server, _) = await NodeOnAsync(NodeIngressMode.Tunnel);
        var appId = Guid.CreateVersion7();

        var allocator = Allocator();
        var router = Router();

        foreach (var deployment in (int[])[1, 2])
        {
            var nodePort = await allocator.AllocateAsync(server.Id, appId, deployment, CancellationToken.None);
            await router.ResolveAsync(server, appId, deployment, nodePort, CancellationToken.None);
        }

        _registry.BoundPorts.Should().Be(2);

        await allocator.ReleaseAppAsync(appId, CancellationToken.None);

        _registry.BoundPorts.Should().Be(0);
    }

    // --- configuration ---

    [Fact]
    public void The_ingress_range_must_not_overlap_the_public_gateway_range()
    {
        var options = new NodeAgentControlPlaneOptions
        {
            GatewayPublicPortStart = 41000,
            GatewayPublicPortEnd = 41999,
            IngressPortStart = 41500,
            IngressPortEnd = 42500,
        };

        // The gateway range is published to the internet; the ingress range must not be. An overlap
        // would let an app's internal listener land on a port anyone can reach, bypassing Traefik.
        options.Validate().Should().ContainSingle().Which.Should().Contain("must not overlap");
    }

    [Fact]
    public void The_default_ranges_do_not_overlap() =>
        new NodeAgentControlPlaneOptions().Validate().Should().NotContain(p => p.Contains("overlap"));

    // --- helpers ---

    private async Task<(Server Server, Node Node)> NodeOnAsync(NodeIngressMode mode)
    {
        var server = new Server { Name = "web-01", Hostname = "203.0.113.9", AgentEndpoint = null };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(CancellationToken.None);

        var node = new Node
        {
            NodeId = "node-1",
            Name = "web-01",
            Status = NodeStatus.Online,
            Health = "healthy",
            ServerId = server.Id,
            IngressMode = mode,
        };

        _db.Nodes.Add(node);
        await _db.SaveChangesAsync(CancellationToken.None);

        return (server, node);
    }

    private HostPortAllocator Allocator() =>
        new(_db, _registry, NullLogger<HostPortAllocator>.Instance);

    private NodeIngressRouter Router() =>
        new(_db,
            _registry,
            Allocator(),
            Options.Create(new NodeAgentControlPlaneOptions { IngressPortStart = 47100, IngressPortEnd = 47199 }),
            Options.Create(new HarboraRuntimeOptions()),
            NullLogger<NodeIngressRouter>.Instance);

    private sealed class StubChannel : INodeIngressChannel
    {
        public Task ServeAsync(int nodeHostPort, TcpClient client, CancellationToken ct) => Task.CompletedTask;
    }
}
