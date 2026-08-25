namespace Harbora.Domain.Servers;

/// <summary>
/// Bounds and recommended starting points for a server's commitment ratios — how much of its
/// reported capacity the scheduler may hand out versus what is physically there.
///
/// <para>
/// The recommendations are exactly that: nothing here is applied automatically. <see cref="Server"/>'s
/// own property defaults are what a row actually gets; these constants are what the admin form shows
/// beside each field so a decision has somewhere to start, per the owner's instruction that the choice
/// stays theirs — "you can recommend a default, but it must be the admin's call."
/// </para>
///
/// <para>
/// The CPU and memory recommendations differ on purpose, and are not the same dial twice. CPU
/// contention just makes things slower — a generous overcommit is recoverable the moment load drops.
/// Memory exhaustion gets a process killed by the OS OOM-killer, which is not recoverable by waiting.
/// The recommended memory factor is therefore 1.0 (no overcommit beyond the headroom
/// <see cref="Server.ReservedMemoryRatio"/> already reserves) while the recommended CPU factor allows
/// real oversubscription.
/// </para>
/// </summary>
public static class ServerCapacityPolicy
{
    /// <summary>
    /// A factor at or below zero is never valid. Zero collapses a node's allocatable figure to zero,
    /// which <see cref="Harbora.Application.Abstractions.NodeCapacity.CanFit"/> reads as "unmeasured —
    /// allow everything", the opposite of what a zero was probably meant to express; a negative factor
    /// is nonsensical. Below 1 down to this floor is still a real, useful choice — deliberate
    /// undercommit, which the owner explicitly asked to keep legitimate.
    /// </summary>
    public const double MinOvercommitFactor = 0.1;

    /// <summary>
    /// Ceiling for CPU overcommit. Above 8:1 is past what even aggressive virtualization guidance
    /// treats as routine, and CPU is the more forgiving of the two — contention only queues work.
    /// </summary>
    public const double MaxCpuOvercommitFactor = 8.0;

    /// <summary>
    /// Ceiling for memory overcommit, deliberately tighter than CPU's: memory overcommit fails by
    /// OOM-kill, not by slowdown, so the same numeric ceiling would not mean the same thing twice.
    /// </summary>
    public const double MaxMemoryOvercommitFactor = 4.0;

    public const double MinReservedMemoryRatio = 0.0;

    /// <summary>
    /// Reserving 90% or more of a host as headroom leaves it unable to schedule anything meaningful —
    /// refused outright rather than accepted and silently useless.
    /// </summary>
    public const double MaxReservedMemoryRatio = 0.9;

    /// <summary>
    /// Recommended starting point for CPU: real, moderate overcommit. Container workloads are rarely
    /// all pegged at their CPU limit at once, and being wrong here costs queueing, not data.
    /// </summary>
    public const double RecommendedCpuOvercommitFactor = 2.0;

    /// <summary>
    /// Recommended starting point for memory: none, beyond the headroom <see cref="Server.ReservedMemoryRatio"/>
    /// already carves out. This is the conservative half of the CPU/memory asymmetry described on the
    /// type — memory exhaustion is the dangerous failure mode, so the suggested default takes no risk
    /// with it and leaves any overcommit to an administrator who chooses to depart from the default.
    /// </summary>
    public const double RecommendedMemoryOvercommitFactor = 1.0;

    public static bool IsValidOvercommitFactor(double factor, double max) =>
        !double.IsNaN(factor) && !double.IsInfinity(factor) && factor >= MinOvercommitFactor && factor <= max;

    public static bool IsValidReservedMemoryRatio(double ratio) =>
        !double.IsNaN(ratio) && !double.IsInfinity(ratio) && ratio >= MinReservedMemoryRatio && ratio <= MaxReservedMemoryRatio;
}
