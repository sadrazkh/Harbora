using Harbora.Domain.Common;

namespace Harbora.Domain.Backups;

/// <summary>A backup destination (local dir or S3-compatible bucket). Secrets encrypted.</summary>
public class BackupDestination : BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public BackupDestinationType Type { get; set; } = BackupDestinationType.Local;

    public string? LocalPath { get; set; }

    // S3-compatible settings
    public string? Endpoint { get; set; }
    public string? Bucket { get; set; }
    public string? Region { get; set; }
    public string? AccessKey { get; set; }
    public string? EncryptedSecretKey { get; set; }

    // SFTP settings. The host key is not optional — without it Harbora cannot tell the real server
    // from anything else answering on that address, and would hand it the backup and the password.
    public string? SftpHost { get; set; }
    public int SftpPort { get; set; } = 22;
    public string? SftpUsername { get; set; }
    public string? EncryptedSftpPassword { get; set; }
    public string? SftpDirectory { get; set; }
    public string? SftpHostKey { get; set; }

    public bool IsDefault { get; set; }
}

/// <summary>A recurring backup rule evaluated by the backup scheduler.</summary>
public class BackupSchedule : BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public Guid DestinationId { get; set; }

    public BackupType Type { get; set; }
    public string TargetRef { get; set; } = string.Empty;

    /// <summary>
    /// D2 (2026-08-25 shared-databases plan): which logical database inside the instance named by
    /// <see cref="TargetRef"/> this schedule backs up. Null keeps meaning exactly what it always
    /// has — the instance's own admin/default database — so every schedule created before this
    /// column existed keeps running unchanged. A loose reference, like <see cref="TargetRef"/>
    /// itself: no foreign key, because a schedule for a database that has since been deleted should
    /// still be visible (and pausable) rather than disappear with it.
    /// </summary>
    public Guid? ManagedServiceDatabaseId { get; set; }

    public int IntervalHours { get; set; } = 24;
    /// <summary>Keep at most this many successful backups per target; older ones are pruned.</summary>
    public int RetentionCount { get; set; } = 7;

    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public bool IsEnabled { get; set; } = true;
}

/// <summary>A backup run + its retention metadata.</summary>
public class Backup : BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public Guid DestinationId { get; set; }
    public BackupDestination? Destination { get; set; }

    public BackupType Type { get; set; }
    public BackupStatus Status { get; set; } = BackupStatus.Pending;

    /// <summary>Loose reference to the backed-up resource (app id, service id, "platform").</summary>
    public string TargetRef { get; set; } = string.Empty;

    /// <summary>
    /// D2 (2026-08-25 shared-databases plan): which logical database inside the instance named by
    /// <see cref="TargetRef"/> this is a backup OF. Null means exactly what every backup taken before
    /// this column existed already meant — the instance's own admin/default database — which is what
    /// makes this addition backward compatible without a data migration: nothing sets it, so nothing
    /// changes for a single-database instance or a whole-instance restore.
    ///
    /// <para>
    /// A loose reference, like <see cref="TargetRef"/> itself — no foreign key, so a backup survives
    /// the logical database it was taken from being deleted, the same way a backup already survives
    /// its <see cref="Harbora.Domain.Services.ManagedService"/> being deleted (<see cref="TargetRef"/>
    /// carries no FK either).
    /// </para>
    /// </summary>
    public Guid? ManagedServiceDatabaseId { get; set; }

    public string? ArtifactPath { get; set; }
    public long SizeBytes { get; set; }
    public string? Checksum { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? ErrorMessage { get; set; }

    public bool IsScheduled { get; set; }

    /// <summary>
    /// When this backup was last checked, and what the check concluded.
    ///
    /// Recorded because the interesting question is not "did the backup run" — that is already
    /// visible — but "has anyone confirmed it would restore, and how long ago". A backup taken
    /// nightly for a year and never verified is a year of assumption.
    /// </summary>
    public DateTimeOffset? VerifiedAt { get; set; }
    public bool? VerifiedRestorable { get; set; }
    public string? VerificationNote { get; set; }
}
