using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Backups;

/// <summary>
/// Checks backups on its own, so nobody finds out during an incident.
///
/// One at a time and slowly on purpose: verifying now means restoring the dump into a scratch
/// database, which costs real work on the same server the database is serving from. The point is not
/// to check everything constantly — it is that no backup goes indefinitely unchecked, and that a
/// backup which will not restore is discovered on an ordinary afternoon instead of at 3am.
/// </summary>
public sealed class BackupVerifier(IServiceScopeFactory scopeFactory, ILogger<BackupVerifier> logger) : BackgroundService
{
    /// <summary>Slow by design — see the note above about what a verification actually costs.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Long enough after startup that migrations, seeding and any queued deploys are done: this
        // competes for the same server as real work.
        try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try { await VerifyOneAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Backup verification pass failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Verifies at most one backup. Public because "which one is due" is a rule worth exercising
    /// directly rather than waiting an hour to observe.
    /// </summary>
    public async Task VerifyOneAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var engine = scope.ServiceProvider.GetRequiredService<BackupEngine>();
        var clock = scope.ServiceProvider.GetRequiredService<ISystemClock>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var incidents = scope.ServiceProvider.GetRequiredService<Monitoring.IncidentService>();

        var candidates = await db.Backups.IgnoreQueryFilters()
            .Where(b => b.Status == BackupStatus.Completed)
            .ToListAsync(ct);

        if (VerificationSchedule.NextDue(candidates, clock.UtcNow) is not { } due) return;

        logger.LogInformation("Verifying backup {Id} ({Type} of {Target}).", due.Id, due.Type, due.TargetRef);
        var result = await engine.VerifyAsync(due.Id, ct);

        if (result.IsRestorable) return;

        // Worth interrupting someone for: this is a backup they believe they have. Opens rather than
        // stays a notification-only fact, for the same reason a failed backup does anywhere else in
        // this file: a restore check never re-runs itself, so nothing will ever observe this clear —
        // only a person acknowledging it, or the bounded auto-expiry backstop, closes it.
        await incidents.OpenAsync(due.WorkspaceId, AlertEvent.BackupFailed, due.Id.ToString(),
            AlertSeverity.Critical, "A backup would not restore",
            $"The most recent {due.Type} backup of '{due.TargetRef}' failed its restore check. " +
            $"{result.Reason} Take a fresh backup and check it before relying on this one.", clock.UtcNow, ct);
        await db.SaveChangesAsync(ct);
        await notifications.NotifyAsync(due.WorkspaceId, AlertEvent.BackupFailed, AlertSeverity.Critical,
            "A backup would not restore",
            $"The most recent {due.Type} backup of '{due.TargetRef}' failed its restore check. " +
            $"{result.Reason} Take a fresh backup and check it before relying on this one.", ct);
    }
}
