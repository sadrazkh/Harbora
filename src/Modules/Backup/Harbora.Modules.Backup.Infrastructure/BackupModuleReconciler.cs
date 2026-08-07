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

/// <summary>What one attempt to remove a staged copy proved about it.</summary>
public enum SweepResult
{
    /// <summary>Nothing was there — no path recorded, or the path had already gone.</summary>
    Gone,

    /// <summary>It was there and it is not any more.</summary>
    Removed,

    /// <summary>
    /// Not ours to delete — outside the staging root, or the root itself — <b>and something is
    /// still there</b>.
    ///
    /// <para>
    /// Both halves are checked, because they are different questions. The first is a permanent
    /// verdict about <i>sweepability</i>. The second is <i>existence</i>, and it is asked separately
    /// rather than assumed: a pointer whose copy has already gone comes back
    /// <see cref="Gone"/> and clears itself. Without that, changing
    /// <c>BackupModuleOptions.StagingDirectory</c> — the realistic way a stored path ends up outside
    /// the root, and an ordinary operational act — would strand every pointer written under the old
    /// root and warn about them on every boot forever, including about copies some housekeeping job
    /// removed years ago. A warning nobody can act on is a warning everybody learns to ignore.
    /// </para>
    /// <para>
    /// When it <i>is</i> still there, the row keeps pointing at it and the warning does repeat every
    /// restart until a person deals with it. That copy is the least discoverable one there is,
    /// because it is the one listing the staging root will never show, so the repetition is the
    /// feature.
    /// </para>
    /// </summary>
    Refused,

    /// <summary>
    /// The delete was attempted and threw. Transient — a busy mount at 03:00 is exactly the case a
    /// retry fixes — so the row keeps pointing at it.
    /// </summary>
    Failed
}

