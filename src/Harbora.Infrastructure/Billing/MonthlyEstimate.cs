namespace Harbora.Infrastructure.Billing;

/// <summary>
/// What an hourly rate comes to over a month, for a customer who wants to plan rather than
/// multiply.
///
/// <para>
/// <b>It is an estimate and every caller must say so.</b> A month is 28 to 31 days, and the
/// arithmetic here also assumes the workload runs for every hour of it — which is exactly what an
/// hourly platform does not promise. The figure is rendered with a "≈" and the word "estimate"
/// beside it; a figure that presents itself as exact is the figure a customer later disputes.
/// </para>
///
/// <para>
/// Pure, no database and no clock, for the reason <see cref="BillingRates"/> and
/// <c>BillingHourPlan</c> are: the money arithmetic is then provable without a container. And one
/// helper rather than a multiplication at each call site, so there is exactly one notion of how long
/// a month is on this install.
/// </para>
/// </summary>
public static class MonthlyEstimate
{
    /// <summary>
    /// Hours in an average month: 365 × 24 ÷ 12.
    ///
    /// <para>
    /// Written as the number rather than the expression so it can be read at a glance, and asserted
    /// against the expression in a test so the two cannot drift. 730 rather than 672 (four weeks) or
    /// 744 (the longest month): a year divides into twelve of these exactly, so twelve monthly
    /// estimates add up to the year the customer will actually be charged for.
    /// </para>
    /// </summary>
    public const int HoursPerMonth = 730;

    /// <summary>
    /// The monthly figure for an hourly rate, or <c>null</c> when there is no honest one.
    ///
    /// <para>
    /// <b>Null in, null out.</b> Nothing times 730 is zero, and "≈ 0.00/month" printed beside an
    /// unpriced tier reads as a free tier — which is the one confusion every nullable rate column in
    /// this codebase exists to prevent, and it would be reintroduced here by a single <c>?? 0</c>.
    /// An unpriced tier has no monthly figure because it has no hourly one.
    /// </para>
    ///
    /// <para>
    /// <b>Zero in, zero out.</b> The other half of that distinction: somebody typed a zero on
    /// purpose, and a free tier's month really does cost nothing. That is a figure, not a gap.
    /// </para>
    ///
    /// <para>
    /// <b>A negative rate has no estimate.</b> <c>BillingHourPlan</c> drops a negative rate rather
    /// than charging it, so no such rate ever becomes money — and rendering "≈ -14.60/month" would
    /// advertise a refund the ledger will never make. There is no honest monthly figure for a rate
    /// that is itself a bug upstream.
    /// </para>
    ///
    /// <para>
    /// <b>And a rate too large to multiply has none either.</b> This project compiles unchecked, so
    /// the multiplication would not throw: it would wrap to a large negative and print a monthly
    /// credit, and only on the installs with the biggest bills. The bound is tested from both sides
    /// so it refuses what it must and nothing more.
    /// </para>
    /// </summary>
    public static long? FromHourly(long? hourlyMinor)
    {
        if (hourlyMinor is not { } hourly) return null;
        if (hourly < 0) return null;
        if (hourly > long.MaxValue / HoursPerMonth) return null;

        return hourly * HoursPerMonth;
    }
}
