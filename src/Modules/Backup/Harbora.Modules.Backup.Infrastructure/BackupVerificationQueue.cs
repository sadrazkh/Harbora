using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>
/// Hands a snapshot that has just finished to the verifier.
///
/// <para>
/// One line of work, in one place, because two callers make it: the snapshot job at the end of an
/// ordinary backup, and the restore job after it has copied a destination aside. Both want the same
/// thing said the same way — including what happens when it cannot be said.
/// </para>
/// </summary>
internal static class BackupVerificationQueue
{
    /// <summary>
    /// Whether a check of this snapshot is already waiting to run.
    ///
    /// <para>
    /// <b>Pending only, deliberately.</b> A check that is already <i>running</i> answers a question
    /// asked before it started, so an operator pressing "verify now" while one is in flight is
    /// asking for a fresh answer and gets one — the queue's per-(kind, target) exclusion makes the
    /// two run one after the other rather than at once. A check that has not started yet, on the
    /// other hand, will answer every press that arrives before it does, so five presses are one
    /// browse of one archive instead of five.
    /// </para>
    /// </summary>
    public static Task<bool> AlreadyQueuedAsync(
        HarboraDbContext db, Guid snapshotId, CancellationToken ct) =>
        db.Jobs.AnyAsync(j => j.Kind == JobKind.BackupVerify
                              && j.TargetId == snapshotId
                              && j.Status == JobStatus.Pending, ct);

    /// <summary>
    /// Enqueue the check, and never let failing to enqueue it cost the backup — or anything the
    /// caller is about to write.
    ///
    /// <para>
    /// The snapshot is complete and its row is saved before this is called. If the insert of the job
    /// row fails, the honest outcome is a backup that exists, is recorded, and has not been checked —
    /// which the Backup Center already shows as "not verified", with a button to ask again. Throwing
    /// instead would mark the JOB failed for work that succeeded, and the retry would find the
    /// snapshot terminal and do nothing: a completed backup permanently reported as a failure.
    /// </para>
    /// <para>
    /// Cancellation is swallowed with everything else, deliberately. A shutdown arriving here would
    /// otherwise reach <c>BackupSnapshotService.RunAsync</c>'s cancellation handler, which moves the
    /// snapshot to <c>Cancelled</c> — a transition <c>SnapshotLifecycle</c> forbids from
    /// <c>Completed</c>, so the process would trade an unverified backup for an exception on the way
    /// out. Nothing is lost by stopping here: the backup is already written down.
    /// </para>
    /// <para>
    /// <b>Why the context is a parameter.</b> <c>DatabaseJobQueue.AddAsync</c> adds the <c>Job</c> to
    /// the <b>caller's</b> scoped context and saves it there, and EF Core leaves a failed
    /// <c>Added</c> entity tracked. Swallowing the exception without detaching it therefore does not
    /// end the failure — it postpones it onto whatever the caller saves next, which in the restore's
    /// case was the write recording <c>SafetySnapshotRef</c>. That is the one field whose loss this
    /// method's whole justification depends on not happening: a restore that DID have a way back
    /// would have been settled as one that had none. So the leaked insert is detached here, where
    /// the swallow is, rather than left to an unstated "nothing may save after this" rule that the
    /// two call sites had no way to state and a third would not have known about.
    /// </para>
    /// </summary>
    public static async Task RequestAsync(
        IJobQueue jobs, HarboraDbContext db, Guid snapshotId, Guid workspaceId, ILogger logger,
        CancellationToken ct)
    {
        try
        {
            if (await AlreadyQueuedAsync(db, snapshotId, ct))
            {
                logger.LogDebug(
                    "Snapshot {SnapshotId} is already waiting to be verified; not asking twice.",
                    snapshotId);
                return;
            }

            await jobs.EnqueueAsync(JobKind.BackupVerify, snapshotId, workspaceId, ct);
        }
        catch (Exception ex)
        {
            Forget(db);

            logger.LogWarning(ex,
                "Snapshot {SnapshotId} finished but could not be queued for verification. The backup " +
                "itself is fine; it stays 'not verified' until someone asks for it in the Backup Center.",
                snapshotId);
        }
    }

    /// <summary>
    /// Drops a job row a refused insert left tracked, so the caller's context holds only what the
    /// caller put there.
    ///
    /// <para>
    /// Only <see cref="Job"/> entries, and only <c>Added</c> ones: the caller's own rows — the
    /// snapshot, the restore, the repository counters — are tracked in this same context and are the
    /// entire reason this is not <c>ChangeTracker.Clear()</c>. The same distinction
    /// <c>RestoreService.TakeSafetyCopyAsync</c> makes when the unique index refuses its snapshot.
    /// </para>
    /// </summary>
    private static void Forget(HarboraDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries<Job>().Where(e => e.State == EntityState.Added))
            entry.State = EntityState.Detached;
    }
}
