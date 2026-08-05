using Harbora.Modules.Backup.Contracts;
using Harbora.Domain.Common;

namespace Harbora.Modules.Backup.Domain;

/// <summary>One backup run and what it produced.</summary>
public class BackupSnapshot : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>Null for a manual or safety snapshot taken outside any policy.</summary>
    public Guid? PolicyId { get; set; }
    public BackupPolicy? Policy { get; set; }

    public Guid RepositoryId { get; set; }
    public BackupRepository? Repository { get; set; }

    public BackupTargetType TargetType { get; set; }
    public string TargetRef { get; set; } = string.Empty;

    /// <summary>
    /// The engine's own handle for this snapshot. Null until the engine reports one — a row exists
    /// from the moment the work is queued so a crash mid-run leaves a visible Failed snapshot rather
    /// than no trace at all.
    /// </summary>
    public string? EngineSnapshotId { get; set; }

    public BackupSnapshotStatus Status { get; set; } = BackupSnapshotStatus.Pending;

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public TimeSpan? Duration =>
        StartedAt is { } started && CompletedAt is { } completed ? completed - started : null;

    /// <summary>Bytes read from the source.</summary>
    public long OriginalSizeBytes { get; set; }

    /// <summary>Bytes this snapshot actually added to the repository, after dedup and compression.</summary>
    public long StoredSizeBytes { get; set; }

    /// <summary>Bytes that were already present and did not need storing again.</summary>
    public long DeduplicatedSizeBytes { get; set; }

    public long FilesCount { get; set; }

    /// <summary>Whether a logical database dump was included alongside the file data.</summary>
    public bool DatabaseDumpIncluded { get; set; }

    public BackupVerificationStatus VerificationStatus { get; set; } = BackupVerificationStatus.NotVerified;
    public DateTimeOffset? VerifiedAt { get; set; }
    public string? VerificationNote { get; set; }

    /// <summary>Redacted before storage.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Warnings from a run that succeeded anyway, newline-separated.</summary>
    public string? Warnings { get; set; }

    public BackupTrigger TriggeredBy { get; set; } = BackupTrigger.Manual;

    /// <summary>User who asked for it, when a person did.</summary>
    public Guid? TriggeredByUserId { get; set; }

    /// <summary>Ties every log line and job for this snapshot together.</summary>
    public string? CorrelationId { get; set; }

    public bool IsTerminal => Status is BackupSnapshotStatus.Completed
        or BackupSnapshotStatus.CompletedWithWarnings
        or BackupSnapshotStatus.Failed
        or BackupSnapshotStatus.Cancelled
        or BackupSnapshotStatus.Deleted;

    /// <summary>Only a snapshot that finished with usable data can be restored or verified.</summary>
    public bool IsRestorable => Status is BackupSnapshotStatus.Completed
        or BackupSnapshotStatus.CompletedWithWarnings;
}
