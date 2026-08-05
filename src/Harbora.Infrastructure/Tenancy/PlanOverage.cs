using Harbora.Application.Abstractions;

namespace Harbora.Infrastructure.Tenancy;

/// <summary>Which limit a workspace is past.</summary>
public enum PlanResource
{
    Apps = 0,
    Services = 1,
    Memory = 2,
    Cpu = 3,
    Disk = 4
}

/// <summary>One limit a workspace has gone past, with both figures so it can be read.</summary>
public readonly record struct PlanBreach(PlanResource Resource, double Used, double Limit);

/// <summary>
/// Who a limit change is already biting.
///
/// Lowering a plan's limits takes nothing away from anybody — tenants over the new figure keep what
/// they have and simply cannot add more — so without a list of them the change has no visible
/// effect at all, and an operator sets a memory limit believing something happened.
///
/// The list checked applications, databases and CPU. Memory and disk were missing from it, which is
/// the pair an operator is most likely to be lowering: those are the two that cost money on the
/// host, and both were invisible.
/// </summary>
public static class PlanOverage
{
    /// <summary>
    /// Floating point slack for CPU. Cores are stored as doubles and summed, so a workspace running
    /// 0.1 + 0.2 cores against a 0.3 limit is over by 5.5e-17 — arithmetic, not overuse, and
    /// reporting it puts a tenant on a warning list for a limit they are exactly inside.
    /// </summary>
    private const double CpuTolerance = 1e-9;

    /// <summary>
    /// Every limit this workspace is past. Empty when it is inside all of them.
    ///
    /// A limit of zero means unlimited, as it does everywhere else on a plan, and being "over"
    /// unlimited is not a thing that can happen.
    /// </summary>
    public static IReadOnlyList<PlanBreach> For(WorkspaceUsage usage)
    {
        var breaches = new List<PlanBreach>();

        if (usage.MaxApps > 0 && usage.Apps > usage.MaxApps)
            breaches.Add(new PlanBreach(PlanResource.Apps, usage.Apps, usage.MaxApps));

        if (usage.MaxServices > 0 && usage.Services > usage.MaxServices)
            breaches.Add(new PlanBreach(PlanResource.Services, usage.Services, usage.MaxServices));

        if (usage.MaxMemoryBytes > 0 && usage.MemoryUsedBytes > usage.MaxMemoryBytes)
            breaches.Add(new PlanBreach(PlanResource.Memory, usage.MemoryUsedBytes, usage.MaxMemoryBytes));

        if (usage.MaxCpuCores > 0 && usage.CpuUsed > usage.MaxCpuCores + CpuTolerance)
            breaches.Add(new PlanBreach(PlanResource.Cpu, usage.CpuUsed, usage.MaxCpuCores));

        // Only what was measured. Volumes nobody has measured are not counted as zero and not
        // guessed at either: a tenant is not put on this list by an estimate.
        if (usage.MaxDiskBytes > 0 && usage.DiskUsedBytes > usage.MaxDiskBytes)
            breaches.Add(new PlanBreach(PlanResource.Disk, usage.DiskUsedBytes, usage.MaxDiskBytes));

        return breaches;
    }
}
