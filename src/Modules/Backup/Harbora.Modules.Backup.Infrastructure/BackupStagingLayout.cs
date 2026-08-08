using Harbora.Infrastructure.Backups;
using Harbora.Modules.Backup.Contracts;

namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>
/// Where a snapshot's temporary copies live, decided from the snapshot's own id.
///
/// <para>
/// This exists because of a leak that nothing could find. The staging directories used to be named
/// from a <b>fresh</b> Guid minted inside the stager, and the row only learned that name after the
/// copy had finished — so a kill during the copy (the longest and likeliest window there is; it is
/// the part that moves 200 GB) left a plaintext directory on disk whose name appeared nowhere, in
/// any row, ever. Deriving the name from <c>snapshot.Id</c> instead makes the leftovers findable
/// from the row <b>before</b> the copy begins, which is the only property that makes a startup sweep
/// honest.
/// </para>
/// <para>
/// A pure decision class with no I/O, so the reconciler that deletes these paths and the stagers
/// that create them read the same rule rather than two copies of a naming convention that drift.
/// </para>
/// </summary>
public static class BackupStagingLayout
{
    /// <summary>A staged Docker volume.</summary>
    public static string VolumeDirectory(Guid snapshotId) => $"volume-{snapshotId:N}";

    /// <summary>An exported database dump, taken as the source of a snapshot.</summary>
    public static string DatabaseDirectory(Guid snapshotId) => $"db{snapshotId:N}";

    /// <summary>An assembled application: its definition plus one directory per volume.</summary>
    public static string ApplicationDirectory(Guid snapshotId) => $"app{snapshotId:N}";

    /// <summary>The dump a database <i>restore</i> lands in before it is loaded into the server.</summary>
    public static string DatabaseRestoreDirectory(Guid restoreJobId) => $"dbrestore-{restoreJobId:N}";

    /// <summary>The engine's own tar.gz for a snapshot — a plaintext archive of the whole target.</summary>
    public static string ArchiveFile(Guid snapshotId) => $"{snapshotId:N}.tar.gz";

    /// <summary>The encrypted copy of <see cref="ArchiveFile"/>, written beside it.</summary>
    public static string EncryptedArchiveFile(Guid snapshotId) =>
        ArchiveFile(snapshotId) + ArchiveCipher.Extension;

    /// <summary>
    /// The plaintext one <b>read</b> of a snapshot decrypts to, named for that read alone.
    ///
    /// <para>
    /// A read used to decrypt to a path derived from the archive's own name, so a browse and a
    /// restore of the same snapshot named the same file — and both remove it in a <c>finally</c>.
    /// One deletes or truncates what the other is part-way through reading, and the reachable
    /// symptoms are a truncated restore and a <c>Failed</c> verification with a Critical alert on a
    /// perfectly good backup. Automatic verification put those two within a window of each other on
    /// the page restores are launched from, and more than one worker can hold two verifies of one
    /// snapshot besides.
    /// </para>
    /// <para>
    /// The snapshot's id still leads, so a copy a kill leaves behind can be traced back to the row
    /// it came from — which is the whole reason this class exists. The operation id is what makes it
    /// unshared. Distinct from <see cref="ArchiveFile"/> as well, so a read of one snapshot can
    /// never name the file a backup of it is writing.
    /// </para>
    /// </summary>
    public static string ReadArchiveFile(Guid snapshotId, Guid operationId) =>
        $"{snapshotId:N}.read-{operationId:N}.tar.gz";

    /// <summary>
    /// Where one read's <i>downloaded</i> artifact lands, for the repository types that download.
    ///
    /// <para>
    /// A local repository is read where it stands, but an S3 or SFTP fetch copies the object into
    /// the shared staging directory — and used to do so under the object's own name, which is the
    /// same name for every concurrent reader of one snapshot. Nothing deletes it, so this is a torn
    /// write rather than a vanishing file; the fix is the same one.
    /// </para>
    /// </summary>
    public static string FetchedArchiveFile(Guid snapshotId, Guid operationId) =>
        ReadArchiveFile(snapshotId, operationId) + ArchiveCipher.Extension;

    /// <summary>
    /// Matches every copy a read makes, whichever of the two it is.
    ///
    /// <para>
    /// A read removes both of its files in a <c>finally</c>, and a <c>finally</c> is exactly what a
    /// kill skips. Naming them per operation is what stops two live reads colliding, and it is also
    /// what stops the leftovers of one crash overwriting the leftovers of the last — so the copies a
    /// crash abandons need somebody to come and find them, and this is the shape they have.
    /// </para>
    /// <para>
    /// Only ever matched against the staging directory, and only at a moment when no read can be in
    /// flight. See <c>BackupModuleReconciler.StartingAsync</c>.
    /// </para>
    /// </summary>
    public const string AbandonedReadPattern = "*.read-*";

    /// <summary>
    /// The staging directory a target of this kind would be copied into, or <c>null</c> when the
    /// kind stages nothing.
    ///
    /// <para>
    /// A <see cref="BackupTargetType.Directory"/> target is deliberately <c>null</c>: its "source
    /// path" is the operator's own live data, and no sweep may ever be handed a path to it. The
    /// types this module cannot read at all are null for the same reason — nothing was staged, so
    /// there is nothing to remove.
    /// </para>
    /// </summary>
    public static string? StagedDirectoryFor(BackupTargetType targetType, Guid snapshotId) =>
        targetType switch
        {
            BackupTargetType.DockerVolume => VolumeDirectory(snapshotId),
            BackupTargetType.Database => DatabaseDirectory(snapshotId),
            BackupTargetType.Application => ApplicationDirectory(snapshotId),
            _ => null
        };
}
