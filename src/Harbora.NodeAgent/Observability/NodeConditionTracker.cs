using System.Globalization;
using System.Text.Json;
using Harbora.NodeAgent.Contracts;

namespace Harbora.NodeAgent.Observability;

/// <summary>One managed container as the runtime reported it at a heartbeat.</summary>
public readonly record struct ContainerObservation(string State, string? WorkloadId);

/// <summary>
/// Everything the node checks about itself once per heartbeat, gathered in one place so the
/// difference between two of them is a single comparison.
/// </summary>
public sealed record NodeConditions
{
    public required HealthVerdict Health { get; init; }

    /// <summary>The one host reading this heartbeat was built from. See <see cref="HostSample"/>.</summary>
    public required HostSample Host { get; init; }

    /// <summary>
    /// Which reading this is. Assigned when the host is sampled, not when the comparison is made.
    ///
    /// <para>
    /// Serialising the commit does not serialise the reading it commits: two heartbeat loops can
    /// sample the host in one order and reach the tracker in the other, and the later arrival would
    /// then compare a stale reading against a newer baseline and announce the condition it had
    /// already watched end. Ordering the readings themselves is what stops the tracker inventing a
    /// transition that never happened.
    /// </para>
    /// </summary>
    public long Sequence { get; init; }

    /// <summary>When the node's own credential stops working. A change of this value is a rotation.</summary>
    public DateTimeOffset? CertificateExpiresAt { get; init; }

    /// <summary>Container name → what it was doing. Keyed by name because that is what survives a list call.</summary>
    public IReadOnlyDictionary<string, ContainerObservation> Containers { get; init; } =
        new Dictionary<string, ContainerObservation>(StringComparer.Ordinal);

    /// <summary>Tunnel key → status. The key is the grant id, or <c>ingress</c> for the node's own.</summary>
    public IReadOnlyDictionary<string, TunnelStatus> Tunnels { get; init; } =
        new Dictionary<string, TunnelStatus>(StringComparer.Ordinal);
}

/// <summary>
/// Turns a stream of identical observations into the handful of moments worth telling the control
/// plane about.
///
/// <para>
/// Seven of the contract's event kinds describe a <em>change</em> — pressure starting, a certificate
/// nearing its end, a container falling over, a tunnel dropping. The node re-evaluates all of them
/// every thirty seconds, so publishing what it sees would put a hundred and twenty identical events
/// an hour into the feed an operator opens to find out what happened. Publishing only the edges
/// makes that feed a history instead of a gauge; the gauge is the heartbeat, and it is already sent.
/// </para>
///
/// <para>
/// <b>Computing and committing are separate on purpose.</b> <see cref="Changes"/> says what has
/// happened; only <see cref="Accept"/> records it as told. A transition whose event never reached
/// the outbox is therefore reported again on the next heartbeat rather than being forgotten,
/// because a condition the control plane was never told about has not been announced no matter what
/// this object believes. The cost is a duplicate when some of a batch got away and some did not,
/// which is the same at-least-once bargain the channel itself makes.
/// </para>
///
/// <para>
/// The previous observation lives in memory and nowhere else. The first observation after a process
/// start establishes a baseline and announces nothing: whatever was true before the restart had
/// already been published, and the durable outbox is what carried it across the outage.
/// </para>
///
/// <para>
/// Each method is individually safe to call from any thread. A caller that needs the
/// compute-publish-commit sequence to be atomic — and one that can be entered twice must — has to
/// serialise it itself, because the publishing in the middle is asynchronous and cannot be done
/// under a lock. <c>HeartbeatReporter</c> does exactly that.
/// </para>
/// </summary>
public sealed class NodeConditionTracker
{
    private readonly Lock _gate = new();
    private NodeConditions? _previous;

    /// <summary>What has changed since the last accepted observation. Records nothing.</summary>
    public IReadOnlyList<NodeEvent> Changes(NodeConditions current, DateTimeOffset at)
    {
        NodeConditions? previous;
        lock (_gate) previous = _previous;

        if (previous is null) return [];

        // A reading older than the one already accepted describes a node that has since moved on.
        // Comparing it would announce a condition whose end has already been reported.
        if (current.Sequence < previous.Sequence) return [];

        var events = new List<NodeEvent>();

        if (previous.Health.DiskPressure != current.Health.DiskPressure)
            events.Add(Edge(
                NodeEventKinds.DiskPressure, current.Health.DiskPressure, at,
                $"This node is low on disk: {NodeHealthEvaluator.FormatBytes(current.Host.Disk.FreeBytes)} free.",
                "Disk pressure on this node has cleared.",
                ("freeBytes", Number(current.Host.Disk.FreeBytes)),
                ("totalBytes", Number(current.Host.Disk.TotalBytes))));

        if (previous.Health.MemoryPressure != current.Health.MemoryPressure)
            events.Add(Edge(
                NodeEventKinds.MemoryPressure, current.Health.MemoryPressure, at,
                $"This node is low on memory: {NodeHealthEvaluator.FormatBytes(current.Host.FreeMemoryBytes)} free.",
                "Memory pressure on this node has cleared.",
                ("freeBytes", Number(current.Host.FreeMemoryBytes)),
                ("totalBytes", Number(current.Host.TotalMemoryBytes))));

        if (previous.Health.CpuPressure != current.Health.CpuPressure)
            events.Add(Edge(
                NodeEventKinds.CpuPressure, current.Health.CpuPressure, at,
                $"This node is saturated: load {Load(current.Host.Load.One)} across {current.Host.CpuCores} core(s).",
                "CPU pressure on this node has cleared.",
                ("load1", Load(current.Host.Load.One)),
                ("cores", Number(current.Host.CpuCores))));

        if (previous.Health.CertificateExpiringSoon != current.Health.CertificateExpiringSoon)
            events.Add(Edge(
                NodeEventKinds.CertificateExpiring, current.Health.CertificateExpiringSoon, at,
                $"This node's credential expires {current.CertificateExpiresAt:u}.",
                "This node's credential is no longer close to expiry.",
                ("expiresAt", $"{current.CertificateExpiresAt:u}")));

        Rotation(events, at, previous.CertificateExpiresAt, current.CertificateExpiresAt);
        Containers(events, at, previous, current);
        Tunnels(events, at, previous, current);

        return events;
    }

