using FluentAssertions;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Observability;
using Xunit;

namespace Harbora.NodeAgent.Tests;

/// <summary>
/// The seven event kinds the contract declared and the agent never sent.
///
/// <para>
/// Every one of them is a <em>change</em>, and the node evaluates itself every thirty seconds. A
/// node under disk pressure for a week would otherwise publish twenty thousand identical events into
/// the feed an operator reads to find out what happened — which is the same as publishing none. So
/// the rule this file exists to hold is: once on the way in, once on the way out, and silence in
/// between.
/// </para>
/// </summary>
public class NodeConditionTrackerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Expiry = Now.AddDays(60);

    private readonly NodeConditionTracker _tracker = new();

    private List<NodeEvent> Observe(NodeConditions conditions) =>
        _tracker.Observe(conditions, Now).ToList();

    // --- the baseline ---

    [Fact]
    public void The_first_observation_announces_nothing()
    {
        // A restarted agent has no memory of what it already told the control plane, and the outbox
        // is what carried those events across the outage. Re-announcing everything on the way back
        // up would double every condition in the feed after each agent update.
        Observe(Conditions(disk: true, memory: true, cpu: true, certificate: true)).Should().BeEmpty();
    }

    [Fact]
    public void An_unchanged_node_announces_nothing()
    {
        Observe(Conditions());

        Observe(Conditions()).Should().BeEmpty();
    }

    // --- pressure ---

    [Theory]
    [InlineData(NodeEventKinds.DiskPressure)]
    [InlineData(NodeEventKinds.MemoryPressure)]
    [InlineData(NodeEventKinds.CpuPressure)]
    [InlineData(NodeEventKinds.CertificateExpiring)]
    public void A_condition_is_announced_once_entering_it_and_once_leaving_it(string kind)
    {
        var raised = Raise(kind);

        Observe(Conditions());                                   // baseline, nothing to say

        var entering = Observe(raised);
        var during = Observe(raised).Concat(Observe(raised)).ToList();
        var leaving = Observe(Conditions());
        var after = Observe(Conditions());

        entering.Should().ContainSingle().Which.Kind.Should().Be(kind);
        during.Should().BeEmpty("thirty seconds later the node is in the same state, which is not news");
        leaving.Should().ContainSingle().Which.Kind.Should().Be(kind);
        after.Should().BeEmpty();
    }

    [Theory]
    [InlineData(NodeEventKinds.DiskPressure)]
    [InlineData(NodeEventKinds.MemoryPressure)]
    [InlineData(NodeEventKinds.CpuPressure)]
    [InlineData(NodeEventKinds.CertificateExpiring)]
    public void Entering_and_leaving_are_told_apart_by_the_event_itself(string kind)
    {
        Observe(Conditions());

        var entering = Observe(Raise(kind)).Single();
        var leaving = Observe(Conditions()).Single();

        // A consumer must not have to diff two events to know which way the node went.
        entering.Data!["state"].Should().Be("entered");
        leaving.Data!["state"].Should().Be("cleared");
    }

    [Fact]
    public void Disk_pressure_says_how_much_room_is_left()
    {
        Observe(Conditions());

        var entered = Observe(Conditions(disk: true) with { FreeDiskBytes = 1_610_612_736 }).Single();

        entered.Message.Should().Contain("1.5 GiB");
        entered.Data!["freeBytes"].Should().Be("1610612736");
    }

    [Fact]
    public void Cpu_pressure_says_the_load_and_the_core_count()
    {
        Observe(Conditions());

        var entered = Observe(Conditions(cpu: true) with { Load1 = 9.25, CpuCores = 4 }).Single();

        entered.Message.Should().Contain("9.25").And.Contain("4 core");
    }

    [Fact]
    public void Two_pressures_starting_at_once_are_two_events()
    {
        Observe(Conditions());

        var events = Observe(Conditions(disk: true, memory: true));

        events.Select(e => e.Kind).Should().BeEquivalentTo(
            [NodeEventKinds.DiskPressure, NodeEventKinds.MemoryPressure]);
    }

    // --- the certificate ---

    [Fact]
    public void An_expiring_credential_names_the_date_it_stops_working()
    {
        // The same certificate throughout: what moves is the clock, not the credential, so this is
        // the warning crossing its threshold rather than a rotation.
        Observe(Conditions(expiry: Now.AddDays(3)));

        var entered = Observe(Conditions(certificate: true, expiry: Now.AddDays(3))).Single();

        entered.Kind.Should().Be(NodeEventKinds.CertificateExpiring);
        entered.Message.Should().Contain("2026-08-07");
    }

    [Fact]
    public void A_replaced_credential_is_announced_as_a_rotation()
    {
        Observe(Conditions(certificate: true, expiry: Now.AddDays(3)));

        // Renewal starts early, so the new certificate arrives while the old one still works: the
        // expiry the node reports moves, and that move is the only proof a rotation happened.
        var rotated = Observe(Conditions(expiry: Now.AddDays(90)));

        rotated.Should().Contain(e => e.Kind == NodeEventKinds.CertificateRotated);
        rotated.Single(e => e.Kind == NodeEventKinds.CertificateRotated)
            .Message.Should().Contain("2026-11-02");
    }

    [Fact]
    public void A_rotation_is_announced_once_and_not_on_every_heartbeat_afterwards()
    {
        Observe(Conditions(expiry: Now.AddDays(3)));
        Observe(Conditions(expiry: Now.AddDays(90)));

        Observe(Conditions(expiry: Now.AddDays(90))).Should().BeEmpty();
    }

    [Fact]
    public void A_credential_that_has_not_moved_is_not_a_rotation()
    {
        Observe(Conditions(expiry: Expiry));

        Observe(Conditions(expiry: Expiry)).Should().BeEmpty();
    }

    [Fact]
    public void Rotating_out_of_the_warning_window_reports_both_the_rotation_and_the_all_clear()
    {
        Observe(Conditions(certificate: true, expiry: Now.AddDays(3)));

        var events = Observe(Conditions(certificate: false, expiry: Now.AddDays(90)));

        events.Select(e => e.Kind).Should().BeEquivalentTo(
            [NodeEventKinds.CertificateRotated, NodeEventKinds.CertificateExpiring]);
    }

    // --- containers ---

    [Fact]
    public void A_container_that_stops_running_is_announced_once_and_once_when_it_comes_back()
    {
        Observe(Conditions(containers: Containers(("shop-app-r1", "running"))));

        var crashed = Observe(Conditions(containers: Containers(("shop-app-r1", "exited"))));
        var still = Observe(Conditions(containers: Containers(("shop-app-r1", "exited"))));
        var recovered = Observe(Conditions(containers: Containers(("shop-app-r1", "running"))));

        crashed.Should().ContainSingle().Which.Kind.Should().Be(NodeEventKinds.ContainerStateChanged);
        still.Should().BeEmpty();
        recovered.Should().ContainSingle().Which.Kind.Should().Be(NodeEventKinds.ContainerStateChanged);
    }

    [Fact]
    public void A_container_event_names_the_workload_it_belongs_to()
    {
        Observe(Conditions(containers: Containers(("shop-app-r1", "running"))));

        var crashed = Observe(Conditions(containers: Containers(("shop-app-r1", "exited")))).Single();

        crashed.WorkloadId.Should().Be("wl-shop");
        crashed.Data!["previous"].Should().Be("running");
        crashed.Data!["state"].Should().Be("exited");
        crashed.Message.Should().Contain("shop-app-r1");
    }

    [Fact]
    public void A_container_appearing_or_disappearing_is_not_a_state_change()
    {
        // A release id is part of the container's name, so every redeploy retires one name and
        // creates another. Reporting those as state changes would put two events in the feed for
        // every deployment, next to the deployment event that already says what happened.
        Observe(Conditions(containers: Containers(("shop-app-r1", "running"))));

        var redeployed = Observe(Conditions(containers: Containers(("shop-app-r2", "running"))));

        redeployed.Should().BeEmpty();
    }

    [Fact]
    public void Each_changed_container_gets_its_own_event()
    {
        Observe(Conditions(containers: Containers(("a", "running"), ("b", "running"))));

        var events = Observe(Conditions(containers: Containers(("a", "exited"), ("b", "exited"))));

        events.Should().HaveCount(2);
        events.Should().OnlyContain(e => e.Kind == NodeEventKinds.ContainerStateChanged);
    }

    // --- tunnels ---

    [Fact]
    public void A_tunnel_that_drops_is_announced_once_and_once_when_it_returns()
    {
        Observe(Conditions(tunnels: Tunnels(("gr-1", TunnelStatus.Connected))));

        var dropped = Observe(Conditions(tunnels: Tunnels(("gr-1", TunnelStatus.Reconnecting))));
        var still = Observe(Conditions(tunnels: Tunnels(("gr-1", TunnelStatus.Reconnecting))));
        var back = Observe(Conditions(tunnels: Tunnels(("gr-1", TunnelStatus.Connected))));

        dropped.Should().ContainSingle().Which.Kind.Should().Be(NodeEventKinds.TunnelStateChanged);
        still.Should().BeEmpty();
        back.Should().ContainSingle().Which.Kind.Should().Be(NodeEventKinds.TunnelStateChanged);
    }

    [Fact]
    public void A_tunnel_event_names_the_tunnel_and_both_statuses()
    {
        Observe(Conditions(tunnels: Tunnels(("ingress", TunnelStatus.Connected))));

        var dropped = Observe(Conditions(tunnels: Tunnels(("ingress", TunnelStatus.Failed)))).Single();

        dropped.Data!["tunnel"].Should().Be("ingress");
        dropped.Data!["previous"].Should().Be("connected");
        dropped.Data!["status"].Should().Be("failed");
        dropped.Message.Should().Contain("ingress");
    }

    [Fact]
    public void A_tunnel_that_was_closed_on_purpose_stops_being_tracked_without_an_event()
    {
        // Revocation already publishes database-grant.revoked, and the socket closing is what that
        // event means. A second event saying the tunnel went away is the same news twice.
        Observe(Conditions(tunnels: Tunnels(("gr-1", TunnelStatus.Connected))));

        Observe(Conditions()).Should().BeEmpty();
    }

    // --- all seven, together ---

    [Fact]
    public void All_seven_of_the_declared_kinds_can_actually_be_produced()
    {
        // The defect this file closes was not a broken rule but an absent one: seven kinds existed
        // in the contract and no code path could emit any of them.
        Observe(Conditions(
            certificate: true, expiry: Now.AddDays(3),
            containers: Containers(("shop-app-r1", "running")),
            tunnels: Tunnels(("gr-1", TunnelStatus.Connected))));

        var produced = Observe(Conditions(
            disk: true, memory: true, cpu: true, certificate: false, expiry: Now.AddDays(90),
            containers: Containers(("shop-app-r1", "exited")),
            tunnels: Tunnels(("gr-1", TunnelStatus.Failed))));

        produced.Select(e => e.Kind).Should().Contain(
        [
            NodeEventKinds.DiskPressure,
            NodeEventKinds.MemoryPressure,
            NodeEventKinds.CpuPressure,
            NodeEventKinds.CertificateExpiring,
            NodeEventKinds.CertificateRotated,
            NodeEventKinds.ContainerStateChanged,
            NodeEventKinds.TunnelStateChanged,
        ]);
    }

    [Fact]
    public void Every_event_is_stamped_with_the_moment_it_was_observed()
    {
        Observe(Conditions());

        var later = Now.AddSeconds(30);
        var events = _tracker.Observe(Conditions(disk: true), later);

        events.Should().OnlyContain(e => e.At == later);
    }

    // --- helpers ---

    private static NodeConditions Raise(string kind) => kind switch
    {
        NodeEventKinds.DiskPressure => Conditions(disk: true),
        NodeEventKinds.MemoryPressure => Conditions(memory: true),
        NodeEventKinds.CpuPressure => Conditions(cpu: true),
        NodeEventKinds.CertificateExpiring => Conditions(certificate: true),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a boolean condition"),
    };

    private static NodeConditions Conditions(
        bool disk = false,
        bool memory = false,
        bool cpu = false,
        bool certificate = false,
        DateTimeOffset? expiry = null,
        IReadOnlyDictionary<string, ContainerObservation>? containers = null,
        IReadOnlyDictionary<string, TunnelStatus>? tunnels = null) => new()
    {
        Health = new HealthVerdict(
            disk || memory || cpu ? NodeHealthState.Degraded : NodeHealthState.Healthy,
            disk, memory, cpu, certificate, []),
        CertificateExpiresAt = expiry ?? Expiry,
        FreeDiskBytes = 4L * 1024 * 1024 * 1024,
        FreeMemoryBytes = 512L * 1024 * 1024,
        Load1 = 0.4,
        CpuCores = 4,
        Containers = containers ?? new Dictionary<string, ContainerObservation>(),
        Tunnels = tunnels ?? new Dictionary<string, TunnelStatus>(),
    };

    private static Dictionary<string, ContainerObservation> Containers(params (string Name, string State)[] containers) =>
        containers.ToDictionary(c => c.Name, c => new ContainerObservation(c.State, "wl-shop"));

    private static Dictionary<string, TunnelStatus> Tunnels(params (string Key, TunnelStatus Status)[] tunnels) =>
        tunnels.ToDictionary(t => t.Key, t => t.Status);
}
