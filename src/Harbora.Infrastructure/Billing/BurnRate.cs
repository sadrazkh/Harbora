namespace Harbora.Infrastructure.Billing;

/// <summary>
/// How long a balance lasts at a given hourly cost, and nothing else.
///
/// <para>
/// This is the one piece of arithmetic behind the low-balance warning's "about N more hour(s)" and
/// the wallet page's runway date, so the two are pulled from a single place rather than each carrying
/// its own copy of "balance divided by rate" that could quietly drift apart. Before this existed,
/// <c>BillingTick.FlooredHours</c> was the only place that did this division; it now delegates here,
/// so a customer reading the incident that warned them and a customer reading the wallet page a
/// minute later see numbers that agree because they came from the same line of code.
/// </para>
///
/// <para>
/// Pure — no database, no clock read internally — for the reason <see cref="BillingRates"/> and
/// <see cref="MonthlyEstimate"/> are: the money arithmetic is provable without a container.
/// </para>
/// </summary>
public static class BurnRate
{
    /// <summary>
    /// Runway hours beyond which this class refuses to name a specific date.
    ///
    /// <para>
    /// About twenty years. Not because a bigger number would overflow <see cref="DateTimeOffset"/> —
    /// it would not, at this magnitude — but because a workspace burning a handful of minor units an
    /// hour against a large balance has, in truth, no meaningful "runs out" moment at all, and naming
    /// one anyway is exactly the overconfident-surface failure this feature exists not to repeat.
    /// </para>
    /// </summary>
    public const long MaxStatableRunwayHours = 20 * 365 * 24;

    /// <summary>
    /// Whole hours <paramref name="balanceMinor"/> still covers at <paramref name="hourlyCostMinor"/>,
    /// floored — never rounded up, because a runway that promises an hour the balance does not have
    /// is worse than one that understates it.
    ///
    /// <para>
    /// <b>Null when nothing is currently costing money.</b> A hourly cost of zero or less does not
    /// mean the balance lasts forever in the sense a number could state — it means the question does
    /// not apply, the same way <see cref="MonthlyEstimate.FromHourly"/> answers null rather than
    /// zero for an unpriced rate. A caller that turned this into a very large number would be
    /// inventing a figure nothing here actually computed.
    /// </para>
    ///
    /// <para>
    /// <b>Zero, not negative, once the balance is already spent.</b> A balance at or below nothing
    /// has no hours left rather than a negative count of them — negative hours is not a runway, it is
    /// an overdraft, and this class only answers the first question.
    /// </para>
    /// </summary>
    public static long? RunwayHours(long balanceMinor, long hourlyCostMinor)
    {
        if (hourlyCostMinor <= 0) return null;
        return balanceMinor <= 0 ? 0 : balanceMinor / hourlyCostMinor;
    }

    /// <summary>
    /// The moment <see cref="RunwayHours"/> hours from <paramref name="now"/> lands on, or
    /// <c>null</c> when there is no honest date to give — either because nothing is currently costing
    /// money, or because the runway is past <see cref="MaxStatableRunwayHours"/>.
    /// </summary>
    public static DateTimeOffset? RunwayDate(DateTimeOffset now, long balanceMinor, long hourlyCostMinor)
    {
        if (RunwayHours(balanceMinor, hourlyCostMinor) is not { } hours) return null;
        return hours > MaxStatableRunwayHours ? null : now.AddHours(hours);
    }
}
