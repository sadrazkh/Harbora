using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Jobs;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

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
        catch (DbUpdateException e)
            when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Lost the race the pre-check above cannot win. The partial unique index over the
            // active statuses is the authority, and losing it means exactly what the pre-check
            // describes — so say the same thing rather than surfacing a constraint name.
            //
            // Qualified on the unique violation, and only that. Every other refusal this insert can
            // meet — a repository deleted between the read and the write, a check constraint, a
            // serialisation failure, a dropped connection — is NOT "already running", and reporting
            // it as such would send an operator looking for a backup that does not exist while the
            // real fault went unrecorded. Those surface as themselves.
            db.ChangeTracker.Clear();
            return new SnapshotOutcome(false, Error: AlreadyRunning);
        }

        await jobs.EnqueueAsync(JobKind.BackupSnapshot, snapshot.Id, snapshot.WorkspaceId, ct);
        return new SnapshotOutcome(true, snapshot.Id);
    }

    /// <summary>
    /// Ask the verifier to read a finished snapshot back.
    ///
    /// <para>
    /// The queue's job, not this method's: reading a snapshot back means fetching and decrypting an
    /// archive, which is not work to do inside the request an operator is waiting on. The job
    /// excludes on the snapshot's own id, so a "verify now" pressed twice, or pressed while the
    /// automatic check from the backup is still queued, runs one after the other rather than twice
    /// at once — and either order leaves the same answer on the row.
    /// </para>
    /// <para>
    /// Read through the ordinary filtered set, so a snapshot belonging to another workspace is
    /// indistinguishable from one that is not there.
    /// </para>
    /// </summary>
    public async Task<SnapshotOutcome> QueueVerificationAsync(Guid snapshotId, CancellationToken ct)
    {
        var snapshot = await db.BackupSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == snapshotId, ct);

        if (snapshot is null) return new SnapshotOutcome(false, Error: "That snapshot no longer exists.");

        // Same sentence the handler writes when it is asked to verify something unfinished, so the
        // refusal an operator reads on the screen and the note they would have read on the row say
        // the same thing.
        if (!snapshot.IsRestorable)
            return new SnapshotOutcome(false, Error:
                $"This backup is {snapshot.Status} — only a completed backup can be verified.");

        // A check already waiting is the answer to this press. Five presses of a button that fetches
        // and decrypts an archive would otherwise be five fetches of the same archive, each one
        // excluding the next through the queue and all of them producing the identical row.
        //
        // Reported as SUCCESS rather than as a refusal, because it is one: the operator asked for
        // the Verified column to be brought up to date and it is going to be. Failing here would put
        // a red message on the screen for a request that is being honoured.
        if (!await BackupVerificationQueue.AlreadyQueuedAsync(db, snapshot.Id, ct))
            await jobs.EnqueueAsync(JobKind.BackupVerify, snapshot.Id, snapshot.WorkspaceId, ct);

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

        // Already in flight, so this execution is a duplicate and must touch nothing.
        //
        // SnapshotLifecycle.CanTransition allows Preparing -> Preparing and Running -> Running on
        // purpose: re-applying the state a row already holds is how an idempotent retry behaves.
        // That was harmless while staging directories were named from a fresh Guid — a second
        // execution simply staged somewhere else. It is not harmless now that the name comes from
        // the snapshot's id: every stager clears the directory before creating it, so going on from
        // here would delete the copy the live execution is part-way through writing, and the archive
        // that survived would be two moments of the data mixed together with nothing saying so.
        //
        // Nothing legitimate arrives here. BackupModuleReconciler settles every stranded row before
        // the host releases the job worker, so a row still Preparing or Running at this point is one
        // another execution owns right now. Refusing costs this snapshot nothing it was not already
        // losing — the same active row is what QueueAsync refuses on — and the next restart settles
        // it if that other execution never does.
        //
        // What this return leaves behind, said plainly: the row stays Preparing with FailureReason
        // null, and the JOB is recorded as having succeeded, because returning is not throwing. So
        // the Backup Center shows a target whose backups are being refused with nothing on screen
        // saying why, until the next restart's reconciler settles it with a reason. Only the warning
        // below records it in the meantime.
        //
        // Writing a reason onto the row instead is what must not happen: the row belongs to the
        // execution that is still running it, and this method's whole purpose here is to touch
        // nothing that execution owns. Throwing is no better — the job would be retried, and a
        // retry is exactly the duplicate being refused.
        if (snapshot.Status is BackupSnapshotStatus.Preparing or BackupSnapshotStatus.Running)
        {
            logger.LogWarning(
                "Snapshot {SnapshotId} is already {Status}, so another execution owns it and the " +
                "staged copy named after it. Leaving both alone; the row keeps its status and " +
                "carries no reason until a restart settles it. [{Correlation}]",
                snapshotId, snapshot.Status, snapshot.CorrelationId);
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
            // The snapshot's own id names the staged copy. That is what makes the copy findable
            // from this row DURING the staging — the longest window there is, and the one this
            // assignment cannot cover, because the row is not written until AcquireAsync returns.
            await using var lease = await targets.AcquireAsync(
                snapshot.TargetType, snapshot.TargetRef, snapshot.Id, ct);

            if (!lease.Succeeded)
            {
                await FailAsync(snapshot, lease.Error!, ct);
                return;
            }

            // Written down as well, so the exact path is on the row even if the layout above ever
            // changes. A directory target is excluded: its "source path" is the operator's own live
            // data, and the reconciler must never be handed a path to it.
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

            // The check is asked for HERE — at the end of the job that produced the snapshot —
            // rather than from BackupPolicyScheduler, and the choice is not stylistic.
            //
            // The scheduler only knows about policies. A manual "back up now", an API-triggered
            // snapshot and the safety copy a restore takes have no policy at all, so a scheduled
            // sweep would leave precisely the backups a person asked for unchecked. This is also the
            // first moment at which the thing to check exists and is known to be finished, which is
            // what makes NotVerified a state a snapshot passes through rather than one it sits in
            // until the next tick.
            //
            // It cannot collide with the work around it. The queue excludes on a (kind, target)
            // pair: this is a BackupVerify keyed on the snapshot's own id, so it is invisible to the
            // BackupSnapshot job it was enqueued from — which is right, because that job is finished
            // with the repository by now and verification only reads. What it does exclude with is
            // another verify of the SAME snapshot, so the automatic check and an operator pressing
            // "verify now" queue behind one another instead of both browsing at once; either order
            // leaves the same answer on the row.
            //
            // Enqueued after the completion is saved, and never allowed to undo it — nor to leave a
            // refused insert tracked in this context for the next save to trip over. See
            // BackupVerificationQueue.
            await BackupVerificationQueue.RequestAsync(jobs, db, snapshot.Id, snapshot.WorkspaceId, logger, ct);
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
