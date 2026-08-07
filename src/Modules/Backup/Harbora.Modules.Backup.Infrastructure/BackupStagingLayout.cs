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
