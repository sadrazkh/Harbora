using Harbora.Modules.Backup.Contracts;
using Harbora.Domain.Common;

namespace Harbora.Modules.Backup.Domain;

/// <summary>
/// A place snapshots are stored, and the engine that owns its format.
///
/// <para>
/// Deliberately separate from the existing <c>BackupDestination</c> rather than extra columns on it.
/// A destination is a location the current engine writes an artifact file to; a repository is a
/// managed store with its own internal structure, history and garbage collection. Folding them
/// together would put a dozen always-null columns on every existing destination row and make the two
/// restore paths hard to tell apart — which is the one place in this product ambiguity is expensive.
/// </para>
/// </summary>
public class BackupRepository : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    public string Name { get; set; } = string.Empty;

    public BackupRepositoryType Type { get; set; } = BackupRepositoryType.Local;

    /// <summary>
    /// Which engine wrote this repository. Fixed at creation: the data inside is in that engine's
    /// format, so changing it later would not convert anything — it would just stop the existing
    /// snapshots from being readable.
    /// </summary>
    public BackupEngineKind Engine { get; set; } = BackupEngineKind.Native;

    public BackupRepositoryStatus Status { get; set; } = BackupRepositoryStatus.Pending;

    /// <summary>Free-text provider label (e.g. "backblaze"), for display only.</summary>
    public string? Provider { get; set; }

    public string? Endpoint { get; set; }
    public string? Bucket { get; set; }
    public string? Region { get; set; }

    /// <summary>Prefix within the bucket, or the directory for a local repository.</summary>
    public string? BasePath { get; set; }

    /// <summary>
    /// Points at the encrypted credential, rather than holding it.
    ///
    /// <para>
    /// Access keys and repository passwords are encrypted through <c>ISecretProtector</c> and stored
    /// in <see cref="EncryptedCredentials"/>/<see cref="EncryptedPassword"/>. Nothing on this entity
    /// is ever returned by an API or written to a log.
    /// </para>
    /// </summary>
    public Guid? CredentialReferenceId { get; set; }

    /// <summary>Engine credentials (access key/secret, SFTP password) as encrypted JSON.</summary>
    public string? EncryptedCredentials { get; set; }

    /// <summary>
    /// The repository password, encrypted. Without it the repository cannot be opened at all — an
    /// intentional property of the engines, and the reason losing the master key loses the backups.
    /// </summary>
    public string? EncryptedPassword { get; set; }

    /// <summary>Engine-side identifier, recorded once the repository has been created or connected.</summary>
    public string? EngineRepositoryId { get; set; }

    public DateTimeOffset? LastHealthCheckAt { get; set; }

    /// <summary>
    /// Kept apart from <see cref="LastHealthCheckAt"/> on purpose. "Checked a minute ago" and
    /// "last worked a minute ago" are the same sentence only while things are fine; the gap between
    /// them is exactly what tells an operator how long a repository has been failing.
    /// </summary>
    public DateTimeOffset? LastSuccessfulHealthCheckAt { get; set; }

    /// <summary>Redacted before storage — engine output can contain credentials.</summary>
    public string? LastError { get; set; }

    public long StorageUsageBytes { get; set; }
    public long SnapshotCount { get; set; }

    /// <summary>Disabled repositories keep their snapshots but take no new ones.</summary>
    public bool IsEnabled { get; set; } = true;
}
