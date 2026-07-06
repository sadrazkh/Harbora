using Harbora.Application.Abstractions;
using Harbora.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Backups;

/// <summary>
/// Evaluates <c>BackupSchedule</c>s on a fixed tick: any schedule whose NextRunAt is due gets a
/// backup queued, then retention is enforced. Interval-based for the MVP (cron can replace it).
/// </summary>
public sealed class BackupScheduler(IServiceScopeFactory scopeFactory, ILogger<BackupScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small startup delay so migrations/seed finish first.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try { await RunDueSchedulesAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Backup scheduler tick failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunDueSchedulesAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var engine = scope.ServiceProvider.GetRequiredService<IBackupEngine>();
        var clock = scope.ServiceProvider.GetRequiredService<ISystemClock>();
        var now = clock.UtcNow;

        var due = await db.BackupSchedules
            .Where(s => s.IsEnabled && (s.NextRunAt == null || s.NextRunAt <= now))
            .ToListAsync(ct);

        foreach (var schedule in due)
        {
            await engine.QueueBackupAsync(schedule.WorkspaceId, schedule.Type, schedule.TargetRef, schedule.DestinationId, scheduled: true, ct);
            schedule.LastRunAt = now;
            schedule.NextRunAt = now.AddHours(Math.Max(1, schedule.IntervalHours));
            logger.LogInformation("Queued scheduled {Type} backup for {Target}.", schedule.Type, schedule.TargetRef);
        }
        if (due.Count > 0) await db.SaveChangesAsync(ct);

        await engine.EnforceRetentionAsync(ct);
    }
}
