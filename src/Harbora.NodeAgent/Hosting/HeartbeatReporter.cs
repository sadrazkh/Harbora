using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Database;
using Harbora.NodeAgent.Identity;
using Harbora.NodeAgent.Inventory;
using Harbora.NodeAgent.Observability;
using Harbora.NodeAgent.Runtime;
using Harbora.NodeAgent.State;
using Harbora.NodeAgent.Transport;
using Harbora.NodeAgent.Tunnels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.NodeAgent.Hosting;

/// <summary>
/// Composes and sends one heartbeat: the node's own account of what it is doing right now.
///
/// <para>
/// Separate from <see cref="NodeAgentWorker"/> because the worker is a loop and this is a
/// measurement. The gauges here are read from the components that own the state rather than tracked
/// alongside it — a second copy of "how many grants are open" is a second thing that can be wrong,
/// and the one that ends up on the operator's screen would be the copy.
/// </para>
/// </summary>
public sealed class HeartbeatReporter(
    IOptions<NodeAgentOptions> options,
    ControlChannel channel,
    IContainerRuntime runtime,
    IHostFacts host,
    JsonFileStore<NodeState> stateStore,
    DatabaseAccessManager grants,
    TunnelSupervisor tunnels,
    NodeMetrics metrics,
    INodeEventPublisher events,
    TimeProvider clock,
    ILogger<HeartbeatReporter> log)
{
    private static readonly Dictionary<string, string> ManagedByThisNode = new() { [NodeLabels.Managed] = "true" };

    private readonly NodeAgentOptions _options = options.Value;
    private readonly NodeHealthEvaluator _health = new(host, options.Value);
    private readonly NodeConditionTracker _conditions = new();

    internal async Task SendAsync(NodeIdentity? identity, bool credentialRevoked, CancellationToken ct)
    {
        var runtimeInfo = await runtime.GetInfoAsync(ct);

        var managed = runtimeInfo.Available
            ? await runtime.ListContainersAsync(ManagedByThisNode, includeStopped: false, ct)
            : [];

        // A second call rather than counting the running half of one that includes stopped
        // containers: what the daemon considers running is the daemon's definition, and
        // runningWorkloads has been reported through that definition since v1.0.0.
        var everything = runtimeInfo.Available
            ? await runtime.ListContainersAsync(ManagedByThisNode, includeStopped: true, ct)
            : [];

        var persisted = stateStore.Load() ?? new NodeState();

        var verdict = _health.Evaluate(
            new HealthInputs
            {
                RuntimeAvailable = runtimeInfo.Available,
                Draining = persisted.Draining,
                ChannelConnected = channel.IsConnected,
                CertificateExpiresAt = identity?.NotAfter,
                CredentialRevoked = credentialRevoked,
            },
            clock.GetUtcNow());

        metrics.Health(verdict);
        metrics.RunningWorkloads(managed.Count);

        var disk = host.Disk(_options.DataDirectory);

        // Read, not remembered. The manager owns the grant store and the supervisor owns the
        // sockets, so asking them is the only way the number on the operator's screen and the
        // number on this node can be the same number.
        var activeGrants = grants.ActiveCount;
        var activeTunnels = tunnels.ActiveCount;

        await channel.SendEphemeralAsync(NodeFrames.Heartbeat, new NodeHeartbeat
        {
            NodeId = persisted.NodeId ?? "unknown",
            AgentVersion = AgentVersion.Current,
            Health = verdict.State,
            Load1 = host.Load.One,
            Load5 = host.Load.Five,
            Load15 = host.Load.Fifteen,
            FreeMemoryBytes = host.FreeMemoryBytes,
            FreeDiskBytes = disk.FreeBytes,
            RunningWorkloads = managed.Count,
            ActiveDatabaseGrants = activeGrants,
            ActiveTunnels = activeTunnels,
            Draining = persisted.Draining,
            CertificateExpiresAt = identity?.NotAfter,
        }, ct);

        if (verdict.State is NodeHealthState.Degraded or NodeHealthState.Unhealthy)
            log.LogWarning("Node health is {State}: {Reasons}.", verdict.State, string.Join("; ", verdict.Reasons));

        await AnnounceChangesAsync(verdict, identity, everything, ct);
    }

    /// <summary>
    /// Tell the control plane what changed since the last heartbeat — and only that.
    ///
    /// <para>
    /// Sent after the heartbeat, not before: an event says a number moved, and the frame carrying
    /// the number it moved to should already be on the wire when it arrives.
    /// </para>
    /// </summary>
    private async Task AnnounceChangesAsync(
        HealthVerdict verdict, NodeIdentity? identity, IReadOnlyList<RuntimeContainer> containers, CancellationToken ct)
    {
        var disk = host.Disk(_options.DataDirectory);

        var observed = _conditions.Observe(
            new NodeConditions
            {
                Health = verdict,
                CertificateExpiresAt = identity?.NotAfter,
                FreeDiskBytes = disk.FreeBytes,
                FreeMemoryBytes = host.FreeMemoryBytes,
                Load1 = host.Load.One,
                CpuCores = host.CpuCores,
                Containers = Observed(containers),
                Tunnels = tunnels.ByKey().ToDictionary(t => t.Key, t => t.Value.Status, StringComparer.Ordinal),
            },
            clock.GetUtcNow());

        foreach (var change in observed) await events.PublishAsync(change, ct);
    }

    /// <summary>
    /// The containers this node deployed, keyed by name.
    ///
    /// <para>
    /// Narrower than the managed set on purpose. The volume archiver labels its throwaway helper
    /// containers as managed too, and a busybox that ran for four seconds during a snapshot is not
    /// a state change anybody wants in the node's feed. Carrying a workload id is what separates
    /// "something the control plane deployed" from "something the agent used".
    /// </para>
    /// </summary>
    private static Dictionary<string, ContainerObservation> Observed(IReadOnlyList<RuntimeContainer> containers)
    {
        var observed = new Dictionary<string, ContainerObservation>(StringComparer.Ordinal);

        foreach (var container in containers)
            if (container.Labels.TryGetValue(NodeLabels.Workload, out var workloadId))
                observed[container.Name] = new ContainerObservation(container.State, workloadId);

        return observed;
    }
}
