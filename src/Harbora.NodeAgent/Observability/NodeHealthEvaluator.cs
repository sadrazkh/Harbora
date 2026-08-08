using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Inventory;

namespace Harbora.NodeAgent.Observability;

/// <summary>Inputs to the health verdict, gathered once per heartbeat.</summary>
public sealed record HealthInputs
{
    public required bool RuntimeAvailable { get; init; }
    public required bool Draining { get; init; }
    public required bool ChannelConnected { get; init; }
    public DateTimeOffset? CertificateExpiresAt { get; init; }
    public bool CredentialRevoked { get; init; }
}

/// <summary>The verdict plus the pressure flags that produced it.</summary>
public sealed record HealthVerdict(
    NodeHealthState State,
    bool DiskPressure,
    bool MemoryPressure,
    bool CpuPressure,
    bool CertificateExpiringSoon,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Turns host facts into the single health word the control plane schedules on.
///
/// <para>
/// The thresholds are deliberately conservative, and <em>degraded</em> is a real state rather than
/// a nicer word for unhealthy. A node at 92% disk can still serve every container it is running;
/// what it must not do is accept a new deploy that will pull a 2 GB image. Collapsing that into
/// "unhealthy" would evacuate a node that was fine.
/// </para>
/// </summary>
public sealed class NodeHealthEvaluator(IHostFacts host, NodeAgentOptions options)
{
    /// <summary>Below this fraction of free disk, the node stops being a good place for new work.</summary>
    public const double DiskPressureFreeRatio = 0.10;

    /// <summary>Hard floor regardless of ratio: a 2 TB disk at 5% free is still 100 GB, but a pull needs headroom now.</summary>
    public const long DiskPressureFreeBytes = 2L * 1024 * 1024 * 1024;

    public const double MemoryPressureFreeRatio = 0.05;

    /// <summary>Load per core above which the node is considered saturated.</summary>
    public const double CpuPressureLoadPerCore = 2.0;

    /// <summary>A certificate this close to expiry is worth an event, well before it stops working.</summary>
    public static readonly TimeSpan CertificateWarningWindow = TimeSpan.FromDays(7);

    public HealthVerdict Evaluate(HealthInputs inputs, DateTimeOffset now)
    {
        var reasons = new List<string>();

        var disk = host.Disk(options.DataDirectory);
        var diskPressure = disk.TotalBytes > 0 &&
                           (disk.FreeBytes < DiskPressureFreeBytes ||
                            (double)disk.FreeBytes / disk.TotalBytes < DiskPressureFreeRatio);

        var totalMemory = host.TotalMemoryBytes;
        var memoryPressure = totalMemory > 0 &&
                             (double)host.FreeMemoryBytes / totalMemory < MemoryPressureFreeRatio;

        var cores = Math.Max(1, host.CpuCores);
        var cpuPressure = host.Load.One / cores > CpuPressureLoadPerCore;

        var certificateExpiring = inputs.CertificateExpiresAt is { } expiry &&
                                  expiry - now < CertificateWarningWindow;

        if (diskPressure) reasons.Add($"disk free {FormatBytes(disk.FreeBytes)} of {FormatBytes(disk.TotalBytes)}");
        if (memoryPressure) reasons.Add($"memory free {FormatBytes(host.FreeMemoryBytes)} of {FormatBytes(totalMemory)}");
        if (cpuPressure) reasons.Add($"load {host.Load.One:0.00} across {cores} core(s)");
        if (certificateExpiring) reasons.Add($"credential expires {inputs.CertificateExpiresAt:u}");
        if (!inputs.ChannelConnected) reasons.Add("control channel disconnected");

        var state = Decide(inputs, diskPressure, memoryPressure, cpuPressure, reasons);

        return new HealthVerdict(state, diskPressure, memoryPressure, cpuPressure, certificateExpiring, reasons);
    }

    private static NodeHealthState Decide(
        HealthInputs inputs, bool disk, bool memory, bool cpu, List<string> reasons)
    {
        // Order is severity, not convenience. A revoked credential is terminal and must not be
        // masked by the node also being busy.
        if (inputs.CredentialRevoked)
        {
            reasons.Add("credential revoked; re-enroll this node");
            return NodeHealthState.Unhealthy;
        }

        if (!inputs.RuntimeAvailable)
        {
            reasons.Add("container runtime unavailable");
            return NodeHealthState.Unhealthy;
        }

        // Draining outranks pressure: the operator has already decided this node takes no work, and
        // reporting it as merely degraded would invite the scheduler to try.
        if (inputs.Draining) return NodeHealthState.Draining;

        return disk || memory || cpu ? NodeHealthState.Degraded : NodeHealthState.Healthy;
    }

    /// <summary>
    /// Shared with <see cref="NodeConditionTracker"/> on purpose: the size in a pressure event and
    /// the size in the health reason an operator sees beside it must be the same number, written the
    /// same way.
    /// </summary>
    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MiB",
        _ => $"{bytes} B",
    };
}
