using Harbora.Application.Abstractions;
using Harbora.Domain.Jobs;
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
    /// Enqueue the check, and never let failing to enqueue it cost the backup.
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
    /// </summary>
    public static async Task RequestAsync(
        IJobQueue jobs, Guid snapshotId, ILogger logger, CancellationToken ct)
    {
        try
        {
            await jobs.EnqueueAsync(JobKind.BackupVerify, snapshotId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Snapshot {SnapshotId} finished but could not be queued for verification. The backup " +
                "itself is fine; it stays 'not verified' until someone asks for it in the Backup Center.",
                snapshotId);
        }
    }
}
