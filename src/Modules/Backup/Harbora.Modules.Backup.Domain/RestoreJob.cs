using Harbora.Modules.Backup.Contracts;
using Harbora.Domain.Common;

namespace Harbora.Modules.Backup.Domain;

/// <summary>
/// A request to put data back.
///
/// <para>
/// Persisted as its own aggregate rather than run inline, for two reasons. A restore can take longer
/// than any HTTP request should, and — more importantly — it is the most destructive authenticated
/// operation in the product. A row that records who asked, for what, onto what, and how it ended is
/// the difference between an incident that can be reconstructed and one that cannot.
/// </para>
/// </summary>
public class RestoreJob : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    public Guid SnapshotId { get; set; }
    public BackupSnapshot? Snapshot { get; set; }

    public RestoreType RestoreType { get; set; }

    /// <summary>Where it goes: a volume name, a directory, a database name, an app id.</summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>
    /// True when <see cref="Destination"/> is live data being overwritten, rather than a new
    /// location. Requires explicit confirmation, and is what the audit entry is written about.
    /// </summary>
    public bool OverwritesLiveTarget { get; set; }

    public RestoreConflictStrategy ConflictStrategy { get; set; } = RestoreConflictStrategy.Fail;

    /// <summary>Newline-separated entries relative to the snapshot root. Empty restores everything.</summary>
    public string? Entries { get; set; }

    public RestoreJobStatus Status { get; set; } = RestoreJobStatus.Pending;

    public Guid RequestedByUserId { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>0-100. Coarse by design — a precise figure the engine cannot supply is a lie.</summary>
    public int Progress { get; set; }

    public long RestoredFilesCount { get; set; }
    public long RestoredBytes { get; set; }

    /// <summary>Redacted before storage.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Where a pre-restore safety copy was written, when one was taken.</summary>
    public string? SafetySnapshotRef { get; set; }

    public string? CorrelationId { get; set; }

    public bool IsTerminal => Status is RestoreJobStatus.Completed
        or RestoreJobStatus.Failed
        or RestoreJobStatus.Cancelled;
}
