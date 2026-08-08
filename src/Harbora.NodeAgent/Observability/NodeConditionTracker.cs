using System.Globalization;
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

    /// <summary>When the node's own credential stops working. A change of this value is a rotation.</summary>
    public DateTimeOffset? CertificateExpiresAt { get; init; }

    public long FreeDiskBytes { get; init; }
    public long FreeMemoryBytes { get; init; }
    public double Load1 { get; init; }
    public int CpuCores { get; init; }

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
/// The previous observation lives in memory and nowhere else. That is deliberate: the first
/// observation after a process start establishes a baseline and announces nothing, because whatever
/// was true before the restart had already been published, and the durable outbox is what carried it
/// across the outage. The cost is a condition that both began and ended while the agent was down,
/// which nobody could have observed anyway.
/// </para>
/// </summary>
public sealed class NodeConditionTracker
{
    private NodeConditions? _previous;

    public IReadOnlyList<NodeEvent> Observe(NodeConditions current, DateTimeOffset at)
    {
        var previous = _previous;
        _previous = current;

        if (previous is null) return [];

        var events = new List<NodeEvent>();

        Edge(events, at, NodeEventKinds.DiskPressure,
            previous.Health.DiskPressure, current.Health.DiskPressure,
            $"This node is low on disk: {NodeHealthEvaluator.FormatBytes(current.FreeDiskBytes)} free.",
            "Disk pressure on this node has cleared.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["freeBytes"] = current.FreeDiskBytes.ToString(CultureInfo.InvariantCulture),
            });

        Edge(events, at, NodeEventKinds.MemoryPressure,
            previous.Health.MemoryPressure, current.Health.MemoryPressure,
            $"This node is low on memory: {NodeHealthEvaluator.FormatBytes(current.FreeMemoryBytes)} free.",
            "Memory pressure on this node has cleared.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["freeBytes"] = current.FreeMemoryBytes.ToString(CultureInfo.InvariantCulture),
            });

        Edge(events, at, NodeEventKinds.CpuPressure,
            previous.Health.CpuPressure, current.Health.CpuPressure,
            $"This node is saturated: load {Load(current.Load1)} across {current.CpuCores} core(s).",
            "CPU pressure on this node has cleared.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["load1"] = Load(current.Load1),
                ["cores"] = current.CpuCores.ToString(CultureInfo.InvariantCulture),
            });

        Edge(events, at, NodeEventKinds.CertificateExpiring,
            previous.Health.CertificateExpiringSoon, current.Health.CertificateExpiringSoon,
            $"This node's credential expires {current.CertificateExpiresAt:u}.",
            "This node's credential is no longer close to expiry.",
            Expiry(current.CertificateExpiresAt));

        Rotation(events, at, previous.CertificateExpiresAt, current.CertificateExpiresAt);
        Containers(events, at, previous, current);
        Tunnels(events, at, previous, current);

        return events;
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
                ["previousExpiresAt"] = $"{was:u}",
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
                    ["status"] = Name(now),
                },
                At = at,
            });
        }
    }

    private static void Edge(
        List<NodeEvent> events, DateTimeOffset at, string kind,
        bool before, bool now, string entered, string cleared, Dictionary<string, string> data)
    {
        if (before == now) return;

        data["state"] = now ? "entered" : "cleared";

        events.Add(new NodeEvent
        {
            Kind = kind,
            Message = now ? entered : cleared,
            Data = data,
            At = at,
        });
    }

    private static Dictionary<string, string> Expiry(DateTimeOffset? expiresAt)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        if (expiresAt is { } when) data["expiresAt"] = $"{when:u}";
        return data;
    }

    private static string Load(double load) => load.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Name(TunnelStatus status) =>
        System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(status.ToString());
}
