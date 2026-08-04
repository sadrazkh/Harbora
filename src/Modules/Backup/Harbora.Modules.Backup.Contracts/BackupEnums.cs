namespace Harbora.Modules.Backup.Contracts;

/// <summary>
/// Which engine owns a repository's on-disk format.
///
/// <para>
/// Stored per repository rather than configured globally. A repository's bytes are written in its
/// engine's format — a Kopia repository is only readable by Kopia — so a global setting would
/// silently strand every existing artifact the first time someone changed it.
/// </para>
/// </summary>
public enum BackupEngineKind
{
    /// <summary>Harbora's own tar + AES-GCM pipeline. The default; what every existing backup uses.</summary>
    Native = 0,

    /// <summary>Kopia: content-addressed, deduplicated, with real snapshot history.</summary>
    Kopia = 1
}

/// <summary>Where a repository's data physically lives. Persisted by value — never renumber.</summary>
public enum BackupRepositoryType
{
    Local = 0,
    S3Compatible = 1,
    AmazonS3 = 2,
    MinIO = 3,
    BackblazeB2 = 4,
    Sftp = 5,
    WebDav = 6,

    /// <summary>Another Harbora node acting as storage.</summary>
    HarboraNode = 7,

    Custom = 8
}

public enum BackupRepositoryStatus
{
    /// <summary>The row exists but the engine has not yet created or connected to the repository.</summary>
    Pending = 0,

    Ready = 1,

    /// <summary>Reachable, but the last health check found something wrong.</summary>
    Degraded = 2,

    /// <summary>The last health check could not reach it at all.</summary>
    Unavailable = 3,

    /// <summary>Disconnected by an operator. Not used for new snapshots; existing ones are kept.</summary>
    Disabled = 4
}

/// <summary>
/// What a policy or snapshot points at.
/// </summary>
public enum BackupTargetType
{
    Application = 0,
    DockerVolume = 1,
    Directory = 2,
    Database = 3,
    Server = 4,
    Device = 5,
    Configuration = 6,
    EnvironmentVariables = 7
}

/// <summary>
/// Snapshot lifecycle. Transitions are enforced by <c>SnapshotLifecycle</c> rather than by callers
/// assigning freely, so a snapshot cannot go from Failed back to Running and lose the reason.
/// </summary>
public enum BackupSnapshotStatus
{
    Pending = 0,
    Preparing = 1,
    Running = 2,
    Verifying = 3,
    Completed = 4,

    /// <summary>Finished with usable data, but something the operator should read about.</summary>
    CompletedWithWarnings = 5,

    Failed = 6,
    Cancelled = 7,
    Deleting = 8,
    Deleted = 9
}

/// <summary>
/// Whether anyone has confirmed a snapshot would actually restore.
///
/// <para>
/// <see cref="NotVerified"/> and <see cref="Passed"/> must never render the same way. A year of
/// nightly snapshots nobody ever checked is a year of assumption, and a UI that shows silence as
/// success is how that goes unnoticed.
/// </para>
/// </summary>
public enum BackupVerificationStatus
{
    NotVerified = 0,
    Passed = 1,
    Failed = 2,

    /// <summary>Verification does not apply to this artifact (e.g. nothing to rehearse).</summary>
    Skipped = 3
}

public enum RestoreType
{
    File = 0,
    Folder = 1,
    Volume = 2,
    Database = 3,
    Application = 4,
    FullServer = 5
}

/// <summary>What to do when a restored entry already exists at the destination.</summary>
public enum RestoreConflictStrategy
{
    /// <summary>Stop and change nothing. The default: the safe choice must be the unchosen one.</summary>
    Fail = 0,

    Overwrite = 1,
    Skip = 2,
    Rename = 3,

    /// <summary>Restore into a fresh directory, leaving the original untouched.</summary>
    RestoreToNewLocation = 4
}

public enum RestoreJobStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4
}

/// <summary>Who or what caused a snapshot to be taken.</summary>
public enum BackupTrigger
{
    Manual = 0,
    Schedule = 1,

    /// <summary>Taken automatically before a risky operation, e.g. a restore or an upgrade.</summary>
    Safety = 2,

    Api = 3
}

/// <summary>Database engines with a native dump/restore path.</summary>
public enum DatabaseEngine
{
    PostgreSql = 0,
    MySql = 1,
    MariaDb = 2,
    MongoDb = 3,
    Redis = 4
}
