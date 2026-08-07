using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Jobs;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Modules.Backup.Infrastructure;

public sealed record SnapshotOutcome(bool Succeeded, Guid? SnapshotId = null, string? Error = null);

/// <summary>
/// Queues and runs snapshots.
///
/// <para>
/// Queue and run are separate methods on purpose. The row is written and a job persisted inside the
/// request; the work happens later on the worker. A snapshot that took twenty minutes inside an HTTP
/// request would be a snapshot lost to the first proxy timeout, with no row to show it was ever
/// attempted.
/// </para>
/// </summary>
public sealed class BackupSnapshotService(
    HarboraDbContext db,
    IBackupEngineResolver engines,
    IRepositoryCredentialReader credentials,
    IBackupTargetResolver targets,
    IJobQueue jobs,
    IBackupNotificationService notifications,
    ICurrentUser currentUser,
    IAuditLogger audit,
    ILogger<BackupSnapshotService> logger)
{
    /// <summary>
    /// One sentence for both halves of the guard: the query that gives it early, and the index that
    /// enforces it late. A caller must not be able to tell which one refused.
    /// </summary>
    internal const string AlreadyRunning = "A backup of this target is already running.";

    /// <summary>Create the row and hand the work to the durable queue.</summary>
    public async Task<SnapshotOutcome> QueueAsync(
        Guid workspaceId,
        Guid repositoryId,
        BackupTargetType targetType,
        string targetRef,
        Guid? policyId,
        BackupTrigger trigger,
        CancellationToken ct)
    {
        var repository = await db.BackupRepositories
            .FirstOrDefaultAsync(r => r.Id == repositoryId, ct);

        if (repository is null) return new SnapshotOutcome(false, Error: "That repository no longer exists.");
        if (!repository.IsEnabled)
            return new SnapshotOutcome(false, Error: $"Repository '{repository.Name}' is disabled.");

        // Validated, not acquired: this must not stage a 200 GB volume inside an HTTP request. The
        // check is side-effect-free, so a mistyped target is a message on the screen the user is
        // looking at instead of a failed job they have to go and find.
        var resolved = targets.Validate(targetType, targetRef);
        if (!resolved.Succeeded) return new SnapshotOutcome(false, Error: resolved.Error);

        // One backup at a time per target. Two concurrent snapshots of the same directory waste the
        // work at best, and at worst disagree about what the data looked like.
        //
        // This check exists for its MESSAGE, not for its safety: it is a read followed by an
        // insert, so two callers can both pass it. The partial unique index behind the insert below
        // is what actually holds under concurrency.
        var alreadyRunning = await db.BackupSnapshots.AnyAsync(s =>
            s.WorkspaceId == workspaceId
            && s.TargetType == targetType
            && s.TargetRef == targetRef
            && (s.Status == BackupSnapshotStatus.Pending
                || s.Status == BackupSnapshotStatus.Preparing
                || s.Status == BackupSnapshotStatus.Running), ct);

        if (alreadyRunning) return new SnapshotOutcome(false, Error: AlreadyRunning);

        var snapshot = new BackupSnapshot
        {
            WorkspaceId = workspaceId,
            RepositoryId = repositoryId,
            PolicyId = policyId,
            TargetType = targetType,
            TargetRef = targetRef,
            Status = BackupSnapshotStatus.Pending,
            TriggeredBy = trigger,
            TriggeredByUserId = currentUser.UserId,
            CorrelationId = Guid.CreateVersion7().ToString("N")[..16]
        };

        db.BackupSnapshots.Add(snapshot);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost the race the pre-check above cannot win. The partial unique index over the
            // active statuses is the authority, and losing it means exactly what the pre-check
            // describes — so say the same thing rather than surfacing a constraint name.
            db.ChangeTracker.Clear();
            return new SnapshotOutcome(false, Error: AlreadyRunning);
        }

        await jobs.EnqueueAsync(JobKind.BackupSnapshot, snapshot.Id, ct);
        return new SnapshotOutcome(true, snapshot.Id);
    }

    /// <summary>
    /// The job body.
    ///
    /// <para>
    /// Idempotent, because a worker that crashed mid-run will claim this again: a snapshot already in
    /// a terminal state returns immediately rather than producing a second copy.
    /// </para>
    /// </summary>
    public async Task RunAsync(Guid snapshotId, CancellationToken ct)
    {
        var snapshot = await db.BackupSnapshots.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == snapshotId, ct);

        if (snapshot is null) return;
        if (snapshot.IsTerminal)
        {
            logger.LogInformation("Snapshot {SnapshotId} is already {Status}; nothing to do.",
                snapshotId, snapshot.Status);
            return;
        }

        var repository = await db.BackupRepositories.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == snapshot.RepositoryId, ct);

        if (repository is null)
        {
            await FailAsync(snapshot, "The repository this backup was going to no longer exists.", ct);
            return;
        }

        try
        {
            SnapshotLifecycle.Transition(snapshot, BackupSnapshotStatus.Preparing);
            snapshot.StartedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            // A Docker volume is staged to disk here, so the lease is held for exactly as long as
            // the engine needs it and released whatever happens — a staged copy is plaintext
            // application data and must not outlive the backup that needed it.
            await using var lease = await targets.AcquireAsync(snapshot.TargetType, snapshot.TargetRef, ct);
            if (!lease.Succeeded)
            {
                await FailAsync(snapshot, lease.Error!, ct);
                return;
            }

            // Written down so a kill -9 — which skips the lease's finally — still leaves a trail to
            // the staged copy. A directory target is excluded: its "source path" is the operator's
            // own live data, and the reconciler must never be handed a path to it.
            snapshot.StagingPath = snapshot.TargetType is BackupTargetType.Directory
                ? null
                : lease.SourcePath;

            var password = await credentials.GetPasswordAsync(repository.Id, ct);
            if (password is null)
            {
                await FailAsync(snapshot,
                    "The repository password could not be decrypted, so nothing was written.", ct);
                return;
            }

            SnapshotLifecycle.Transition(snapshot, BackupSnapshotStatus.Running);
            await db.SaveChangesAsync(ct);

            var engine = engines.Resolve(repository.Engine);
            var result = await engine.CreateSnapshotAsync(new CreateBackupSnapshotRequest(
                repository.Id,
                snapshot.Id,
                lease.SourcePath!,
                password,
                snapshot.TargetType,
                snapshot.TargetRef), ct);

            if (!result.Succeeded)
            {
                await FailAsync(snapshot, result.Error ?? "The backup failed.", ct);
                return;
            }

            snapshot.EngineSnapshotId = result.EngineSnapshotId;
            snapshot.OriginalSizeBytes = result.OriginalSizeBytes;
            snapshot.StoredSizeBytes = result.StoredSizeBytes;
            snapshot.DeduplicatedSizeBytes = result.DeduplicatedSizeBytes;
            snapshot.FilesCount = result.FilesCount;
            snapshot.CompletedAt = DateTimeOffset.UtcNow;
            snapshot.Warnings = result.Warnings is { Count: > 0 }
                ? string.Join('\n', result.Warnings)
                : null;

            SnapshotLifecycle.Transition(snapshot, result.Warnings is { Count: > 0 }
                ? BackupSnapshotStatus.CompletedWithWarnings
                : BackupSnapshotStatus.Completed);

            // The lease removes the staged copy on the way out of this method, so the row must stop
            // claiming there is one to remove.
            snapshot.StagingPath = null;

            if (snapshot.PolicyId is { } policyId)
            {
                var policy = await db.BackupPolicies.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(p => p.Id == policyId, ct);
                if (policy is not null) policy.LastSuccessAt = snapshot.CompletedAt;
            }

            repository.SnapshotCount++;
            repository.StorageUsageBytes += result.StoredSizeBytes;

            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Snapshot {SnapshotId} completed: {Files} file(s), {Stored} byte(s) stored. [{Correlation}]",
                snapshot.Id, result.FilesCount, result.StoredSizeBytes, snapshot.CorrelationId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SnapshotLifecycle.Transition(snapshot, BackupSnapshotStatus.Cancelled);
            snapshot.CompletedAt = DateTimeOffset.UtcNow;
            snapshot.StagingPath = null;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Snapshot {SnapshotId} failed.", snapshotId);
            await FailAsync(snapshot, ex.Message, ct);
        }
    }

    public async Task<IReadOnlyList<EngineEntry>> BrowseAsync(
        Guid snapshotId, string relativePath, CancellationToken ct)
    {
        var (snapshot, repository, password) = await LoadForReadAsync(snapshotId, ct);
        if (snapshot?.EngineSnapshotId is null || repository is null || password is null) return [];

        var engine = engines.Resolve(repository.Engine);
        return await engine.BrowseSnapshotAsync(
            new BrowseSnapshotRequest(repository.Id, snapshot.EngineSnapshotId, password, relativePath), ct);
    }

    /// <summary>
    /// Delete a snapshot: from the engine first, then the row.
    ///
    /// <para>
    /// That order matters. Removing the row first and failing at the engine would leave an artifact
    /// nothing knows about — invisible, never pruned, and still holding the data someone asked to
    /// have deleted.
    /// </para>
    /// </summary>
    public async Task<SnapshotOutcome> DeleteAsync(Guid snapshotId, CancellationToken ct)
    {
        var snapshot = await db.BackupSnapshots.FirstOrDefaultAsync(s => s.Id == snapshotId, ct);
        if (snapshot is null) return new SnapshotOutcome(false, Error: "That snapshot no longer exists.");

        var repository = await db.BackupRepositories
            .FirstOrDefaultAsync(r => r.Id == snapshot.RepositoryId, ct);
        if (repository is null) return new SnapshotOutcome(false, Error: "That repository no longer exists.");

        var password = await credentials.GetPasswordAsync(repository.Id, ct);
        if (password is null)
            return new SnapshotOutcome(false, Error: "The repository password could not be decrypted.");

        if (snapshot.EngineSnapshotId is { } engineId)
        {
            SnapshotLifecycle.Transition(snapshot, BackupSnapshotStatus.Deleting);
            await db.SaveChangesAsync(ct);

            var engine = engines.Resolve(repository.Engine);
            var deleted = await engine.DeleteSnapshotAsync(
                new DeleteSnapshotRequest(repository.Id, engineId, password), ct);

            if (!deleted.Succeeded)
            {
                SnapshotLifecycle.Transition(snapshot, BackupSnapshotStatus.Failed);
                snapshot.FailureReason = deleted.Error;
                await db.SaveChangesAsync(ct);
                return new SnapshotOutcome(false, Error: deleted.Error);
            }
        }

        db.BackupSnapshots.Remove(snapshot);
        if (repository.SnapshotCount > 0) repository.SnapshotCount--;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("backup.snapshot.delete", "BackupSnapshot", snapshotId.ToString(),
            userIdOverride: currentUser.UserId, ct: ct);

        return new SnapshotOutcome(true, snapshotId);
    }

    /// <summary>
    /// What a screen needs to show a snapshot: the row and the repository's name.
    ///
    /// <para>
    /// Deliberately does NOT hand back the repository password. The internal overload below does,
    /// because the engine calls need it; a controller never does, and a secret that is not returned
    /// cannot be logged, serialised into a view model, or put in a hidden field by mistake.
    /// </para>
    /// </summary>
    public async Task<(BackupSnapshot? Snapshot, string? RepositoryName)> GetForDisplayAsync(
        Guid snapshotId, CancellationToken ct)
    {
        var (snapshot, repository, _) = await LoadForReadAsync(snapshotId, ct);
        return (snapshot, repository?.Name);
    }

    internal async Task<(BackupSnapshot? Snapshot, BackupRepository? Repository, string? Password)>
        LoadForReadAsync(Guid snapshotId, CancellationToken ct)
    {
        var snapshot = await db.BackupSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == snapshotId, ct);
        if (snapshot is null) return (null, null, null);

        var repository = await db.BackupRepositories.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == snapshot.RepositoryId, ct);
        if (repository is null) return (snapshot, null, null);

        return (snapshot, repository, await credentials.GetPasswordAsync(repository.Id, ct));
    }

    private async Task FailAsync(BackupSnapshot snapshot, string reason, CancellationToken ct)
    {
        SnapshotLifecycle.Transition(snapshot, BackupSnapshotStatus.Failed);
        snapshot.FailureReason = reason;
        snapshot.CompletedAt = DateTimeOffset.UtcNow;
        // The lease cleans up behind this method, so nothing is left for a reconciler to sweep.
        snapshot.StagingPath = null;
        await db.SaveChangesAsync(ct);

        await notifications.SendAsync(new BackupNotification(
            snapshot.WorkspaceId,
            BackupNotificationKind.BackupFailed,
            BackupNotificationSeverity.Warning,
            $"Backup of {snapshot.TargetRef} failed",
            reason,
            snapshot.RepositoryId,
            snapshot.Id,
            snapshot.PolicyId), ct);
    }

    /// <summary>Metadata a screen needs, without ever loading an artifact.</summary>
    public Task<List<BackupSnapshot>> ListAsync(Guid? repositoryId, int take, CancellationToken ct)
    {
        var query = db.BackupSnapshots.AsNoTracking();
        if (repositoryId is { } id) query = query.Where(s => s.RepositoryId == id);

        return query.OrderByDescending(s => s.CreatedAt).Take(take).ToListAsync(ct);
    }

    /// <summary>Serialises a snapshot's stats for the audit trail without touching secrets.</summary>
    internal static string Describe(BackupSnapshot snapshot) => JsonSerializer.Serialize(new
    {
        snapshot.TargetRef,
        Target = snapshot.TargetType.ToString(),
        Status = snapshot.Status.ToString(),
        snapshot.FilesCount,
        snapshot.StoredSizeBytes
    });
}
