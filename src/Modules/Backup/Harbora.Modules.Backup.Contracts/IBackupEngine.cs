namespace Harbora.Modules.Backup.Contracts;

/// <summary>
/// The storage engine behind a backup repository: repositories, snapshots and bytes.
///
/// <para>
/// NOT the same thing as <c>Harbora.Application.Abstractions.IBackupEngine</c>, which is the panel's
/// original target-oriented service ("back up this app to that destination"). This one sits BELOW
/// that: it knows nothing about apps, managed services or workspaces, and is implemented once per
/// storage engine. See <c>docs/backup-sync/ARCHITECTURE.md</c> § 2.
/// </para>
///
/// <para>
/// Implementations must not log secrets, must not build process arguments by string concatenation,
/// and must confine every path they are given (see <c>docs/backup-sync/THREAT_MODEL.md</c> T1/T2/T3).
/// </para>
/// </summary>
public interface IBackupEngine
{
    /// <summary>Which repositories this adapter can serve. Used to resolve an adapter per repository.</summary>
    BackupEngineKind Kind { get; }

    /// <summary>
    /// Create the repository if it does not exist, or connect to it if it does.
    ///
    /// <para>
    /// Idempotent by contract: a repository that already exists is connected to, not recreated.
    /// Re-initialising an existing repository would orphan every snapshot already in it.
    /// </para>
    /// </summary>
    Task<BackupRepositoryResult> CreateRepositoryAsync(
        CreateBackupRepositoryRequest request,
        CancellationToken cancellationToken);

