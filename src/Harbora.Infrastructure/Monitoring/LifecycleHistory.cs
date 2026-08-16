using Harbora.Data;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Monitoring;

/// <summary>
/// Answers "is it healthy and how often does it crash" over a window, from the <c>app.up</c> and
/// <c>app.restarts</c> series <see cref="MetricsCollector"/> writes.
///
/// <para>
/// Both series are ordinary gauges to <see cref="MetricRollups"/> — see <see cref="RestartDelta"/> for
/// why <c>app.restarts</c> is allowed to be one even though a restart count is a counter — so reading
/// either of them past <see cref="MetricRollups.RawRetention"/> means reading rollups instead of raw
/// samples, exactly as <see cref="MetricRollups.BestSourceFor"/> already decides for every other
/// series a chart draws.
/// </para>
/// <para>
/// <b>The one place the two series read differently is what the rollup's own <c>Average</c> means.</b>
/// <c>app.up</c> is 0 or 1, so the average of a period <em>is</em> the uptime fraction — no further
/// arithmetic needed. <c>app.restarts</c> is a per-tick delta, so its average is restarts-per-tick, a
/// number nobody asked for; the total that actually happened is recovered as
/// <c>Average × SampleCount</c>. Displaying the bare average here would be the exact "plausible on a
/// chart, means nothing" mistake this whole series exists to avoid.
/// </para>
/// </summary>
public sealed class LifecycleHistory(HarboraDbContext db)
{
    private const string UpMetric = "app.up";
    private const string RestartMetric = "app.restarts";

    /// <summary>
    /// The fraction of <paramref name="since"/>..<paramref name="until"/> the container was observed
    /// running, 0–100, or null when nothing was ever collected for it in the window — an app with no
    /// samples reports unknown, never a fabricated 100%.
    /// </summary>
    public async Task<double?> UptimePercentAsync(
        Guid serverId, string resourceRef, DateTimeOffset since, DateTimeOffset until, CancellationToken ct)
    {
        if (MetricRollups.BestSourceFor(until - since) is { } period)
        {
            var rollups = await db.MetricRollups.AsNoTracking()
                .Where(r => r.ServerId == serverId && r.Name == UpMetric && r.ResourceRef == resourceRef
                            && r.Period == period && r.PeriodStart >= since && r.PeriodStart < until)
                .Select(r => new { r.Average, r.SampleCount })
                .ToListAsync(ct);

            if (rollups.Count == 0) return null;

            // Weighted by sample count for the same reason MetricRollups.ToDaily is: a hundred ticks
            // and ten ticks do not deserve equal say in the average just because each period holds one row.
            var totalSamples = rollups.Sum(r => r.SampleCount);
            return totalSamples == 0
                ? rollups.Average(r => r.Average) * 100
                : rollups.Sum(r => r.Average * r.SampleCount) / totalSamples * 100;
        }

        var raw = await db.MonitoringMetrics.AsNoTracking()
            .Where(m => m.ServerId == serverId && m.Name == UpMetric && m.ResourceRef == resourceRef
                        && m.Timestamp >= since && m.Timestamp < until)
            .Select(m => m.Value)
            .ToListAsync(ct);

        return raw.Count == 0 ? null : raw.Average() * 100;
    }

    /// <summary>
    /// Restarts attributed to <paramref name="since"/>..<paramref name="until"/>, or null when the
    /// container was never observed in the window at all. Zero is a real, measured answer — an app
    /// that was watched the whole window and never restarted reports 0, not unknown — so this shares
    /// its "was anything ever collected" gate with <see cref="UptimePercentAsync"/> rather than
    /// inferring it from the restart series alone, which would conflate "never restarted" with
    /// "never watched".
    /// </summary>
    public async Task<int?> RestartCountAsync(
        Guid serverId, string resourceRef, DateTimeOffset since, DateTimeOffset until, CancellationToken ct)
    {
        if (await UptimePercentAsync(serverId, resourceRef, since, until, ct) is null) return null;

        if (MetricRollups.BestSourceFor(until - since) is { } period)
        {
            var rollups = await db.MetricRollups.AsNoTracking()
                .Where(r => r.ServerId == serverId && r.Name == RestartMetric && r.ResourceRef == resourceRef
                            && r.Period == period && r.PeriodStart >= since && r.PeriodStart < until)
                .Select(r => new { r.Average, r.SampleCount })
                .ToListAsync(ct);

            // Average × SampleCount, never Average alone — see the type doc above.
            return (int)Math.Round(rollups.Sum(r => r.Average * r.SampleCount));
        }

        var sum = await db.MonitoringMetrics.AsNoTracking()
            .Where(m => m.ServerId == serverId && m.Name == RestartMetric && m.ResourceRef == resourceRef
                        && m.Timestamp >= since && m.Timestamp < until)
            .SumAsync(m => (double?)m.Value, ct) ?? 0;

        return (int)Math.Round(sum);
    }
}
