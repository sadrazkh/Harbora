using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Jobs;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    IJobQueue jobs,
    IBackupNotificationService notifications,
    ICurrentUser currentUser,
    IAuditLogger audit,
    IOptions<BackupModuleOptions> options,
    ILogger<RestoreService> logger)
{
    private readonly BackupModuleOptions _options = options.Value;

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

        // Two restores into one destination produce a result neither of them describes.
        var contested = await db.RestoreJobs.AnyAsync(r =>
            r.Destination == destination
            && (r.Status == RestoreJobStatus.Pending || r.Status == RestoreJobStatus.Running), ct);

        if (contested)
            return new RestoreOutcome(false, Error: "A restore into this destination is already running.");

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
        await db.SaveChangesAsync(ct);

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

            var isDatabase = job.RestoreType is RestoreType.Database;

            // A database dump lands in the staging area first, because that is the only directory
            // the database's client container can also see. It is deleted afterwards: a dump on disk
            // is the whole database in the clear.
            var filesDestination = isDatabase
                ? Path.Combine(_options.StagingDirectory, $"dbrestore-{job.Id:N}")
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

    private async Task FailAsync(RestoreJob job, string reason, CancellationToken ct)
    {
        job.Status = RestoreJobStatus.Failed;
        job.FailureReason = reason;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("backup.restore.failed", "RestoreJob", job.Id.ToString(),
            userIdOverride: job.RequestedByUserId, ct: ct);

        await notifications.SendAsync(new BackupNotification(
            job.WorkspaceId,
            BackupNotificationKind.RestoreFailed,
            BackupNotificationSeverity.Critical,
            "Restore failed",
            reason,
            SnapshotId: job.SnapshotId), ct);
    }

    public Task<List<RestoreJob>> ListAsync(int take, CancellationToken ct) =>
        db.RestoreJobs.AsNoTracking().OrderByDescending(r => r.CreatedAt).Take(take).ToListAsync(ct);
}
