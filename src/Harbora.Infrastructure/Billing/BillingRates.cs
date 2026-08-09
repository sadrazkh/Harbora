using Harbora.Domain.Billing;
using Harbora.Domain.Tenancy;

namespace Harbora.Infrastructure.Billing;

/// <summary>
/// What one hour of one thing costs. Pure arithmetic with no database, for the same reason
/// <c>PlanOverage</c> and <c>RetentionRule</c> are: the money maths is then provable without a
/// container, and the job that calls it is left with only orchestration.
/// </summary>
public static class BillingRates
{
    private const long BytesPerGibibyte = 1024L * 1024 * 1024;

    /// <summary>
    /// The hourly rate for one workload. A size nobody has priced costs nothing rather than
    /// throwing: sizes existed before this module did, and a tick that dies on one unpriced row
    /// bills nobody at all that hour.
    /// </summary>
    public static long ForWorkload(InstanceSize size, BilledRunState state) => state switch
    {
        BilledRunState.Running => size.RunningRatePerHourMinor,
        BilledRunState.Stopped => size.StoppedRatePerHourMinor,
        _ => 0
    };

    /// <summary>
    /// Gibibytes, rounded up, because a customer holding one byte over a boundary is holding the
    /// whole next gibibyte as far as the disk is concerned.
    ///
    /// <para>
    /// Written as a division rather than <c>bytes + BytesPerGibibyte - 1</c> so an absurd figure
    /// cannot overflow. This project compiles unchecked, so that addition would not throw on a
    /// nonsense reading — it would wrap to a large negative and turn the hour's charge into a
    /// credit, which is worse than a crash because nothing reports it.
    /// </para>
    /// </summary>
    public static long GibibytesCeiling(long bytes)
    {
        // A negative byte count is a bug somewhere upstream — an unmeasured volume is null here,
        // never negative. It reads as free, because the alternative is that truncating division
        // leaves a remainder and bills a whole gibibyte for a figure nobody trusts.
        if (bytes <= 0) return 0;

        var whole = bytes / BytesPerGibibyte;
        return bytes % BytesPerGibibyte == 0 ? whole : whole + 1;
    }

    /// <summary>What an allocated volume costs for one hour.</summary>
    public static long ForVolume(long bytes, long ratePerGbHourMinor) =>
        GibibytesCeiling(bytes) * ratePerGbHourMinor;
}