/// <summary>One path the sweep looked at, and what it found.</summary>
public sealed record SweepAttempt(string Path, SweepResult Result);

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
/// <b>Why <see cref="IHostedLifecycleService"/> and not plain <c>IHostedService</c>.</b> Hosted
/// services run their <c>StartAsync</c> in registration order, and this module is registered after
/// <c>AddHarboraInfrastructure</c> — which is where <c>JobStartupGateOpener</c> releases the job
/// worker. Reconciling in <c>StartAsync</c> would therefore race a worker that was already claiming:
/// <c>BackupSnapshot</c> carries no concurrency token, so EF writes every changed column, and a
/// worker that completed a snapshot between this pass's read and its save would have that result
/// overwritten back to <c>Failed</c> — a backup that exists, with data in the repository, recorded
/// as not taken. The host runs <c>StartingAsync</c> on <b>every</b> hosted service before
/// <b>any</b> <c>StartAsync</c>, so doing the work there puts this pass ahead of the gate whatever
/// the registration order is, and the guarantee lives in this file rather than in a comment
/// somewhere else.
/// </para>
/// <para>
/// Never <i>fails</i> startup, like <c>JobReconciler</c>: a panel that will not boot because it
/// could not tidy up is a worse outcome than rows that are tidied on the next restart.
/// </para>
/// <para>
/// <b>Boot does, however, wait on this — deliberately.</b> Kestrel's <c>GenericWebHostService</c> is
/// a plain <c>IHostedService</c>, so running the pass in <c>StartingAsync</c> puts all of it,
/// including a recursive delete over a partly-staged directory, ahead of the port opening. Nothing
/// configures <c>HostOptions.StartupTimeout</c>, whose default is
/// <c>Timeout.InfiniteTimeSpan</c>, so a slow pass delays the listener rather than aborting the
/// host; a health check polling the panel sees nothing until it finishes. That is the right trade
/// here for three reasons:
/// </para>
/// <list type="number">
/// <item>A clean shutdown costs nothing at all. With no stranded row and no row claiming a staged
/// copy, the pass returns <see cref="BackupReconciliation.None"/> before it touches the disk.</item>
/// <item>What it deletes is <b>plaintext application data</b>. Answering requests while an
/// unencrypted copy of somebody's database sits in staging is the worse state to be in, and every
/// minute this is deferred is exposure that has already been paid for once by the crash.</item>
/// <item>A timeout would not bound the work anyway. <c>Directory.Delete(recursive)</c> takes no
/// <c>CancellationToken</c>, so the single large tree — the only delete that could run for minutes —
/// cannot be interrupted once it has begun. A budget could skip only paths it had not started, which
/// does not remove the wait; it moves the leaked copy to a later restart and leaves it on disk in
/// the meantime.</item>
/// </list>
/// <para>
/// <b>And here is the case those three do not cover, which is the one worth knowing about.</b> A
/// pass used to be bounded by what was in flight: the partial unique index allows at most one active
/// snapshot per target, so a crash strands roughly one row per backup that was running, each naming
/// at most four paths. <see cref="RemainingStagingPath"/> ended that. A path that comes back
/// <see cref="SweepResult.Failed"/> keeps its pointer, and the query at the top of
/// <see cref="ReconcileAsync"/> loads those rows again on the next boot — so the swept set is now
/// bounded by <i>unresolved leaks</i>, not by what was in flight, and it does not shrink until
/// either the copy goes away or a person intervenes. A <c>Failed</c> path re-runs
/// <c>Directory.Delete(recursive: true)</c> at <b>every</b> start, and a <c>Refused</c> one re-runs
/// an existence check at every start.
/// </para>
/// <para>
/// That matters for exactly one failure, and it is not the slow one: a delete that <b>blocks</b>
/// rather than throws. A wedged NFS or SMB mount does not return an error, it hangs — and
/// <c>Directory.Exists</c> hangs on it too. With <c>StartupTimeout</c> infinite and no
/// <c>CancellationToken</c> to hand, that is a panel that never finishes starting, on every restart,
/// self-healing on none of them. The retained pointer is what leads it back there: a path that
/// errored on one boot and wedged before the next is a path this pass would not have looked at at
/// all before the retry existed.
/// </para>
/// <para>
/// Named rather than bounded, deliberately. A retry cap over <c>Failed</c> is the obvious bound and
/// does not bound this one: an attempt that never returns is never counted, so the count would have
/// to be persisted <i>before</i> the delete is attempted — a new column, a migration, and an extra
/// write on the boot path — and even then it would hang boot N times before giving up. The bound
/// that works is structural, and it is the next paragraph.
/// </para>
/// <para>
/// What would change the answer — and one half of it already has. The set this sweeps no longer
/// scales only with what was in flight; it scales with leaks nobody has cleared. If that set is ever
/// observed to be non-trivial, or a staging directory is ever put on a network mount, the disk half
/// belongs after the listener binds and the row-settling half must stay here — settling is what
/// races the job worker; sweeping does not. That is a change to the ordering guarantee this class
/// exists to make, so it belongs to whoever makes it deliberately rather than to a sweep retry.
/// </para>
/// <para>
/// <b>What this assumes about topology.</b> One panel process per database and per staging
/// directory. The paths it deletes are derived from the row (see <see cref="BackupStagingLayout"/>),
/// so two processes sharing a database and a staging directory would let the second settle rows the
/// first is actively running and delete the copy underneath it. Single-instance is the supported
/// topology — <c>docs/product-audit/13-target-architecture.md</c> §4 records the per-instance
/// constraints the platform already relies on (<c>JobCancellationRegistry</c>, <c>AlertThrottle</c>,
/// the AI rate-limit windows, <c>NodeIngressRegistry</c>), and HA is P3 — so no defence against it
/// is built here. When HA does arrive this pass needs a lease or an instance-scoped staging root,
/// and this paragraph is where to start reading.
/// </para>
/// </summary>
public sealed class BackupModuleReconciler(
    IServiceScopeFactory scopeFactory,
    IOptions<BackupFeatureOptions> features,
    IOptions<BackupModuleOptions> options,
    ISystemClock clock,
    ILogger<BackupModuleReconciler> logger) : IHostedLifecycleService
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

    /// <summary>
    /// The whole pass, before the host starts anything. See the class remark: this is what keeps
    /// the reconciler ahead of the job worker's gate regardless of registration order.
    /// </summary>
    public async Task StartingAsync(CancellationToken ct)
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
            // Never FAIL startup on reconciliation — a panel that will not boot because it could
            // not tidy up is worse than rows tidied on the next restart. Startup does wait on this
            // pass, on purpose; the class remark is where that trade is argued and where the case
            // it does not cover is named.
            logger.LogError(ex, "Backup reconciliation failed on startup.");
        }
    }

    // Everything is done by the time the host starts services; these exist to satisfy the interface.
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// <c>StagingPath</c> is the row's standing claim that a plaintext copy still exists at that
    /// path. It is cleared whenever the sweep established the copy is gone, and kept otherwise —
    /// whether the delete was attempted and threw (<see cref="SweepResult.Failed"/>, retried next
    /// restart) or was never ours to attempt (<see cref="SweepResult.Refused"/>, which needs a
    /// person). The question the column answers is "is there still a copy at this path", and both
    /// of those answer yes <i>having checked</i>: <c>Refused</c> is only returned once the path has
    /// been confirmed to still hold something, so a stale pointer comes back
    /// <see cref="SweepResult.Gone"/> and clears itself. Clearing unconditionally would throw away
    /// the only pointer to a leaked copy of somebody's application data; never clearing would warn
    /// forever about copies that are not there.
    /// </summary>
    public static string? RemainingStagingPath(IReadOnlyList<SweepAttempt> attempts) =>
        attempts.FirstOrDefault(a => a.Result is SweepResult.Failed or SweepResult.Refused)?.Path;

    /// <summary>
    /// The statuses a restart can strand. Named here rather than inlined because the queue guard,
    /// the partial unique index and this pass must agree on exactly one set — <c>Verifying</c> is
    /// deliberately outside it: it does not block the next backup, and settling it here would
    /// overwrite a verification that is legitimately in flight.
    /// </summary>
    private static bool IsStranded(BackupSnapshotStatus status) =>
        status is BackupSnapshotStatus.Pending
            or BackupSnapshotStatus.Preparing
            or BackupSnapshotStatus.Running;

    public async Task<BackupReconciliation> ReconcileAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

        // IgnoreQueryFilters, stated rather than relied upon. A background scope reports itself
        // unscoped and the filters fall away anyway — but a version of this that somehow ran inside
        // a request scope would read an EMPTY set, settle nothing, and log a successful pass. That
        // is the exact shape of the failure it exists to fix.
        //
        // The StagingPath term picks up rows that are ALREADY terminal and still claim a staged
        // copy: that is a delete this reconciler tried and could not do, or would not do, and the
        // next restart is exactly when to look at it again.
        //
        // The OR makes this a sequential scan — no index serves both branches — and that is
        // accepted rather than overlooked. It runs ONCE PER PROCESS START, which is the whole
        // justification: a few milliseconds against a table of tens of thousands of rows, paid at
        // boot, versus an index maintained on every snapshot insert and update forever.
        //
        // Not "a table pruned by retention": BackupRetentionService.PruneAsync filters on
        // PolicyId == policyId, so only policy-driven snapshots are ever pruned. A manual or
        // API-triggered snapshot has no policy and is kept until somebody deletes it, so this table
        // does grow without bound in the general case. That does not change the conclusion at any
        // realistic size, and it does change when to revisit it: if BackupSnapshots ever reaches a
        // size where one sequential scan at boot is measurable, not if pruning changes.
        var snapshots = await db.BackupSnapshots.IgnoreQueryFilters()
            .Where(s => s.Status == BackupSnapshotStatus.Pending
                        || s.Status == BackupSnapshotStatus.Preparing
                        || s.Status == BackupSnapshotStatus.Running
                        || s.StagingPath != null)
            .ToListAsync(ct);

        var restores = await db.RestoreJobs.IgnoreQueryFilters()
            .Where(r => r.Status == RestoreJobStatus.Pending || r.Status == RestoreJobStatus.Running)
            .ToListAsync(ct);

        if (snapshots.Count == 0 && restores.Count == 0) return BackupReconciliation.None;

        var stranded = snapshots.Count(s => IsStranded(s.Status));

        if (stranded > 0 || restores.Count > 0)
            logger.LogWarning(
                "Settling {Snapshots} backup(s) and {Restores} restore(s) left mid-flight by a restart.",
                stranded, restores.Count);

        var now = clock.UtcNow;
        var swept = 0;

        foreach (var snapshot in snapshots)
        {
            if (IsStranded(snapshot.Status))
            {
                // Through the lifecycle rather than by assignment, so an unexpected source state is
                // a named exception instead of a silently rewritten history.
                SnapshotLifecycle.Transition(snapshot, BackupSnapshotStatus.Failed);
                snapshot.FailureReason = SnapshotInterrupted;
                snapshot.CompletedAt = now;
            }

            var attempts = SweepAll(StagingArtifactsOf(snapshot));
            swept += attempts.Count(a => a.Result is SweepResult.Removed);
            snapshot.StagingPath = RemainingStagingPath(attempts);
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
                && Sweep(Path.Combine(
                    _options.StagingDirectory,
                    BackupStagingLayout.DatabaseRestoreDirectory(job.Id))) is SweepResult.Removed)
                swept++;
        }

        await db.SaveChangesAsync(ct);
        return new BackupReconciliation(stranded, restores.Count, swept);
    }

    /// <summary>
    /// Every temporary copy this snapshot could have left on disk.
    ///
    /// <para>
    /// Three sources, and each covers a window the others do not:
    /// </para>
    /// <list type="bullet">
    /// <item><c>StagingPath</c> — what the row actually recorded, once the copy had finished.</item>
    /// <item>The staged directory <see cref="BackupStagingLayout"/> names from the snapshot id —
    /// present from the moment the copy STARTS, which is the window <c>StagingPath</c> cannot cover
    /// because the row is not written until the copy returns.</item>
    /// <item>The engine's own <c>{id}.tar.gz</c> and its encrypted twin. Those are removed in a
    /// <c>finally</c>, which is precisely what a kill skips, and the unencrypted one is a plaintext
    /// archive of the entire target.</item>
    /// </list>
    /// </summary>
    private List<string> StagingArtifactsOf(BackupSnapshot snapshot)
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(snapshot.StagingPath)) paths.Add(snapshot.StagingPath);

        if (BackupStagingLayout.StagedDirectoryFor(snapshot.TargetType, snapshot.Id) is { } staged)
            paths.Add(Path.Combine(_options.StagingDirectory, staged));

        paths.Add(Path.Combine(_options.StagingDirectory, BackupStagingLayout.ArchiveFile(snapshot.Id)));
        paths.Add(Path.Combine(_options.StagingDirectory, BackupStagingLayout.EncryptedArchiveFile(snapshot.Id)));

        // A row whose StagingPath already IS the derived directory must not be swept twice, or a
        // successful delete would be counted once and then reported as "still there".
        return paths.Distinct(StringComparer.Ordinal).ToList();
    }

    private List<SweepAttempt> SweepAll(IEnumerable<string> paths) =>
        paths.Select(p => new SweepAttempt(p, Sweep(p))).ToList();

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
    private SweepResult Sweep(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return SweepResult.Gone;

        var check = PathGuard.ResolveWithin(_options.StagingDirectory, path);
        if (!check.Allowed)
        {
            // Two questions, asked separately. "May this sweep delete it" is answered above and the
            // answer is no, permanently. "Is a copy still there" is a different question and one
            // stat answers it — and it has to be asked, because the realistic way a stored path ends
            // up outside the root is an operator changing StagingDirectory, not an attack. Every
            // pointer under the old root would otherwise warn on every boot forever, about copies
            // that may have been cleaned up years ago.
            if (!StillThere(path)) return SweepResult.Gone;

            logger.LogWarning(
                "Left {Path} alone: it is not inside {Root} ({Rejection}). A copy of application " +
                "data is still there and nothing here can remove it.",
                path, _options.StagingDirectory, check.Rejection);

            return SweepResult.Refused;
        }

        var resolved = check.ResolvedPath!;
        if (string.Equals(resolved.TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(_options.StagingDirectory).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.Ordinal))
        {
            // Same two questions. A staging root that is not there at all is a root nothing can be
            // leaking into, so the pointer to it is stale rather than dangerous.
            if (!StillThere(resolved)) return SweepResult.Gone;

            logger.LogWarning("Left the staging root {Root} alone; only its contents are sweepable.",
                _options.StagingDirectory);
            return SweepResult.Refused;
        }

        try
        {
            if (Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
                logger.LogInformation("Removed the staged copy a restart left at {Path}.", resolved);
                return SweepResult.Removed;
            }

            if (File.Exists(resolved))
            {
                File.Delete(resolved);
                logger.LogInformation("Removed the staged archive a restart left at {Path}.", resolved);
                return SweepResult.Removed;
            }
        }
        catch (Exception ex)
        {
            // Logged loudly rather than thrown: what is left behind is plaintext application data,
            // which is worth someone noticing — but not worth failing the rest of the pass over.
            // The row keeps the path so the next restart tries again; a mount that was busy at
            // 03:00 is exactly the case a retry fixes.
            logger.LogWarning(ex, "A staged copy could not be removed from {Path}.", resolved);
            return SweepResult.Failed;
        }

        return SweepResult.Gone;
    }

    /// <summary>
    /// Whether anything is actually at a path the sweep may not delete.
    ///
    /// <para>
    /// Resolved the way <see cref="PathGuard"/> resolves it, so a relative pointer means "under the
    /// staging root" and not "under whatever directory this process happens to have been started
    /// in". A path that cannot be resolved at all is reported as still there: clearing the row's
    /// pointer is a claim that nothing is at the other end of it, and that claim must not be made
    /// off a check that could not be performed.
    /// </para>
    /// <para>
    /// <c>Directory.Exists</c> and <c>File.Exists</c> answer false for a path that exists but cannot
    /// be read, so an unreadable copy clears its pointer. That is the same answer the in-root branch
    /// already gives — it is one stat, not a permission audit — and it is the only reading available
    /// without opening something the sweep has already decided it may not touch.
    /// </para>
    /// </summary>
    private bool StillThere(string path)
    {
        string resolved;
        try
        {
            var root = Path.GetFullPath(_options.StagingDirectory);
            resolved = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }

        return Directory.Exists(resolved) || File.Exists(resolved);
    }
}
