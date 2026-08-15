namespace Harbora.Infrastructure.Monitoring;

/// <summary>
/// The three windows a usage tab's chart control may show: 1 hour, 24 hours, 7 days.
///
/// Deliberately not a free-form picker. The retention sweeper keeps raw samples for a day and
/// hourly rollups for a month (<see cref="MetricRollups"/>) — offering a window wider than either
/// decides to hold would return an empty series that reads as "the app was idle" rather than "we do
/// not keep that". A wider window arrives with the retention change that makes it truthful, not
/// before.
/// </summary>
public static class UsageRangeWindow
{
    public const int OneHour = 60;
    public const int OneDay = 60 * 24;
    public const int OneWeek = 60 * 24 * 7;

    /// <summary>The only three values the control ever offers, in the order it draws them.</summary>
    public static readonly IReadOnlyList<int> AllowedMinutes = [OneHour, OneDay, OneWeek];

    /// <summary>
    /// An unrecognised value — missing, negative, or anything not one of the three offered windows —
    /// collapses to the hour the endpoint has always defaulted to. That keeps the control fully
    /// determinate: one of its three options is always the one drawn as selected, rather than a
    /// shared link with a stray query value leaving nothing highlighted and the chart asking the
    /// store about a window nobody offered.
    /// </summary>
    public static int Clamp(int? minutes) =>
        minutes is { } value && AllowedMinutes.Contains(value) ? value : OneHour;
}
