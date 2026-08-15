using Harbora.Infrastructure.Billing;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// How an hourly rate, and the month it comes to, read on a screen.
///
/// <para>
/// One place, because the figures now appear on five: the plans page, the price matrix, the size
/// chooser, the app and database resize controls, and the bill. Each of those had — or would have
/// grown — its own local function, and the distinction they all have to keep is the one most easily
/// lost in a copy: <b>an unpriced tier is not a free one</b>. A screen that prints "0.00" for a blank
/// rate sells capacity for nothing and says so to nobody.
/// </para>
///
/// <para>
/// Bilingual by argument rather than by ambient culture, matching how the rest of these views read
/// their language. The figures themselves go through <see cref="MinorUnits.Format"/>, which is
/// invariant on purpose — see the reasoning there.
/// </para>
/// </summary>
public static class RateDisplay
{
    /// <summary>
    /// What one hour costs, as words when it is not a number.
    ///
    /// <para>
    /// Three outcomes, never two. "Not priced" is an operator's unfinished job; "free" is somebody's
    /// decision; a figure is a figure. Collapsing the first two is how a forgotten price hosts a
    /// workload for ever while every hourly tick reports success.
    /// </para>
    /// </summary>
    public static string Hourly(long? minor, bool isFa) => minor switch
    {
        null => isFa ? "قیمت‌گذاری‌نشده" : "not priced",
        0 => isFa ? "رایگان" : "free",
        var set => MinorUnits.Format(set.Value)
    };

    /// <summary>
    /// The estimated month beside it, or <c>null</c> when there is no honest estimate to print.
    ///
    /// <para>
    /// Null for an unpriced rate, and null for a rate too strange to multiply — see
    /// <see cref="MonthlyEstimate.FromHourly"/>. A caller that gets null must print nothing at all
    /// rather than a dash beside a real hourly figure, which would read as "this tier has no monthly
    /// cost".
    /// </para>
    ///
    /// <para>
    /// The "≈" is part of the string rather than left to each caller to remember. It is the whole
    /// honesty of the figure: a month is 28 to 31 days and the arithmetic assumes the workload runs
    /// for every hour of it.
    /// </para>
    /// </summary>
    public static string? Monthly(long? minor, bool isFa)
    {
        if (MonthlyEstimate.FromHourly(minor) is not { } monthly) return null;

        // A deliberately free tier gets a monthly figure of zero, and saying "≈ 0.00" about it is
        // pedantic — nothing is approximate about free. The word is the same one Hourly uses, so the
        // two columns agree.
        if (monthly == 0) return isFa ? "رایگان" : "free";

        return $"≈ {MinorUnits.Format(monthly)}";
    }

    /// <summary>
    /// The pair, as one line: "0.02 / hour · ≈ 14.60 / month".
    ///
    /// <para>
    /// For the places that have room for one string and not two elements — a summary line, a select
    /// option, a card's subtitle. The monthly half is dropped entirely when there is none, rather
    /// than leaving a trailing separator pointing at nothing.
    /// </para>
    /// </summary>
    public static string HourlyAndMonthly(long? minor, bool isFa)
    {
        var hourly = Hourly(minor, isFa);
        var monthly = Monthly(minor, isFa);

        // Nothing to add: either the rate is unset, or it is free and both halves would say so twice.
        if (monthly is null || monthly == hourly) return hourly;

        return isFa
            ? $"{hourly} در ساعت · {monthly} در ماه"
            : $"{hourly}/hour · {monthly}/month";
    }
}
