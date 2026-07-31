using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Monitoring;

/// <summary>
/// Summarises finished periods before their raw samples are let go.
///
/// Order is the whole safety property: a period is summarised first and pruned second, never the
/// other way round. Getting that backwards loses the history silently — the charts keep working, on
/// data that is quietly missing a week.
/// </summary>
public sealed class MetricsRollupService(HarboraDbContext db, ISystemClock clock, ILogger<MetricsRollupService> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;

        await RollUpHoursAsync(now, ct);
        await RollUpDaysAsync(now, ct);
        await PruneAsync(now, ct);
    }

    private async Task RollUpHoursAsync(DateTimeOffset now, CancellationToken ct)
    {
        // Only hours that have finished and are not already summarised. Re-summarising an hour would
        // be harmless arithmetically but would double the rows, and nothing would ever remove them.
        var currentHour = MetricRollups.HourOf(now);
        var lastDone = await db.MetricRollups
            .Where(r => r.Period == RollupPeriod.Hour)
            .MaxAsync(r => (DateTimeOffset?)r.PeriodStart, ct);

        var from = lastDone?.AddHours(1) ?? DateTimeOffset.MinValue;

        var samples = await db.MonitoringMetrics
            .Where(m => m.Timestamp >= from && m.Timestamp < currentHour)
            .ToListAsync(ct);
        if (samples.Count == 0) return;

        var rollups = MetricRollups.ToHourly(samples, now);
        if (rollups.Count == 0) return;

        db.MetricRollups.AddRange(rollups);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Summarised {Count} metric-hour(s).", rollups.Count);
    }

    private async Task RollUpDaysAsync(DateTimeOffset now, CancellationToken ct)
    {
        var today = MetricRollups.DayOf(now);
        var lastDone = await db.MetricRollups
            .Where(r => r.Period == RollupPeriod.Day)
            .MaxAsync(r => (DateTimeOffset?)r.PeriodStart, ct);

        var from = lastDone?.AddDays(1) ?? DateTimeOffset.MinValue;

        var hourly = await db.MetricRollups
            .Where(r => r.Period == RollupPeriod.Hour && r.PeriodStart >= from && r.PeriodStart < today)
            .ToListAsync(ct);
        if (hourly.Count == 0) return;

        var daily = MetricRollups.ToDaily(hourly, now);
        if (daily.Count == 0) return;

        db.MetricRollups.AddRange(daily);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Summarised {Count} metric-day(s).", daily.Count);
    }

    private async Task PruneAsync(DateTimeOffset now, CancellationToken ct)
    {
        // Raw points are pruned by the collector on its own tick; this removes the summaries once
        // they are older than anyone can ask about.
        var hourlyCutoff = now - MetricRollups.HourlyRetention;
        var dailyCutoff = now - MetricRollups.DailyRetention;

        // Loaded and removed rather than a set-based delete: this is a few thousand summary rows at
        // most, unlike the raw samples, and it keeps the behaviour exercisable in tests.
        var expired = await db.MetricRollups
            .Where(r => (r.Period == RollupPeriod.Hour && r.PeriodStart < hourlyCutoff)
                        || (r.Period == RollupPeriod.Day && r.PeriodStart < dailyCutoff))
            .ToListAsync(ct);

        if (expired.Count == 0) return;

        db.MetricRollups.RemoveRange(expired);
        await db.SaveChangesAsync(ct);
    }
}
