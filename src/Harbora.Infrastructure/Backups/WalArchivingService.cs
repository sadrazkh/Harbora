using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Backups;

/// <summary>
/// Turns point-in-time recovery on or off for a PostgreSQL instance (3.1, round-2 market-gaps plan) —
/// the instance-level toggle, in exactly the shape
/// <c>LogicalDatabaseService.SetPgVectorEnabledAsync</c> already established for an adjacent
/// "requested setting that only becomes real on the next rebuild" case. Touches nothing on the
/// engine: it only stores the request and marks
/// <see cref="Harbora.Domain.Services.ManagedService.HasUnpublishedChanges"/>.
/// <see cref="Harbora.Infrastructure.Services.ManagedServiceEngine.ProvisionAsync"/> is what actually
/// changes the container's command line, via <see cref="PostgresWalArchivingCommand"/>.
/// </summary>
public sealed class WalArchivingService(HarboraDbContext db, ILogger<WalArchivingService> logger)
{
    /// <summary>
    /// Requests archiving on or off. Refused, before touching a row, for any engine
    /// <see cref="PitrSupport"/> does not name. Returns null on success, or the sentence to show — the
    /// same shape every other instance-level toggle on this platform already returns.
    ///
    /// <para>
    /// Turning it off is not refused for any reason turning pgvector off can be: nothing about
    /// disabling future archiving makes an already-taken base backup or an already-shipped WAL
    /// segment unusable, so there is no "still installed somewhere" check to run. Existing archives
    /// stay exactly as restorable as they were; only new ones stop being taken.
    /// </para>
    /// </summary>
    public async Task<string?> SetEnabledAsync(Guid managedServiceId, bool enable, CancellationToken ct)
    {
        var service = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == managedServiceId, ct);
        if (service is null) return "That database instance no longer exists.";

        if (!PitrSupport.Supports(service.Type)) return PitrSupport.UnsupportedReason(service.Type);

        // 3.2 (round-2 market-gaps plan): a read replica has no WAL of its own to archive — it never
        // takes an independent write, so there is nothing an archive_command on it would ever ship
        // beyond what streaming replication already copies live. Point-in-time recovery belongs on
        // the primary, which is the row this actually refuses to and names.
        if (service.PrimaryManagedServiceId is { } primaryId)
        {
            var primaryName = await db.ManagedServices.AsNoTracking()
                .Where(s => s.Id == primaryId).Select(s => s.Name).FirstOrDefaultAsync(ct);
            return $"'{service.Name}' is a read replica of '{primaryName ?? "its primary"}'. " +
                   "Point-in-time recovery archives WAL from a primary, not from a replica — turn it " +
                   "on there instead.";
        }

        if (service.PitrEnabled == enable) return null;

        service.PitrEnabled = enable;
        service.HasUnpublishedChanges = true;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Point-in-time recovery {State} for {Name}; takes effect on the instance's next rebuild.",
            enable ? "enabled" : "disabled", service.Name);
        return null;
    }

    /// <summary>
    /// The recoverable window for one instance, right now — <see cref="PitrRecoveryWindow.Compute"/>
    /// fed from this instance's own rows. The single place a controller or a background sweep asks
    /// "what can this instance actually be restored to", so the arithmetic is never duplicated at a
    /// second call site and drifts from this one.
    /// </summary>
    public async Task<PitrWindow> RecoveryWindowAsync(Guid managedServiceId, DateTimeOffset now, CancellationToken ct)
    {
        var service = await db.ManagedServices.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == managedServiceId, ct);
        if (service is null)
            return new PitrWindow(PitrStatus.NotConfigured, null, null, null, 0, null,
                "That database instance no longer exists.");

        if (!PitrSupport.Supports(service.Type))
            return new PitrWindow(PitrStatus.NotConfigured, null, null, null, 0, null,
                PitrSupport.UnsupportedReason(service.Type));

        // The oldest COMPLETED base backup still present. Nothing further to filter: EnforceRetentionAsync
        // already removes a pruned Backup row outright, so anything still here is, by definition, still
        // retained — see WalRetention for the pruning pass this reads the same guarantee from.
        var oldestBaseBackup = await db.Backups.AsNoTracking()
            .Where(b => b.WorkspaceId == service.WorkspaceId
                        && b.Type == BackupType.PostgresBaseBackup
                        && b.TargetRef == service.Id.ToString()
                        && b.Status == BackupStatus.Completed)
            .OrderBy(b => b.FinishedAt)
            .Select(b => b.FinishedAt)
            .FirstOrDefaultAsync(ct);

        var archiving = await db.WalArchivingStatuses.AsNoTracking()
            .FirstOrDefaultAsync(w => w.ManagedServiceId == managedServiceId, ct);

        return PitrRecoveryWindow.Compute(
            service.PitrEnabled, service.HasUnpublishedChanges, oldestBaseBackup, archiving, now);
    }
}
