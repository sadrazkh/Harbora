using FluentAssertions;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Runtime;
using Harbora.NodeAgent.State;
using Harbora.NodeAgent.Tests.Fakes;
using Harbora.NodeAgent.Tunnels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.NodeAgent.Tests;

/// <summary>
/// HTTP ingress for a node behind NAT: the node dials out, and the panel sends requests back down
/// that connection.
///
/// <para>
/// The interesting half is not the plumbing — that is the database tunnel's, already tested — but
/// what the gateway is allowed to name. An <c>open</c> on an ingress tunnel carries a port, and if
/// the node took that at face value the tunnel would be a port-forward into the customer's private
/// network. Most of what follows is about that one decision.
/// </para>
/// </summary>
public sealed class IngressTargetTests : IDisposable
{
    private readonly TempAgent _agent = new();
    private readonly WorkloadRegistry _registry;

    public IngressTargetTests() =>
        _registry = new WorkloadRegistry(TestFactories.Store<WorkloadRegistryState>(_agent, "workloads.json"));

    public void Dispose() => _agent.Dispose();

    private PublishedPortTargetResolver Resolver() =>
        new(_registry, NullLogger<PublishedPortTargetResolver>.Instance);

    [Fact]
    public void A_published_port_resolves_to_loopback_on_this_node()
    {
        Publish("shop", ("app:8080", 32017));

        var target = Resolver().Resolve(TunnelFraming.EncodeTarget(32017));

        target.Should().NotBeNull();
        target!.Port.Should().Be(32017);
        // Always loopback. A published port is bound on every interface, but dialling it by address
        // means the resolver cannot be talked into reaching another machine on the LAN.
        target.Host.Should().Be("127.0.0.1");
    }

    /// <summary>
    /// The whole point. If this returned a target, the control plane could reach anything the node
    /// can — sshd, a metrics endpoint, another box on the customer's network.
    /// </summary>
    [Theory]
    [InlineData(22)]
    [InlineData(2375)]     // an exposed Docker daemon
    [InlineData(5432)]     // a database that belongs to somebody else
    [InlineData(9464)]     // the agent's own metrics endpoint
    public void An_unpublished_port_resolves_to_nothing(int port)
    {
        Publish("shop", ("app:8080", 32017));

        Resolver().Resolve(TunnelFraming.EncodeTarget(port)).Should().BeNull();
    }

    [Fact]
    public void A_port_stops_resolving_once_its_workload_is_gone()
    {
        Publish("shop", ("app:8080", 32017));
        Resolver().Resolve(TunnelFraming.EncodeTarget(32017)).Should().NotBeNull();

        _registry.Remove("shop");

        // No withdrawal step to forget: the reachable set is derived from what is deployed.
        Resolver().Resolve(TunnelFraming.EncodeTarget(32017)).Should().BeNull();
    }

    [Fact]
    public void An_open_that_names_no_port_resolves_to_nothing()
    {
        Publish("shop", ("app:8080", 32017));

        Resolver().Resolve(ReadOnlySpan<byte>.Empty).Should().BeNull();
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0 })]          // too short
    [InlineData(new byte[] { 0, 0, 0, 0, 0 })]    // too long
    public void A_malformed_target_resolves_to_nothing(byte[] payload) =>
        Resolver().Resolve(payload).Should().BeNull();

    [Fact]
    public void Every_published_port_on_the_node_is_reachable_not_just_the_first()
    {
        Publish("shop", ("app:8080", 32017));
        Publish("blog", ("web:3000", 32018), ("worker:9000", 32019));

        var resolver = Resolver();

        foreach (var port in (int[])[32017, 32018, 32019])
            resolver.Resolve(TunnelFraming.EncodeTarget(port)).Should().NotBeNull($"port {port} is published");
    }

    // --- framing ---

    [Theory]
    [InlineData(1)]
    [InlineData(8080)]
    [InlineData(65535)]
    public void A_target_port_round_trips(int port) =>
        TunnelFraming.DecodeTarget(TunnelFraming.EncodeTarget(port)).Should().Be(port);

    [Fact]
    public void A_target_is_four_bytes_big_endian()
    {
        // Pinned rather than inferred: the two ends are different codebases, and byte order is the
        // thing that gets read differently by one of them without anyone noticing. 0x0102 is a
        // valid port whose two halves differ, so little-endian would fail this.
        TunnelFraming.EncodeTarget(0x0102).Should().Equal([0x00, 0x00, 0x01, 0x02]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void A_port_outside_the_range_is_refused(int port)
    {
        var encode = () => TunnelFraming.EncodeTarget(port);
        encode.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_decoded_port_outside_the_range_reads_as_absent()
    {
        // The gateway is a peer, not a caller: a nonsense value on the wire is data to reject, not
        // an argument to throw over.
        TunnelFraming.DecodeTarget([0, 1, 0, 0]).Should().BeNull();
        TunnelFraming.DecodeTarget([0, 0, 0, 0]).Should().BeNull();
    }

    // --- the registration ---

    [Fact]
    public void An_ingress_registration_keys_on_ingress_not_on_a_grant()
    {
        var registration = new TunnelRegistration
        {
            NodeId = "node-1",
            TunnelId = "ingress-node-1",
            Purpose = TunnelPurpose.Ingress,
            TenantId = string.Empty,
        };

        // One per node, so a second registration replaces the first rather than stacking beside it.
        registration.Key.Should().Be(TunnelRegistration.IngressKey);
        registration.GrantId.Should().BeNull();
    }

    [Fact]
    public void A_registration_with_no_purpose_is_still_a_database_tunnel()
    {
        var registration = new TunnelRegistration
        {
            NodeId = "node-1",
            TunnelId = "tun-1",
            GrantId = "gr-1",
            TenantId = "tenant-1",
        };

        // What makes the change additive: a frame from a node that predates ingress means what it
        // always meant.
        registration.Purpose.Should().Be(TunnelPurpose.Database);
        registration.Key.Should().Be("gr-1");
    }

    [Fact]
    public void A_fixed_target_ignores_whatever_the_open_names()
    {
        var resolver = new FixedTunnelTarget(new TunnelTarget("db", 5432));

        // A database tunnel's target was settled when the grant was issued. A gateway that started
        // naming ports on one must not be able to move it.
        resolver.Resolve(TunnelFraming.EncodeTarget(22)).Should().Be(new TunnelTarget("db", 5432));
        resolver.Resolve(ReadOnlySpan<byte>.Empty).Should().Be(new TunnelTarget("db", 5432));
    }

    // --- helpers ---

    private void Publish(string name, params (string Key, int Port)[] ports) =>
        _registry.Save(new WorkloadRecord
        {
            WorkloadId = name,
            TenantId = "tenant-1",
            Name = name,
            ReleaseId = "r1",
            Spec = new WorkloadSpec { WorkloadId = name, Name = name, TenantId = "tenant-1", Containers = [] },
            AllocatedPorts = ports.ToDictionary(p => p.Key, p => p.Port),
        });
}
