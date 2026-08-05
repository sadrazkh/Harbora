using Harbora.Modules.Backup.Contracts;

namespace Harbora.Modules.Backup.Domain;

/// <summary>
/// The states a snapshot may move between.
///
/// <para>
/// Centralised rather than left to each caller assigning <c>Status</c> freely. The transition that
/// matters is the one that must NOT happen: a Failed snapshot moved back to Running loses the reason
/// it failed, and a Completed one moved back to Running claims data that is no longer being written.
/// Both are easy to write by accident in a retry path.
/// </para>
/// </summary>
public static class SnapshotLifecycle
{
    private static readonly Dictionary<BackupSnapshotStatus, BackupSnapshotStatus[]> Allowed = new()
    {
        [BackupSnapshotStatus.Pending] =
        [
            BackupSnapshotStatus.Preparing, BackupSnapshotStatus.Running,
            BackupSnapshotStatus.Cancelled, BackupSnapshotStatus.Failed
        ],
        [BackupSnapshotStatus.Preparing] =
        [
            BackupSnapshotStatus.Running, BackupSnapshotStatus.Cancelled, BackupSnapshotStatus.Failed
        ],
        [BackupSnapshotStatus.Running] =
        [
            BackupSnapshotStatus.Verifying, BackupSnapshotStatus.Completed,
            BackupSnapshotStatus.CompletedWithWarnings, BackupSnapshotStatus.Cancelled,
            BackupSnapshotStatus.Failed
        ],
        [BackupSnapshotStatus.Verifying] =
        [
            BackupSnapshotStatus.Completed, BackupSnapshotStatus.CompletedWithWarnings,
            BackupSnapshotStatus.Failed
        ],

        // A finished snapshot's only remaining move is deletion. Notably NOT back to Running:
        // re-running produces a NEW snapshot rather than reopening an old one, so that the history
        // of what was taken when stays true.
        [BackupSnapshotStatus.Completed] = [BackupSnapshotStatus.Deleting],
        [BackupSnapshotStatus.CompletedWithWarnings] = [BackupSnapshotStatus.Deleting],
        [BackupSnapshotStatus.Failed] = [BackupSnapshotStatus.Deleting],
        [BackupSnapshotStatus.Cancelled] = [BackupSnapshotStatus.Deleting],

        // Deleting may fall back if the engine refuses, so the row does not sit in a state that
        // implies the data is gone when it is still there.
        [BackupSnapshotStatus.Deleting] = [BackupSnapshotStatus.Deleted, BackupSnapshotStatus.Failed],
        [BackupSnapshotStatus.Deleted] = []
    };

    public static bool CanTransition(BackupSnapshotStatus from, BackupSnapshotStatus to)
    {
        // Re-applying the state a snapshot is already in is how an idempotent job retry behaves.
        // Treating that as illegal would make every crash-and-resume look like a bug.
        if (from == to) return true;

        return Allowed.TryGetValue(from, out var next) && Array.IndexOf(next, to) >= 0;
    }

    /// <summary>Moves the snapshot, or throws with a message naming both states.</summary>
    public static void Transition(BackupSnapshot snapshot, BackupSnapshotStatus to)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!CanTransition(snapshot.Status, to))
            throw new InvalidOperationException(
                $"A snapshot cannot go from {snapshot.Status} to {to}.");

        snapshot.Status = to;
        snapshot.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
