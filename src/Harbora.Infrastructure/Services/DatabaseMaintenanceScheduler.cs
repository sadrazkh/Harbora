using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// Fires due <see cref="DatabaseMaintenanceSchedule"/>s (2.3, round-2 market-gaps plan) — the same
/// cron-tick shape <c>Harbora.Modules.Backup.Infrastructure.BackupPolicyScheduler</c> already uses for
/// <c>BackupPolicy</c>, reused rather than a second scheduler stood up beside it: this reads
/// <see cref="CronSchedule"/> the same way, advances <c>NextRunAt</c> before the run is
/// even queued so a slow tick or a crash mid-tick cannot fire a schedule twice, and treats "a backup of
/// this database is running right now" as an ordinary reason to skip a tick rather than an error — the
/// exact words <c>BackupPolicyScheduler</c>'s own remarks use for the mirror-image collision.
///
/// <para>
/// <b>Runs unscoped, and must.</b> A background scope has no <c>HttpContext</c>, so
/// <c>HarboraDbContext</c> already reports itself unscoped and every tenant's schedules are visible —
/// see <see cref="TickAsync"/>'s explicit <c>IgnoreQueryFilters()</c>, which says so rather than
/// relying on that alone, the same belt-and-braces reasoning <c>DataRetentionSweeper</c> gives for
/// doing the same on every table it touches.
/// </para>
/// </summary>
public sealed class DatabaseMaintenanceScheduler(
    IServiceScopeFactory scopeFactory, ILogger<DatabaseMaintenanceScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Migrations and seeding finish first — a tick against a half-migrated schema is noise, the
        // same reasoning BackupPolicyScheduler's own startup delay gives.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<DatabaseMaintenanceService>().ReconcileAsync(stoppingToken);
        }
        catch (Exception ex) { logger.LogError(ex, "Settling interrupted maintenance runs failed."); }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "The database maintenance scheduler tick failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass over every due schedule. Public for the same reason
    /// <c>CronRunner.TickAsync</c> is: a test drives it deterministically instead of racing a
    /// background loop.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var maintenance = scope.ServiceProvider.GetRequiredService<DatabaseMaintenanceService>();
        var clock = scope.ServiceProvider.GetRequiredService<ISystemClock>();
        var now = clock.UtcNow;

        var due = await db.DatabaseMaintenanceSchedules.IgnoreQueryFilters()
            .Where(s => s.Enabled && (s.NextRunAt == null || s.NextRunAt <= now))
            .ToListAsync(ct);

        foreach (var schedule in due)
        {
            ct.ThrowIfCancellationRequested();

            // Advanced before the run is even queued — a slow queue call or a crash mid-tick must not
            // leave NextRunAt in the past, or the next tick would fire the same schedule again.
            schedule.LastRunAt = now;
            schedule.NextRunAt = NextRun(schedule, now);

            var (runId, error) = await maintenance.QueueAsync(
                schedule.ManagedServiceDatabaseId, schedule.Operation,
                DatabaseMaintenanceTrigger.Schedule, schedule.Id, ct);

            if (runId is not null)
                logger.LogInformation("Queued scheduled {Operation} for schedule {ScheduleId}.",
                    DatabaseMaintenanceSql.Label(schedule.Operation), schedule.Id);
            else
                // A backup running right now, an engine that stopped being reachable, a database
                // since deleted — none of these are the scheduler's own failure, so this schedule
                // simply waits for its next cron occurrence rather than being disabled or retried in
                // a tight loop. The same "an ordinary reason to skip a tick" BackupPolicyScheduler's
                // own remarks give the mirror-image collision.
                logger.LogWarning("Schedule {ScheduleId} did not queue: {Error}", schedule.Id, error);
        }

        if (due.Count > 0) await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// When this schedule next fires, in UTC — <c>BackupPolicyService.NextRun</c>'s own body, copied
    /// rather than shared across the module boundary that keeps the Backup module from being
    /// referenced by core Infrastructure (see <c>DependencyInjection</c>'s own project references).
    /// </summary>
    public static DateTimeOffset? NextRun(DatabaseMaintenanceSchedule schedule, DateTimeOffset afterUtc)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        if (!schedule.Enabled) return null;
        if (!CronSchedule.TryParse(schedule.Schedule, out var parsed, out _) || parsed is null) return null;

        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.Timezone);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Falls back rather than returning null: a schedule that stops firing because a tzdata
            // package changed would go quiet with nothing to show for it.
            zone = TimeZoneInfo.Utc;
        }

        var local = TimeZoneInfo.ConvertTime(afterUtc, zone);
        var next = parsed.NextOccurrence(local);
        return next?.ToUniversalTime();
    }
}
