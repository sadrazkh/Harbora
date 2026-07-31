using Harbora.Domain.Monitoring;

namespace Harbora.Infrastructure.Monitoring;

/// <summary>
/// Turning raw samples into summaries that can be kept for a year.
///
/// Raw points live for a day. Without this, every question about last week — "was memory creeping
/// up?", "when did this start?" — has no data behind it, and those are the shape of most real
/// capacity problems.
///
/// Two traps are what make this worth writing down rather than inlining:
///
/// <list type="bullet">
/// <item>Only <b>completed</b> periods are summarised. Rolling up the hour that is still running
/// produces a number that changes and is then never corrected, because the raw samples it came from
/// have been deleted by the time anyone looks.</item>
/// <item>Combining periods keeps the extremes and weights the average by how many samples each held.
/// Averaging averages is wrong the moment two periods hold different counts, and taking the daily
/// maximum from the hourly averages loses the spike — which is the thing being looked for.</item>
/// </list>
/// </summary>
public static class MetricRollups
{
    /// <summary>Start of the hour a moment falls in.</summary>
    public static DateTimeOffset HourOf(DateTimeOffset moment) =>
        new(moment.Year, moment.Month, moment.Day, moment.Hour, 0, 0, TimeSpan.Zero);

    /// <summary>Start of the day a moment falls in.</summary>
    public static DateTimeOffset DayOf(DateTimeOffset moment) =>
        new(moment.Year, moment.Month, moment.Day, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Hourly summaries of these samples, for every hour that has finished by
    /// <paramref name="now"/>. Samples from the current hour are left alone — they are not done yet.
    /// </summary>
    public static IReadOnlyList<MetricRollup> ToHourly(IEnumerable<MonitoringMetric> samples, DateTimeOffset now)
    {
        var currentHour = HourOf(now);

        return samples
            .Where(s => HourOf(s.Timestamp) < currentHour)
            .GroupBy(s => (s.ServerId, s.Name, s.ResourceRef, Hour: HourOf(s.Timestamp)))
            .Select(g => new MetricRollup
            {
                ServerId = g.Key.ServerId,
                Name = g.Key.Name,
                ResourceRef = g.Key.ResourceRef,
                Period = RollupPeriod.Hour,
                PeriodStart = g.Key.Hour,
                Minimum = g.Min(s => s.Value),
                Maximum = g.Max(s => s.Value),
                Average = g.Average(s => s.Value),
                SampleCount = g.Count()
            })
            .OrderBy(r => r.PeriodStart)
            .ToList();
    }

    /// <summary>
    /// Daily summaries built from hourly ones, for days that have finished.
    ///
    /// The extremes come from the hourly extremes rather than the hourly averages, and the average is
    /// weighted by sample count — see the note on this class for why both matter.
    /// </summary>
    public static IReadOnlyList<MetricRollup> ToDaily(IEnumerable<MetricRollup> hourly, DateTimeOffset now)
    {
        var today = DayOf(now);

        return hourly
            .Where(h => h.Period == RollupPeriod.Hour && DayOf(h.PeriodStart) < today)
            .GroupBy(h => (h.ServerId, h.Name, h.ResourceRef, Day: DayOf(h.PeriodStart)))
            .Select(g => new MetricRollup
            {
                ServerId = g.Key.ServerId,
                Name = g.Key.Name,
                ResourceRef = g.Key.ResourceRef,
                Period = RollupPeriod.Day,
                PeriodStart = g.Key.Day,
                Minimum = g.Min(h => h.Minimum),
                Maximum = g.Max(h => h.Maximum),
                Average = WeightedAverage(g),
                SampleCount = g.Sum(h => h.SampleCount)
            })
            .OrderBy(r => r.PeriodStart)
            .ToList();
    }

    /// <summary>
    /// How long each shape of data is worth keeping. Raw points answer "what is happening now",
    /// hourly answers "what happened this month", daily answers "is this a trend".
    /// </summary>
    public static readonly TimeSpan RawRetention = TimeSpan.FromHours(24);
    public static readonly TimeSpan HourlyRetention = TimeSpan.FromDays(31);
    public static readonly TimeSpan DailyRetention = TimeSpan.FromDays(365);

    /// <summary>
    /// Which shape of data answers a question about this much time.
    ///
    /// Reading raw points for a month would return tens of thousands of rows to draw a few hundred
    /// pixels; reading daily summaries for the last hour would return one flat line.
    /// </summary>
    public static RollupPeriod? BestSourceFor(TimeSpan window) => window switch
    {
        _ when window <= RawRetention => null,          // raw samples
        _ when window <= HourlyRetention => RollupPeriod.Hour,
        _ => RollupPeriod.Day
    };

    private static double WeightedAverage(IEnumerable<MetricRollup> periods)
    {
        var counted = periods.Where(p => p.SampleCount > 0).ToList();
        var total = counted.Sum(p => p.SampleCount);

        // No counts recorded at all — old rows, or a period that somehow held nothing. A plain mean
        // is a better answer than dividing by zero.
        if (total == 0)
        {
            var all = periods.ToList();
            return all.Count == 0 ? 0 : all.Average(p => p.Average);
        }

        return counted.Sum(p => p.Average * p.SampleCount) / total;
    }
}
