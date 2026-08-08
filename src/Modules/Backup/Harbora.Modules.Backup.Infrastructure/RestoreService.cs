using Harbora.Shared;
using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Jobs;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>
/// A restore request from a user.
///
/// <para>
/// <paramref name="ConfirmationText"/> must repeat the destination exactly when the destination
/// holds live data. A checkbox is not a confirmation — it is a thing people click. Typing the name
/// of what is about to be overwritten is the only cheap control that reliably distinguishes "I meant
/// this one" from "I clicked the row above".
/// </para>
/// </summary>
public sealed record RestoreRequest(
    Guid SnapshotId,
    RestoreType RestoreType,
    string Destination,
    RestoreConflictStrategy ConflictStrategy,
    IReadOnlyList<string>? Entries = null,
    string? ConfirmationText = null);

public sealed record RestoreOutcome(bool Succeeded, Guid? RestoreJobId = null, string? Error = null);

/// <summary>
/// Queues and runs restores.
///
/// <para>
/// The most destructive authenticated operation in the product, so it is also the most guarded:
/// confined destinations, explicit confirmation before overwriting anything live, an audit entry
/// naming who asked for what, and a job row that survives to say how it ended.
/// </para>
/// </summary>
public sealed class RestoreService(
    HarboraDbContext db,
    IBackupEngineResolver engines,
    IRepositoryCredentialReader credentials,
    IDatabaseRestoreExecutor databaseRestores,
    IBackupTargetResolver targets,
    IJobQueue jobs,
    IBackupNotificationService notifications,
    ICurrentUser currentUser,
    IAuditLogger audit,
    IOptions<BackupModuleOptions> options,
    ILogger<RestoreService> logger)
{
    private readonly BackupModuleOptions _options = options.Value;

    /// <summary>
    /// One sentence for both halves of the guard — the query that gives it early and the index that
    /// enforces it late — so a caller cannot tell which one refused.
    /// </summary>
    internal const string AlreadyRunning = "A restore into this destination is already running.";

    /// <summary>
    /// What an operator reads when the way back could not be made.
    ///
    /// <para>
    /// The legacy engine says the same thing about its pre-restore dump — "the restore was not
    /// started — there would have been nothing to go back to" — and the sentence is kept in that
    /// spirit for one reason: the reader has to finish it certain that their data is untouched.
    /// "Restore failed" is ambiguous at exactly the moment ambiguity is most expensive.
    /// </para>
    /// </summary>
    internal const string SafetyCopyRefused =
        "A copy of the destination could not be taken before restoring, so the restore was not " +
        "started and nothing at the destination was changed — there would have been nothing to go " +
        "back to.";

    /// <summary>
    /// The width of <c>RestoreJob.FailureReason</c> in <c>HarboraDbContext</c>, which is the
    /// authority. Named here because this class is the only thing that lengthens the reason after
    /// the engine has already written into it, and a column overflow at the moment a restore fails
    /// would lose the reason it failed.
    /// </summary>
    private const int FailureReasonLimit = 2048;

    /// <summary>
    /// Removes the intermediate copy a database restore lands in. Best-effort, logged loudly: a dump
    /// left behind is a plaintext copy of an entire database on disk.
    /// </summary>
    private void CleanupDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A restored database dump could not be removed from {Path}.", path);
        }
    }

    public async Task<RestoreOutcome> QueueAsync(Guid workspaceId, RestoreRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var snapshot = await db.BackupSnapshots
            .FirstOrDefaultAsync(s => s.Id == request.SnapshotId, ct);

        if (snapshot is null) return new RestoreOutcome(false, Error: "That backup no longer exists.");

        if (!snapshot.IsRestorable)
            return new RestoreOutcome(false, Error:
                $"This backup is {snapshot.Status} — only a completed backup can be restored.");

        string destination;
        bool overwritesLive;

        if (request.RestoreType is RestoreType.Database)
        {
            // For a database the destination is the database itself, named by its managed-service
            // id. The filesystem location the dump lands in first is derived at run time and is not
            // the user's to choose.
            if (!Guid.TryParse(request.Destination, out var serviceId))
                return new RestoreOutcome(false, Error:
                    "A database restore needs the target database's id as its destination.");

            var name = await databaseRestores.DescribeAsync(serviceId, ct);
            if (name is null)
                return new RestoreOutcome(false, Error: "That database no longer exists.");

            destination = serviceId.ToString();

            // Always. Loading a dump replaces the contents of a live database — there is no version
            // of this that leaves what is there alone.
            overwritesLive = true;

            if (!string.Equals(request.ConfirmationText?.Trim(), name, StringComparison.Ordinal))
                return new RestoreOutcome(false, Error:
                    $"This will replace the contents of the database '{name}'. Type '{name}' to confirm.");
        }
        else
        {
            // Every filesystem destination is confined to the configured root. The check is on the
            // RESOLVED path, so "..", a symlinked parent and an absolute path outside all fail the
            // same way.
            var check = PathGuard.ResolveWithin(_options.RestoreRoot, request.Destination);
            if (!check.Allowed)
                return new RestoreOutcome(false, Error:
                    $"The destination must be inside {_options.RestoreRoot} ({check.Rejection}).");

            destination = check.ResolvedPath!;
            overwritesLive = Directory.Exists(destination)
                             && Directory.EnumerateFileSystemEntries(destination).Any()
                             && request.ConflictStrategy is not RestoreConflictStrategy.RestoreToNewLocation;

            if (overwritesLive)
            {
                var expected = Path.GetFileName(destination.TrimEnd(Path.DirectorySeparatorChar));
                if (!string.Equals(request.ConfirmationText?.Trim(), expected, StringComparison.Ordinal))
                    return new RestoreOutcome(false, Error:
                        $"This restore would write over existing data in '{expected}'. " +
                        $"Type '{expected}' to confirm.");
            }
        }

        // Bounded before the insert, and refused in words. Destination carries a btree unique index
        // and a btree index row is capped near 2704 bytes, which 1024 multi-byte characters — a
        // length the COLUMN still permits — would exceed. Refusing here keeps that limit
        // unreachable through the only path that writes the column, without an ALTER COLUMN that an
        // install holding a longer row would meet as a failed boot. See
        // RestoreJob.MaxDestinationLength.
        if (destination.Length > RestoreJob.MaxDestinationLength)
            return new RestoreOutcome(false, Error:
                $"That destination is {destination.Length} characters long; the longest this panel " +
                $"can record is {RestoreJob.MaxDestinationLength}. Restore into a shorter path.");

        // Two restores into one destination produce a result neither of them describes. Like the
        // snapshot guard, this check is here for its message: it is a read followed by an insert,
        // and the partial unique index behind the insert below is what holds under concurrency.
        var contested = await db.RestoreJobs.AnyAsync(r =>
            r.Destination == destination
            && (r.Status == RestoreJobStatus.Pending || r.Status == RestoreJobStatus.Running), ct);

        if (contested) return new RestoreOutcome(false, Error: AlreadyRunning);

        var job = new RestoreJob
        {
            WorkspaceId = workspaceId,
            SnapshotId = snapshot.Id,
            RestoreType = request.RestoreType,
            Destination = destination,
            OverwritesLiveTarget = overwritesLive,
            ConflictStrategy = request.ConflictStrategy,
            Entries = request.Entries is { Count: > 0 } ? string.Join('\n', request.Entries) : null,
            RequestedByUserId = currentUser.UserId ?? Guid.Empty,
            Status = RestoreJobStatus.Pending,
            CorrelationId = Guid.CreateVersion7().ToString("N")[..16]
        };

        db.RestoreJobs.Add(job);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e)
            when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Lost the race to another restore into the same destination. Nothing was written, and
            // nothing is audited: no restore was requested as far as this caller is concerned.
            //
            // Qualified on the unique violation, and only that. This is the destructive direction,
            // so a wrong explanation costs more here than anywhere: an operator told "a restore into
            // this destination is already running" when the snapshot was actually pruned between the
            // read and the write would go hunting for a concurrent restore instead of reading the
            // fault they hit. Anything that is not the index refusing surfaces as itself.
            db.ChangeTracker.Clear();
            return new RestoreOutcome(false, Error: AlreadyRunning);
        }

        // Audited at REQUEST time, not on completion. A restore that hangs or crashes is exactly the
        // one an incident review needs to see was asked for.
        await audit.LogAsync("backup.restore.request", "RestoreJob", job.Id.ToString(),
            userIdOverride: currentUser.UserId,
            metadataJson: JsonSerializer.Serialize(new
            {
                SnapshotId = snapshot.Id,
                job.Destination,
                job.OverwritesLiveTarget,
                Strategy = job.ConflictStrategy.ToString(),
                EntryCount = request.Entries?.Count ?? 0
            }), ct: ct);

        await jobs.EnqueueAsync(JobKind.BackupRestore, job.Id, ct);
        return new RestoreOutcome(true, job.Id);
    }

    /// <summary>The job body. Idempotent: a restore already finished is not run again.</summary>
    public async Task RunAsync(Guid restoreJobId, CancellationToken ct)
    {
        var job = await db.RestoreJobs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == restoreJobId, ct);

        if (job is null) return;
        if (job.IsTerminal)
        {
            logger.LogInformation("Restore {RestoreId} is already {Status}; nothing to do.",
                restoreJobId, job.Status);
            return;
        }

        var snapshot = await db.BackupSnapshots.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == job.SnapshotId, ct);

        if (snapshot?.EngineSnapshotId is null)
        {
            await FailAsync(job, "The backup this restore came from is no longer usable.", ct);
            return;
        }

        var repository = await db.BackupRepositories.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == snapshot.RepositoryId, ct);

        if (repository is null)
        {
            await FailAsync(job, "The repository this backup lives in no longer exists.", ct);
            return;
        }

        try
        {
            job.Status = RestoreJobStatus.Running;
            job.StartedAt = DateTimeOffset.UtcNow;
            job.Progress = 5;
            await db.SaveChangesAsync(ct);

            var password = await credentials.GetPasswordAsync(repository.Id, ct);
            if (password is null)
            {
                await FailAsync(job, "The repository password could not be decrypted.", ct);
                return;
            }

            job.Progress = 20;
            await db.SaveChangesAsync(ct);

            // The way back, and the last moment there is one.
            //
            // Only when the destination holds live data: OverwritesLiveTarget was decided at queue
            // time, and it is the same flag the typed confirmation was demanded for. A restore into
            // an empty directory has nothing to preserve, so paying for a copy of nothing — and
            // refusing the restore when the repository will not take it — would be protection
            // against a loss that cannot happen.
            //
            // The guard on SafetySnapshotRef is idempotence, not optimism: a second execution of
            // this row must not put a second copy in the repository and forget the first.
            if (job.OverwritesLiveTarget && string.IsNullOrEmpty(job.SafetySnapshotRef))
            {
                var safety = await TakeSafetyCopyAsync(job, repository, password, ct);

                if (!safety.Succeeded)
                {
                    // Nothing has been written yet — the engine has not been asked to restore, no
                    // container has been stopped, no database has been touched — and the refusal
                    // says so outright.
                    await FailAsync(job, $"{SafetyCopyRefused} {safety.Error}", ct);
                    return;
                }

                job.SafetySnapshotRef = safety.Reference;
                job.Progress = 40;
                await db.SaveChangesAsync(ct);

                // Checked like any other backup, and asked for HERE rather than inside the copy.
                // This is the one snapshot whose readability is about to be relied on, so leaving it
                // as the only unverified thing in the repository would be the wrong exception to
                // make — but the reference to it is worth more than the check is, and the enqueue
                // is the step that can fail. Recording where the way back is comes first, and the
                // save above is what makes it durable. See BackupVerificationQueue.
                await BackupVerificationQueue.RequestAsync(jobs, db, safety.SnapshotId, logger, ct);
            }

            var isDatabase = job.RestoreType is RestoreType.Database;

            // A database dump lands in the staging area first, because that is the only directory
            // the database's client container can also see. It is deleted afterwards: a dump on disk
            // is the whole database in the clear.
            var filesDestination = isDatabase
                ? Path.Combine(
                    _options.StagingDirectory, BackupStagingLayout.DatabaseRestoreDirectory(job.Id))
                : job.Destination;

            var engine = engines.Resolve(repository.Engine);
            var result = await engine.RestoreAsync(new RestoreBackupRequest(
                repository.Id,
                snapshot.EngineSnapshotId,
                password,
                filesDestination,
                // The staging directory is ours and always empty, so Fail would be a false alarm.
                isDatabase ? RestoreConflictStrategy.Overwrite : job.ConflictStrategy,
                job.Entries?.Split('\n', StringSplitOptions.RemoveEmptyEntries)), ct);

            if (!result.Succeeded)
            {
                if (isDatabase) CleanupDirectory(filesDestination);
                await FailAsync(job, result.Error ?? "The restore failed.", ct);
                return;
            }

            // A database restore has a second half: the files are on disk, but nothing has reached
            // the server yet. It is not complete until it has.
            if (isDatabase)
            {
                try
                {
                    job.Progress = 70;
                    await db.SaveChangesAsync(ct);

                    var loaded = await databaseRestores.LoadAsync(
                        Guid.Parse(job.Destination), filesDestination, ct);

                    if (!loaded.Succeeded)
                    {
                        await FailAsync(job,
                            loaded.Error ?? "The dump was restored to disk but could not be loaded.", ct);
                        return;
                    }
                }
                finally
                {
                    CleanupDirectory(filesDestination);
                }
            }

            job.Status = RestoreJobStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.Progress = 100;
            job.RestoredFilesCount = result.RestoredFilesCount;
            job.RestoredBytes = result.RestoredBytes;
            await db.SaveChangesAsync(ct);

            await audit.LogAsync("backup.restore.complete", "RestoreJob", job.Id.ToString(),
                userIdOverride: job.RequestedByUserId,
                metadataJson: JsonSerializer.Serialize(new
                {
                    job.RestoredFilesCount,
                    job.RestoredBytes,
                    Warnings = result.Warnings?.Count ?? 0
                }), ct: ct);

            await notifications.SendAsync(new BackupNotification(
                job.WorkspaceId,
                BackupNotificationKind.RestoreCompleted,
                BackupNotificationSeverity.Info,
                "Restore finished",
                $"{result.RestoredFilesCount} file(s) restored to {job.Destination}.",
                repository.Id, snapshot.Id), ct);

            logger.LogInformation("Restore {RestoreId} completed: {Files} file(s). [{Correlation}]",
                job.Id, result.RestoredFilesCount, job.CorrelationId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.Status = RestoreJobStatus.Cancelled;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restore {RestoreId} failed.", restoreJobId);
            await FailAsync(job, ex.Message, ct);
        }
    }

    /// <summary>
    /// What one attempt at a pre-restore copy produced.
    ///
    /// <para>
    /// The row id, not a formatted string, because the caller needs both: one to write onto
    /// <c>RestoreJob.SafetySnapshotRef</c> and one to hand to the verifier.
    /// </para>
    /// </summary>
    private sealed record SafetyCopy(bool Succeeded, Guid SnapshotId = default, string? Error = null)
    {
        /// <summary>
        /// What goes on the row: the snapshot's own id. Not the engine handle — a reference an
        /// operator cannot reach in the Backup Center is not a reference.
        /// </summary>
        public string Reference => SnapshotId.ToString();
    }

    /// <summary>
    /// Copies what is at the destination into the repository before the restore overwrites it.
    ///
    /// <para>
    /// The module's counterpart to <c>BackupEngine.SnapshotBeforeRestoreAsync</c> and the pre-restore
    /// dump in its database path. The caller is the half that matters: a copy that could not be taken
    /// ends the restore rather than being logged and stepped over.
    /// </para>
    /// <para>
    /// Written down as a real <see cref="BackupSnapshot"/> carrying
    /// <see cref="BackupTrigger.Safety"/>, not as a loose artifact in the repository. A way back
    /// nothing lists is not a way back — the row is what puts it in the Backup Center, browsable and
    /// restorable through the same screens as any other backup. It has no policy, and
    /// <c>BackupRetentionService</c> prunes by policy, so nothing deletes it on a schedule.
    /// </para>
    /// </summary>
    private async Task<SafetyCopy> TakeSafetyCopyAsync(
        RestoreJob job, BackupRepository repository, string password, CancellationToken ct)
    {
        var isDatabase = job.RestoreType is RestoreType.Database;

        var snapshot = new BackupSnapshot
        {
            WorkspaceId = job.WorkspaceId,
            RepositoryId = repository.Id,
            TargetType = isDatabase ? BackupTargetType.Database : BackupTargetType.Directory,
            TargetRef = job.Destination,
            Status = BackupSnapshotStatus.Pending,
            TriggeredBy = BackupTrigger.Safety,
            TriggeredByUserId = job.RequestedByUserId == Guid.Empty ? null : job.RequestedByUserId,
            // The restore's own correlation id, so the copy and the restore that forced it read as
            // one event in the log rather than as two unrelated ones.
            CorrelationId = job.CorrelationId
        };

        db.BackupSnapshots.Add(snapshot);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e)
            when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The partial unique index over active snapshots refused: something is already backing
            // this destination up. Detached rather than ChangeTracker.Clear() — the restore row is
            // tracked in this same context and the caller is about to write a refusal onto it, which
            // a wholesale clear would silently throw away.
            db.Entry(snapshot).State = EntityState.Detached;
            return new SafetyCopy(false, Error: "A backup of this destination is already running.");
        }

        try
        {
            // Acquired inside the try, because a stager that throws must still leave a settled row:
            // the partial unique index counts Pending as active, and a row stuck there would refuse
            // every later backup of this destination until a restart reconciled it.
            await using var lease = await AcquireDestinationAsync(job, snapshot.Id, ct);

            if (!lease.Succeeded)
            {
                await AbandonSafetyCopyAsync(snapshot, lease.Error!, ct);
                return new SafetyCopy(false, Error: lease.Error);
            }

            SnapshotLifecycle.Transition(snapshot, BackupSnapshotStatus.Preparing);
            snapshot.StartedAt = DateTimeOffset.UtcNow;
            // Only a database destination is materialised into staging; a directory is read where it
            // stands, and its "staged copy" is the operator's own live data, never ours to delete.
            snapshot.StagingPath = isDatabase ? lease.SourcePath : null;
            SnapshotLifecycle.Transition(snapshot, BackupSnapshotStatus.Running);
            await db.SaveChangesAsync(ct);

            var engine = engines.Resolve(repository.Engine);
            var result = await engine.CreateSnapshotAsync(new CreateBackupSnapshotRequest(
                repository.Id, snapshot.Id, lease.SourcePath!, password,
                snapshot.TargetType, snapshot.TargetRef), ct);

            if (!result.Succeeded)
            {
                await AbandonSafetyCopyAsync(snapshot, result.Error ?? "The copy failed.", ct);
                return new SafetyCopy(false, Error: result.Error ?? "The copy failed.");
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
            snapshot.StagingPath = null;

            SnapshotLifecycle.Transition(snapshot, result.Warnings is { Count: > 0 }
                ? BackupSnapshotStatus.CompletedWithWarnings
                : BackupSnapshotStatus.Completed);

            repository.SnapshotCount++;
            repository.StorageUsageBytes += result.StoredSizeBytes;

            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Copied {Destination} aside as backup {SnapshotId} before restore {RestoreId}. [{Correlation}]",
                job.Destination, snapshot.Id, job.Id, job.CorrelationId);

            // The copy exists and is written down. Nothing else happens in here — the caller records
            // where it is FIRST and asks for it to be verified afterwards, because those two are not
            // worth the same and only one of them can fail.
            return new SafetyCopy(true, snapshot.Id);
        }
        catch (OperationCanceledException)
        {
            // Settled rather than left where the shutdown found it. The row is Pending, Preparing or
            // Running, all of which the partial unique index counts as an ACTIVE backup of this
            // destination — so an abandoned one refuses the next backup of it until a restart
            // reconciles the row. Saved on CancellationToken.None, because the token that stopped the
            // work cannot also be the one that records it stopping.
            SnapshotLifecycle.Transition(snapshot, BackupSnapshotStatus.Cancelled);
            snapshot.CompletedAt = DateTimeOffset.UtcNow;
            snapshot.StagingPath = null;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The safety copy for restore {RestoreId} could not be taken.", job.Id);
            await AbandonSafetyCopyAsync(snapshot, ex.Message, ct);
            return new SafetyCopy(false, Error: ex.Message);
        }
    }

    /// <summary>
    /// Where the pre-restore copy is read from.
    ///
    /// <para>
    /// A database destination is dumped through its own client, which is what
    /// <see cref="IBackupTargetResolver"/> already does for a database backup target — tarring a
    /// running database's files produces a torn copy, and a torn way back is not one.
    /// </para>
    /// <para>
    /// A filesystem destination is read where it stands, and deliberately does NOT go through that
    /// resolver. Its directory gate answers a different question — "which directories may an
    /// operator point a BACKUP at", <c>BackupModuleOptions.AllowedSourceRoots</c> — and the restore
    /// root is not normally one of them. Routing this through it would refuse every overwriting
    /// restore on a default install, which is the opposite of the protection being added here. The
    /// path is re-confined to the restore root instead, because the row was written earlier and the
    /// option could have been changed since.
    /// </para>
    /// </summary>
    private async Task<TargetLease> AcquireDestinationAsync(
        RestoreJob job, Guid safetySnapshotId, CancellationToken ct)
    {
        if (job.RestoreType is RestoreType.Database)
            return await targets.AcquireAsync(
                BackupTargetType.Database, job.Destination, safetySnapshotId, ct);

        var check = PathGuard.ResolveWithin(_options.RestoreRoot, job.Destination);
        if (!check.Allowed)
            return TargetLease.Fail(
                $"The destination is no longer inside {_options.RestoreRoot} ({check.Rejection}).");

        return TargetLease.Ok(check.ResolvedPath!);
    }

    /// <summary>
    /// Settles a safety copy that did not finish.
    ///
    /// <para>
    /// No notification: the restore's own refusal is about to be sent and carries this reason inside
    /// it. Two alerts for one event would train an operator to read neither.
    /// </para>
    /// </summary>
    private async Task AbandonSafetyCopyAsync(
        BackupSnapshot snapshot, string reason, CancellationToken ct)
    {
        SnapshotLifecycle.Transition(snapshot, BackupSnapshotStatus.Failed);
        snapshot.FailureReason = reason;
        snapshot.CompletedAt = DateTimeOffset.UtcNow;
        // The lease cleans up behind the caller, so nothing is left for the reconciler to sweep.
        snapshot.StagingPath = null;
        await db.SaveChangesAsync(ct);
    }

    private async Task FailAsync(RestoreJob job, string reason, CancellationToken ct)
    {
        var told = WithTheWayBack(reason, job.SafetySnapshotRef);

        job.Status = RestoreJobStatus.Failed;
        job.FailureReason = told;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("backup.restore.failed", "RestoreJob", job.Id.ToString(),
            userIdOverride: job.RequestedByUserId, ct: ct);

        // The alert carries the same sentence as the row. Someone woken by a critical notification
        // at 03:00 is the last person who should have to open the panel to find out whether there
        // is anything to put back.
        await notifications.SendAsync(new BackupNotification(
            job.WorkspaceId,
            BackupNotificationKind.RestoreFailed,
            BackupNotificationSeverity.Critical,
            "Restore failed",
            told,
            SnapshotId: job.SnapshotId), ct);
    }

    /// <summary>
    /// Adds where the way back is to a failure that has one.
    ///
    /// <para>
    /// A restore that failed <b>after</b> the copy was taken is the moment the reference earns its
    /// keep: the destination may be half-written, and the operator reading the failure should not
    /// then have to go and work out whether anything was preserved. The Backup Center links the same
    /// snapshot beside the restore; this puts it in the sentence.
    /// </para>
    /// <para>
    /// The reason is trimmed to make room rather than the whole string being cut at the end. An
    /// engine's message can be long, and losing the pointer to the only copy of the previous
    /// contents is the one part of this sentence that must survive.
    /// </para>
    /// <para>
    /// The bound is then applied to the <b>result</b>, not to the reason. Trimming only the reason
    /// is the preference — which half to sacrifice — and it was standing in for the guarantee, which
    /// is that whatever is returned will save. A reference long enough to leave the reason no room
    /// at all took the preference to zero and returned a suffix longer than the column, so the
    /// method's one hard promise rested on <c>SafetySnapshotRef</c> happening to be short. It is
    /// capped at 1024 characters and so it always is; that is a fact about another file.
    /// </para>
    /// </summary>
    public static string WithTheWayBack(string reason, string? safetySnapshotRef)
    {
        if (string.IsNullOrWhiteSpace(safetySnapshotRef)) return Clamp(reason);

        var suffix = " The destination as it was just before this restore started was copied to " +
                     $"backup {safetySnapshotRef}; restore from it in the Backup Center to put " +
                     "things back.";

        var room = FailureReasonLimit - suffix.Length;
        var told = room <= 0 ? suffix : (reason.Length > room ? reason[..room] : reason) + suffix;

        return Clamp(told);
    }

    /// <summary>The column is the authority; nothing leaves here longer than it.</summary>
    private static string Clamp(string reason) =>
        reason.Length > FailureReasonLimit ? reason[..FailureReasonLimit] : reason;

    public Task<List<RestoreJob>> ListAsync(int take, CancellationToken ct) =>
        db.RestoreJobs.AsNoTracking().OrderByDescending(r => r.CreatedAt).Take(take).ToListAsync(ct);
}