    /// <summary>Snapshot a source path into the repository.</summary>
    Task<BackupSnapshotResult> CreateSnapshotAsync(
        CreateBackupSnapshotRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Restore all or part of a snapshot.
    ///
    /// <para>
    /// Destructive when the destination holds live data. Callers are responsible for having taken
    /// the user's explicit confirmation; the engine is responsible for honouring
    /// <see cref="RestoreBackupRequest.ConflictStrategy"/> exactly.
    /// </para>
    /// </summary>
    Task<RestoreResult> RestoreAsync(
        RestoreBackupRequest request,
        CancellationToken cancellationToken);

    /// <summary>Is the repository reachable, and does it look intact?</summary>
    Task<BackupRepositoryHealthResult> CheckHealthAsync(
        Guid repositoryId,
        CancellationToken cancellationToken);

    /// <summary>Snapshots held in a repository, newest first.</summary>
    Task<IReadOnlyList<EngineSnapshot>> ListSnapshotsAsync(
        ListSnapshotsRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// One directory level inside a snapshot, so the restore UI can browse without downloading.
    /// <paramref name="request"/>'s path is relative to the snapshot root; empty means the root.
    /// </summary>
    Task<IReadOnlyList<EngineEntry>> BrowseSnapshotAsync(
        BrowseSnapshotRequest request,
        CancellationToken cancellationToken);

    /// <summary>Delete a snapshot and let the engine reclaim whatever is no longer referenced.</summary>
    Task<EngineOperationResult> DeleteSnapshotAsync(
        DeleteSnapshotRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Picks the adapter for a repository. Injected wherever an engine is needed, so no call site has to
/// know which engines exist.
/// </summary>
public interface IBackupEngineResolver
{
    /// <summary>The adapter for this repository's engine, or throws if it is not registered.</summary>
    IBackupEngine Resolve(BackupEngineKind kind);

    /// <summary>Engines available in this deployment — Kopia is absent when its binary is not installed.</summary>
    IReadOnlyCollection<BackupEngineKind> Available { get; }
}

// ---------------------------------------------------------------------------------------------
// Requests
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Where a repository lives and how to open it.
///
/// <para>
/// <see cref="Password"/> and <see cref="Credentials"/> are plaintext IN MEMORY ONLY — they are
/// decrypted immediately before the call and must never be logged, persisted, or placed on a command
/// line. Persisted forms live encrypted on the repository row.
/// </para>
/// </summary>
public sealed record CreateBackupRepositoryRequest(
    Guid RepositoryId,
    string Name,
    BackupRepositoryType Type,
    string Password,
    string? LocalPath = null,
    string? Endpoint = null,
    string? Bucket = null,
    string? Region = null,
    string? BasePath = null,
    RepositoryCredentials? Credentials = null);

/// <summary>Access credentials for a remote repository. Never rendered, never logged.</summary>
public sealed record RepositoryCredentials(
    string? AccessKeyId = null,
    string? SecretAccessKey = null,
    string? Username = null,
    string? Password = null,
    string? SessionToken = null);

public sealed record CreateBackupSnapshotRequest(
    Guid RepositoryId,
    Guid SnapshotId,
    string SourcePath,
    string Password,
    BackupTargetType TargetType,
    string TargetRef,
    IReadOnlyList<string>? IncludePatterns = null,
    IReadOnlyList<string>? ExcludePatterns = null,
    IReadOnlyDictionary<string, string>? Tags = null);

public sealed record RestoreBackupRequest(
    Guid RepositoryId,
    string EngineSnapshotId,
    string Password,
    string DestinationPath,
    RestoreConflictStrategy ConflictStrategy,

    // Entries to restore, relative to the snapshot root. Empty restores everything.
    IReadOnlyList<string>? Entries = null);

public sealed record ListSnapshotsRequest(Guid RepositoryId, string Password, string? TargetRef = null);

public sealed record BrowseSnapshotRequest(
    Guid RepositoryId,
    string EngineSnapshotId,
    string Password,
    string RelativePath = "");

public sealed record DeleteSnapshotRequest(Guid RepositoryId, string EngineSnapshotId, string Password);

// ---------------------------------------------------------------------------------------------
// Results
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Outcome of opening a repository.
/// <para>
/// <see cref="AlreadyExisted"/> is reported rather than hidden: "connected to your existing
/// repository" and "created a new empty one" are very different sentences to show someone who
/// expected to find their snapshots.
/// </para>
/// </summary>
public sealed record BackupRepositoryResult(
    bool Succeeded,
    Guid RepositoryId,
    bool AlreadyExisted,
    string? EngineRepositoryId = null,
    string? Error = null);

public sealed record BackupSnapshotResult(
    bool Succeeded,
    Guid SnapshotId,
    string? EngineSnapshotId = null,
    long OriginalSizeBytes = 0,
    long StoredSizeBytes = 0,
    long DeduplicatedSizeBytes = 0,
    long FilesCount = 0,
    string? Error = null,

    // Non-fatal problems, e.g. files skipped because they were unreadable.
    IReadOnlyList<string>? Warnings = null);

public sealed record RestoreResult(
    bool Succeeded,
    long RestoredFilesCount = 0,
    long RestoredBytes = 0,
    string? DestinationPath = null,
    string? Error = null,
    IReadOnlyList<string>? Warnings = null);

public sealed record BackupRepositoryHealthResult(
    bool Reachable,
    bool Intact,
    long? TotalSizeBytes = null,
    long? SnapshotCount = null,
    string? Error = null,
    DateTimeOffset? CheckedAt = null);

public sealed record EngineOperationResult(bool Succeeded, string? Error = null);

/// <summary>A snapshot as the engine reports it, before Harbora's own row is joined on.</summary>
public sealed record EngineSnapshot(
    string EngineSnapshotId,
    DateTimeOffset CreatedAt,
    string SourcePath,
    long OriginalSizeBytes,
    long FilesCount,
    IReadOnlyDictionary<string, string>? Tags = null);

/// <summary>One entry inside a snapshot, for browsing.</summary>
public sealed record EngineEntry(
    string Name,
    string RelativePath,
    bool IsDirectory,
    long SizeBytes,
    DateTimeOffset ModifiedAt);
