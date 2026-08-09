using Harbora.Domain.Billing;
using Harbora.Domain.Tenancy;

namespace Harbora.Infrastructure.Billing;

/// <summary>
/// What one hour of one thing costs. Pure arithmetic with no database, for the same reason
/// <c>PlanOverage</c> and <c>RetentionRule</c> are: the money maths is then provable without a
/// container, and the job that calls it is left with only orchestration.
///
/// <para>
/// <b>A rate here is <c>long?</c>, and <c>null</c> is not zero.</b> Null says nobody has priced the
/// thing; zero says somebody priced it at nothing. They read identically on a bill and want
/// opposite responses — a zero is a line worth no money, an unset rate is an operator who has to be
/// told. Returning <c>0</c> for "unpriced" is what lets a forgotten price host a workload for ever
/// while every hourly tick reports success.
/// </para>
/// </summary>
public static class BillingRates
{
    private const long BytesPerGibibyte = 1024L * 1024 * 1024;

    /// <summary>
    /// The hourly rate for one workload, or <c>null</c> if the size has no price for that state.
    ///
    /// <para>
    /// Null rather than an exception, because a tick that dies on one unpriced row bills nobody at
    /// all that hour; and null rather than zero, because a caller has to be able to tell a free
    /// size from an unpriced one. The nullable return is the whole guard: the compiler will not let
    /// a caller spend this as a <c>long</c> without saying, in writing, what it does about null.
    /// A result record carrying <c>(bool IsSet, long Minor)</c> would leave <c>Minor</c> readable
    /// as a plain zero by anyone who forgot to check the flag, which is the bug rather than the fix.
    /// </para>
    ///
    /// <para>
    /// <see cref="BilledRunState.NotApplicable"/> is a real zero, not an unset. That arm never
    /// consults a rate column — volumes and the plan-minimum line are priced by their own rules —
    /// so there is no unanswered question to report.
    /// </para>
    /// </summary>
    public static long? ForWorkload(InstanceSize size, BilledRunState state) => state switch
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

    /// <summary>
    /// What an allocated volume costs for one hour, or <c>null</c> if the plan has no price for a
    /// gibibyte-hour.
    ///
    /// <para>
    /// An unpriced rate stays unset even when the volume is empty, where the arithmetic would come
    /// to zero anyway. Nothing times an unknown price is zero by accident, not by decision, and
    /// reporting it as a figure would hide an unpriced plan behind whichever of its volumes
    /// happened to be empty this hour — surfacing only later, once one of them filled up.
    /// </para>
    /// </summary>
    public static long? ForVolume(long bytes, long? ratePerGbHourMinor) =>
        ratePerGbHourMinor is { } rate ? GibibytesCeiling(bytes) * rate : null;
}
