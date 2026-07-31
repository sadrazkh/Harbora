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
