using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Decides when scheduled jobs run. The run itself — and the row it leaves behind — belongs to
/// <see cref="CronJobRunner"/>, which the "run now" button uses too, so a job someone tests by hand
/// takes exactly the path it will take at 03:00.
///
/// Missed runs are not replayed. A panel that was down for a day should not wake up and fire
/// yesterday's job twenty-four times — it should run the next one on time and leave the gap visible
/// in the history.
/// </summary>
public sealed class CronRunner(IServiceScopeFactory scopeFactory, ILogger<CronRunner> logger) : BackgroundService
{
    /// <summary>Cron's own resolution is a minute, so checking more often buys nothing.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }

        // Before anything is scheduled: settle runs a restart interrupted, or they are shown as
        // still running for ever and their job can never start again.
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<CronJobRunner>().ReconcileAsync(stoppingToken);
        }
        catch (Exception ex) { logger.LogError(ex, "Settling interrupted cron runs failed."); }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Cron tick failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// One pass over every scheduled job: fire what is due, and work out when each is next due.
    /// Public because it is the whole behaviour of this service, and a rule about when a job fires
    /// is not something to find out from production a month later.
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<ISystemClock>();
        var runner = scope.ServiceProvider.GetRequiredService<CronJobRunner>();
        var now = clock.UtcNow;

        var jobs = await db.Apps
            .Include(a => a.EnvironmentVariables)
            .Where(a => a.Kind == ServiceKind.Cron
                        && a.CronExpression != null && a.CronExpression != ""
                        && a.Status != AppStatus.Stopped)
            .ToListAsync(ct);

        foreach (var job in jobs)
        {
            if (!CronSchedule.TryParse(job.CronExpression, out var schedule, out var error))
            {
                // A schedule that cannot be read is recorded once, not shouted every minute.
                if (job.NextRunAt is not null)
                {
                    job.NextRunAt = null;
                    logger.LogWarning("Cron service {Slug} has an unreadable schedule: {Error}", job.Slug, error);
                    await db.SaveChangesAsync(ct);
                }
                continue;
            }

            // First sight of this job: work out when it is next due and wait for that, rather than
            // treating "never run" as "overdue" and firing immediately.
            if (job.NextRunAt is null)
            {
                job.NextRunAt = schedule!.NextOccurrence(now);
                await db.SaveChangesAsync(ct);
                continue;
            }

            if (job.NextRunAt > now) continue;

            // Advance the schedule BEFORE running. If the run is slow, or this process dies mid-job,
            // the next tick must not start it again.
            job.NextRunAt = schedule!.NextOccurrence(now);
            await db.SaveChangesAsync(ct);

            await runner.RunAsync(job, manual: false, ct);
        }
    }
}
