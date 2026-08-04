using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Jobs;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>
/// The module's background work, one handler per <see cref="JobKind"/>.
///
/// <para>
/// Each is resolved by <c>JobDispatcher</c> inside the worker's scope, which runs unscoped and
/// therefore sees every tenant — see ARCHITECTURE.md § 6 for why a handler that accidentally ran
/// with a request scope would read nothing and report success.
/// </para>
/// </summary>
public sealed class BackupSnapshotJobHandler(BackupSnapshotService snapshots) : IJobHandler
{
    public JobKind Kind => JobKind.BackupSnapshot;

    public Task ExecuteAsync(Guid targetId, CancellationToken ct) => snapshots.RunAsync(targetId, ct);
}

public sealed class BackupRestoreJobHandler(RestoreService restores) : IJobHandler
{
    public JobKind Kind => JobKind.BackupRestore;

    public Task ExecuteAsync(Guid targetId, CancellationToken ct) => restores.RunAsync(targetId, ct);
}

public sealed class BackupPruneJobHandler(BackupRetentionService retention) : IJobHandler
{
    public JobKind Kind => JobKind.BackupPrune;

    public Task ExecuteAsync(Guid targetId, CancellationToken ct) => retention.PruneAsync(targetId, ct);
}

public sealed class RepositoryHealthCheckJobHandler(BackupRepositoryService repositories) : IJobHandler
{
    public JobKind Kind => JobKind.RepositoryHealthCheck;

    public Task ExecuteAsync(Guid targetId, CancellationToken ct) =>
        repositories.CheckHealthAsync(targetId, ct);
}

/// <summary>
/// Confirms a stored snapshot is still readable.
///
/// <para>
/// Reads the snapshot back through the engine without touching any live data. It is a weaker check
/// than the existing platform verifier's restore rehearsal, and it is recorded as exactly what it
/// is: <see cref="BackupVerificationStatus.Passed"/> means "the repository could list and open
/// this", not "this will restore your application".
/// </para>
/// </summary>
public sealed class BackupVerifyJobHandler(
    HarboraDbContext db,
    IBackupEngineResolver engines,
    IRepositoryCredentialReader credentials,
    IBackupNotificationService notifications,
    ILogger<BackupVerifyJobHandler> logger) : IJobHandler
{
    public JobKind Kind => JobKind.BackupVerify;

    public async Task ExecuteAsync(Guid snapshotId, CancellationToken ct)
    {
        var snapshot = await db.BackupSnapshots.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == snapshotId, ct);

        if (snapshot is null) return;

        if (!snapshot.IsRestorable || snapshot.EngineSnapshotId is null)
        {
            snapshot.VerificationStatus = BackupVerificationStatus.Skipped;
            snapshot.VerificationNote = "Only a completed backup can be verified.";
            snapshot.VerifiedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return;
        }

        var repository = await db.BackupRepositories.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == snapshot.RepositoryId, ct);

        var password = repository is null
            ? null
            : await credentials.GetPasswordAsync(repository.Id, ct);

        if (repository is null || password is null)
        {
            await RecordAsync(snapshot, BackupVerificationStatus.Failed,
                "The repository could not be opened.", ct);
            return;
        }

        try
        {
            var engine = engines.Resolve(repository.Engine);

            // Listing the root proves the repository can find the snapshot and read its structure.
            var entries = await engine.BrowseSnapshotAsync(
                new BrowseSnapshotRequest(repository.Id, snapshot.EngineSnapshotId, password), ct);

            if (entries.Count == 0 && snapshot.FilesCount > 0)
            {
                await RecordAsync(snapshot, BackupVerificationStatus.Failed,
                    "The backup recorded files but none can be read back.", ct);

                await notifications.SendAsync(new BackupNotification(
                    snapshot.WorkspaceId,
                    BackupNotificationKind.SnapshotVerificationFailed,
                    BackupNotificationSeverity.Critical,
                    $"Backup of {snapshot.TargetRef} failed verification",
                    "The backup recorded files but none could be read back from the repository.",
                    repository.Id, snapshot.Id), ct);
                return;
            }

            await RecordAsync(snapshot, BackupVerificationStatus.Passed,
                $"{entries.Count} top-level entr{(entries.Count == 1 ? "y" : "ies")} readable.", ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Verification of snapshot {SnapshotId} failed.", snapshotId);
            await RecordAsync(snapshot, BackupVerificationStatus.Failed, ex.Message, ct);
        }
    }

    private async Task RecordAsync(
        BackupSnapshot snapshot, BackupVerificationStatus status, string note, CancellationToken ct)
    {
        snapshot.VerificationStatus = status;
        snapshot.VerificationNote = note;
        snapshot.VerifiedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Forwards the module's notifications to Harbora's existing alert pipeline.
///
/// <para>
/// No new channel is invented here. The platform already knows how to reach Telegram, email and the
/// in-app list; this maps the module's vocabulary onto <see cref="AlertEvent"/> so those keep working.
/// </para>
/// </summary>
public sealed class BackupNotificationService(
    INotificationService notifications,
    ILogger<BackupNotificationService> logger) : IBackupNotificationService
{
    public async Task SendAsync(BackupNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var severity = notification.Severity switch
        {
            BackupNotificationSeverity.Critical => AlertSeverity.Critical,
            BackupNotificationSeverity.Warning => AlertSeverity.Warning,
            _ => AlertSeverity.Info
        };

        try
        {
            // Every backup-module event maps to BackupFailed today: it is the only backup-related
            // AlertEvent that exists, and adding members to that enum would change a shipped
            // platform contract for the sake of a module that is still behind a flag.
            await notifications.NotifyAsync(
                notification.WorkspaceId, AlertEvent.BackupFailed, severity,
                notification.Title, notification.Detail, cancellationToken);
        }
        catch (Exception ex)
        {
            // Best-effort, like the platform's own notifier: an unreachable chat must not turn a
            // successful backup into a failed one.
            logger.LogWarning(ex, "Could not deliver a {Kind} notification.", notification.Kind);
        }
    }
}
