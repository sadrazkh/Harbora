using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Inventory;

namespace Harbora.NodeAgent.Observability;

/// <summary>
/// One reading of the host, taken at a single moment.
///
/// <para>
/// <see cref="IHostFacts"/> re-reads <c>/proc/loadavg</c>, <c>/proc/meminfo</c> and re-stats the
/// drive on every property access, with no caching. Three readers of "free disk" in one heartbeat
/// were therefore three different samples, and the number in the frame, the number the verdict was
/// made on and the number a pressure event quoted could all disagree. Taking the reading once and
/// passing it down is what makes them one number.
/// </para>
/// </summary>
public sealed record HostSample(
    DiskSpace Disk,
    long FreeMemoryBytes,
    long TotalMemoryBytes,
    LoadAverage Load,
    int CpuCores)
{
    private readonly int _cpuCores = Math.Max(1, CpuCores);

    /// <summary>
    /// At least one, always. The load-per-core division has no guard of its own, and zero cores
    /// yields <c>Infinity</c> — which is not a crash but a fabricated CPU-pressure verdict, the
    /// worse of the two outcomes.
    ///
    /// <para>
    /// Clamped through a backing field rather than a property initialiser, because an initialiser
    /// only runs for the constructor: <c>with { CpuCores = 0 }</c> goes through the <c>init</c>
    /// setter and would have skipped it.
    /// </para>
    /// </summary>
    public int CpuCores
    {
        get => _cpuCores;
        init => _cpuCores = Math.Max(1, value);
    }

    public static HostSample Take(IHostFacts host, string dataDirectory) => new(
        host.Disk(dataDirectory),
        host.FreeMemoryBytes,
        host.TotalMemoryBytes,
        host.Load,
        host.CpuCores);
}

/// <summary>Inputs to the health verdict, gathered once per heartbeat.</summary>
public sealed record HealthInputs
{
    public required bool RuntimeAvailable { get; init; }
    public required bool Draining { get; init; }
    public required bool ChannelConnected { get; init; }
    public DateTimeOffset? CertificateExpiresAt { get; init; }
    public bool CredentialRevoked { get; init; }

    /// <summary>The host as it was when this heartbeat began, not as it is by the time it is read.</summary>
    public required HostSample Host { get; init; }
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
public sealed class NodeHealthEvaluator
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

        var disk = inputs.Host.Disk;
        var diskPressure = disk.TotalBytes > 0 &&
                           (disk.FreeBytes < DiskPressureFreeBytes ||
                            (double)disk.FreeBytes / disk.TotalBytes < DiskPressureFreeRatio);

        var totalMemory = inputs.Host.TotalMemoryBytes;
        var memoryPressure = totalMemory > 0 &&
                             (double)inputs.Host.FreeMemoryBytes / totalMemory < MemoryPressureFreeRatio;

        var cores = inputs.Host.CpuCores;
        var cpuPressure = inputs.Host.Load.One / cores > CpuPressureLoadPerCore;

        var certificateExpiring = inputs.CertificateExpiresAt is { } expiry &&
                                  expiry - now < CertificateWarningWindow;

        if (diskPressure) reasons.Add($"disk free {FormatBytes(disk.FreeBytes)} of {FormatBytes(disk.TotalBytes)}");
        if (memoryPressure) reasons.Add($"memory free {FormatBytes(inputs.Host.FreeMemoryBytes)} of {FormatBytes(totalMemory)}");
        if (cpuPressure) reasons.Add($"load {inputs.Host.Load.One:0.00} across {cores} core(s)");
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