    /// <summary>
    /// Record an observation as told. Only a caller that got every one of its events away calls
    /// this; anyone else leaves the baseline where it was, so the next heartbeat says it again.
    /// </summary>
    public void Accept(NodeConditions current)
    {
        lock (_gate)
            // Never backwards. A stale reading announced nothing, so it has no baseline to leave.
            if (_previous is null || current.Sequence >= _previous.Sequence)
                _previous = current;
    }

    /// <summary>
    /// A rotation is the reported expiry moving. The node does not need to be told a renewal
    /// happened: nothing else can change that date, and inferring it here means a credential
    /// replaced by any path — renewal, re-enrollment — is reported by the same rule.
    /// </summary>
    private static void Rotation(
        List<NodeEvent> events, DateTimeOffset at, DateTimeOffset? before, DateTimeOffset? after)
    {
        if (before is not { } was || after is not { } now || was == now) return;

        events.Add(new NodeEvent
        {
            Kind = NodeEventKinds.CertificateRotated,
            Message = $"This node's credential was replaced; the new one is valid until {now:u}.",
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["previous"] = $"{was:u}",
                ["expiresAt"] = $"{now:u}",
            },
            At = at,
        });
    }

    /// <summary>
    /// A container changing what it is doing, for the names present in both observations.
    ///
    /// <para>
    /// Names that appear or disappear are deliberately silent. A release id is part of a container's
    /// name, so every redeploy retires one name and creates another — reporting those would put two
    /// events in the feed for each deployment, beside the deployment event that already says what
    /// happened. What is left is the case nothing else reports: a container that fell over.
    /// </para>
    /// </summary>
    private static void Containers(
        List<NodeEvent> events, DateTimeOffset at, NodeConditions previous, NodeConditions current)
    {
        foreach (var (name, now) in current.Containers)
        {
            if (!previous.Containers.TryGetValue(name, out var was) || was.State == now.State) continue;

            events.Add(new NodeEvent
            {
                Kind = NodeEventKinds.ContainerStateChanged,
                Message = $"Container {name} went from {was.State} to {now.State}.",
                WorkloadId = now.WorkloadId,
                Data = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["container"] = name,
                    ["previous"] = was.State,
                    ["state"] = now.State,
                },
                At = at,
            });
        }
    }

    /// <summary>
    /// A tunnel changing status. A tunnel that goes away entirely says nothing here — it was closed
    /// because its grant ended, and the revocation or expiry event is that news already.
    /// </summary>
    private static void Tunnels(
        List<NodeEvent> events, DateTimeOffset at, NodeConditions previous, NodeConditions current)
    {
        foreach (var (key, now) in current.Tunnels)
        {
            if (!previous.Tunnels.TryGetValue(key, out var was) || was == now) continue;

            events.Add(new NodeEvent
            {
                Kind = NodeEventKinds.TunnelStateChanged,
                Message = $"Tunnel {key} went from {Name(was)} to {Name(now)}.",
                Data = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["tunnel"] = key,
                    ["previous"] = Name(was),
                    ["state"] = Name(now),
                },
                At = at,
            });
        }
    }

    /// <summary>
    /// An edge on a condition that is either on or off. The key is <c>transition</c> rather than
    /// <c>state</c>, because the events describing a thing that has states of its own — a container,
    /// a tunnel — use <c>state</c> for that thing's state, and one key cannot mean both.
    /// </summary>
    private static NodeEvent Edge(
        string kind, bool entered, DateTimeOffset at, string enteredMessage, string clearedMessage,
        params (string Key, string Value)[] facts)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["transition"] = entered ? "entered" : "cleared",
        };

        foreach (var (key, value) in facts) data[key] = value;

        return new NodeEvent
        {
            Kind = kind,
            Message = entered ? enteredMessage : clearedMessage,
            Data = data,
            At = at,
        };
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Load(double load) => load.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// The status as it is spelt on the wire, through the contract's own serializer rather than by
    /// re-deriving the naming policy here. Two spellings of one enum is the same class of drift this
    /// whole task exists to close.
    /// </summary>
    private static string Name(TunnelStatus status) =>
        JsonSerializer.SerializeToElement(status, NodeContract.Json).GetString()!;
}
