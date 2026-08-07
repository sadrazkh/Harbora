using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Harbora.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>What one pass settled. Returned so a test — and a log line — can be specific.</summary>
public sealed record BackupReconciliation(int Snapshots, int Restores, int StagingPathsSwept)
{
    public static readonly BackupReconciliation None = new(0, 0, 0);
}

/// <summary>
/// Settles the module's own rows that a crash or restart left mid-flight.
///
/// <para>
/// <c>JobReconciler</c> settles the orphaned <c>Job</c> row, which is the platform's half of the
/// story. Nothing settled the <c>BackupSnapshot</c>, and the consequence was the worst failure a
/// backup product has: <c>BackupSnapshotService.QueueAsync</c> refuses a snapshot while one is
/// active for the same target, so a single hard restart mid-backup ended that target's backups
/// permanently — manual and scheduled alike, with no screen anywhere to clear it. The scheduler
/// logged a warning each tick and advanced <c>NextRunAt</c>, so protection stopped and nothing said
/// so. This is that missing counterpart.
/// </para>
/// <para>
/// Settled, not resumed. A half-written snapshot cannot be continued — the staged copy it was
/// reading is gone with the process — so the honest record is a failure that names its cause, and
/// the next scheduled run takes a whole backup. The row is kept and the reason written on it,
/// because the Backup Center showing "Failed — interrupted by a platform restart" is what turns an
/// invisible outage into something an operator can see.
/// </para>
/// <para>
/// Never blocks startup, like <c>JobReconciler</c>: a panel that will not boot because it could not
/// tidy up is a worse outcome than rows that are tidied on the next restart.
/// </para>
/// </summary>
public sealed class BackupModuleReconciler(
    IServiceScopeFactory scopeFactory,
    IOptions<BackupFeatureOptions> features,
    IOptions<BackupModuleOptions> options,
    ISystemClock clock,
    ILogger<BackupModuleReconciler> logger) : IHostedService
{
    /// <summary>
    /// Written onto the row and shown in the Backup Center. It says what happened, and what it
    /// means for the data — a backup that did not finish is a backup that was not taken.
    /// </summary>
    public const string SnapshotInterrupted =
        "Interrupted by a platform restart before it finished. This backup was never completed, so " +
        "treat it as not taken — the next backup of this target will run normally.";

    /// <summary>
    /// A restore is the destructive direction, so its message says the opposite thing: something
    /// may already be on disk. Nobody should read "failed" here as "nothing changed".
    /// </summary>
    public const string RestoreInterrupted =
        "Interrupted by a platform restart before it finished. Part of the data may already have " +
        "been written to the destination — check it before restoring again.";

    private readonly BackupModuleOptions _options = options.Value;

    public async Task StartAsync(CancellationToken ct)
    {
        // The module's other hosted services gate on the same flag. A module that is off owns no
        // rows and must not settle any.
        if (!features.Value.Backup)
        {
            logger.LogInformation("Backup module is off; its rows are not being reconciled.");
            return;
        }

        try { await ReconcileAsync(ct); }
        catch (Exception ex)
        {
            // Never block startup on reconciliation.
            logger.LogError(ex, "Backup reconciliation failed on startup.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task<BackupReconciliation> ReconcileAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

        // IgnoreQueryFilters, stated rather than relied upon. A background scope reports itself
        // unscoped and the filters fall away anyway — but a version of this that somehow ran inside
        // a request scope would read an EMPTY set, settle nothing, and log a successful pass. That
        // is the exact shape of the failure it exists to fix.
        var snapshots = await db.BackupSnapshots.IgnoreQueryFilters()
            .Where(s => s.Status == BackupSnapshotStatus.Pending
                        || s.Status == BackupSnapshotStatus.Preparing
                        || s.Status == BackupSnapshotStatus.Running)
            .ToListAsync(ct);

        var restores = await db.RestoreJobs.IgnoreQueryFilters()
            .Where(r => r.Status == RestoreJobStatus.Pending || r.Status == RestoreJobStatus.Running)
            .ToListAsync(ct);

        if (snapshots.Count == 0 && restores.Count == 0) return BackupReconciliation.None;

        logger.LogWarning(
            "Settling {Snapshots} backup(s) and {Restores} restore(s) left mid-flight by a restart.",
            snapshots.Count, restores.Count);

        var now = clock.UtcNow;
        var swept = 0;

        foreach (var snapshot in snapshots)
        {
            // Through the lifecycle rather than by assignment, so an unexpected source state is a
            // named exception instead of a silently rewritten history.
            SnapshotLifecycle.Transition(snapshot, BackupSnapshotStatus.Failed);
            snapshot.FailureReason = SnapshotInterrupted;
            snapshot.CompletedAt = now;

            if (Sweep(snapshot.StagingPath)) swept++;
            snapshot.StagingPath = null;
        }

        foreach (var job in restores)
        {
            job.Status = RestoreJobStatus.Failed;
            job.FailureReason = RestoreInterrupted;
            job.CompletedAt = now;

            // Only a database restore stages inside the module's own directory, under a name
            // derived from this row's id. A file restore writes straight to its destination, and
            // what a half-finished restore already put there is the operator's to inspect — not
            // this method's to delete.
            if (job.RestoreType is RestoreType.Database
                && Sweep(Path.Combine(_options.StagingDirectory, $"dbrestore-{job.Id:N}")))
                swept++;
        }

        await db.SaveChangesAsync(ct);
        return new BackupReconciliation(snapshots.Count, restores.Count, swept);
    }

    /// <summary>
    /// Removes one abandoned staging copy.
    ///
    /// <para>
    /// This method deletes recursively from a path read out of a database row, so containment is
    /// the whole design. <see cref="PathGuard"/> resolves first and compares after, which is what
    /// makes "..", a symlinked parent and a plain absolute path all fail the same way; and the
    /// staging root itself is refused separately, because <c>PathGuard</c> rightly allows the root
    /// as a destination and deleting it would take every other target's staged copy along.
    /// </para>
    /// </summary>
    private bool Sweep(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var check = PathGuard.ResolveWithin(_options.StagingDirectory, path);
        if (!check.Allowed)
        {
            logger.LogWarning(
                "Left {Path} alone: it is not inside {Root} ({Rejection}).",
                path, _options.StagingDirectory, check.Rejection);
            return false;
        }

        var resolved = check.ResolvedPath!;
        if (string.Equals(resolved.TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(_options.StagingDirectory).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.Ordinal))
        {
            logger.LogWarning("Left the staging root {Root} alone; only its contents are sweepable.",
                _options.StagingDirectory);
            return false;
        }

        try
        {
            if (Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
                logger.LogInformation("Removed the staged copy a restart left at {Path}.", resolved);
                return true;
            }

            if (File.Exists(resolved))
            {
                File.Delete(resolved);
                logger.LogInformation("Removed the staged archive a restart left at {Path}.", resolved);
                return true;
            }
        }
        catch (Exception ex)
        {
            // Logged loudly rather than thrown: what is left behind is plaintext application data,
            // which is worth someone noticing — but not worth failing the rest of the pass over.
            logger.LogWarning(ex, "A staged copy could not be removed from {Path}.", resolved);
        }

        return false;
    }
}
