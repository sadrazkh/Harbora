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

    public bool IsDefault { get; set; }
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
}
