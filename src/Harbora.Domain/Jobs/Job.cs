using Harbora.Domain.Common;

namespace Harbora.Domain.Jobs;

/// <summary>What kind of work a <see cref="Job"/> represents. Persisted by value — never renumber.</summary>
public enum JobKind
{
    Deployment = 0,
    Backup = 1,
    ServiceProvision = 2,
    /// <summary>A scheduled job run on demand — durable, so "run now" survives a restart.</summary>
    CronRun = 3,

    // Backup module (docs/backup-sync/ARCHITECTURE.md § 6). Appended, never renumbered: existing
    // rows hold these by value, and reordering would turn a queued deployment into a prune.
    BackupSnapshot = 4,
    BackupRestore = 5,
    BackupVerify = 6,
    BackupPrune = 7,
    RepositoryHealthCheck = 8
}

public enum JobStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4
}

/// <summary>
/// A unit of background work, persisted before it is executed (ADR-005 / completes P3).
///
/// The previous queue held <c>Func&lt;IServiceProvider, CancellationToken, Task&gt;</c> delegates in an
/// in-memory channel — a delegate cannot survive a restart, so a queued deployment was only
/// recovered because the reconciler re-queued it into another equally volatile channel. Persisting
/// a *description* of the work (kind + target) instead means the row IS the queue: a restart picks
/// up exactly where it left off, and work in progress can be asked to stop.
/// </summary>
public class Job : BaseEntity
{
    public JobKind Kind { get; set; }

    /// <summary>The aggregate this job acts on: deployment id, backup id or managed-service id.</summary>
    public Guid TargetId { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Pending;

    /// <summary>How many times execution has been started (a claim that later crashed still counts).</summary>
    public int Attempts { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// Set when someone asks for this job to stop. A Pending job is cancelled before it ever runs;
    /// a Running job is signalled through its cancellation token.
    /// </summary>
    public bool CancelRequested { get; set; }

    /// <summary>Worker instance that claimed the job — diagnostics, and identifies orphans after a crash.</summary>
    public string? ClaimedBy { get; set; }

    /// <summary>
    /// Optimistic-concurrency guard on the claim. Two workers racing to take the same Pending job
    /// means one of them loses the update and moves on, so a job is never executed twice.
    /// </summary>
    public int ClaimStamp { get; set; }

    /// <summary>Terminal jobs are never re-run or reconciled.</summary>
    public bool IsTerminal => Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled;
}
