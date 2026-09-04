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
    RepositoryHealthCheck = 8,

    // Appended: persisted enum values are never renumbered. The target is a BillingRun row.
    BillingHour = 9,

    /// <summary>
    /// One call of one function, on a schedule or because something happened. The target is the
    /// <c>FunctionInvocation</c> row, which already holds what to send — so the queue keeps carrying
    /// nothing but a kind and an id, and a scheduled call still fires after a restart that landed
    /// between the tick and the request.
    /// </summary>
    FunctionInvoke = 10,

    /// <summary>
    /// One attempt at one <c>NotificationDelivery</c> row (N1, 2026-08-16 notification-system spec).
    /// The target is the delivery row, which already holds the channel/recipient, subject and
    /// encrypted body — the same reason <see cref="FunctionInvoke"/>'s target is a row rather than a
    /// payload. Appended, never renumbered.
    /// </summary>
    NotificationDelivery = 11,

    /// <summary>
    /// One attempt at one <c>Harbora.Domain.Notifications.EventDelivery</c> row (P6, 2026-08-20
    /// platform-options plan, "Outbound event notifications"). Same shape as
    /// <see cref="NotificationDelivery"/> and for the same reason: the target is the delivery row,
    /// which already holds the subscription id, the event and the rendered payload, so the queue
    /// keeps carrying nothing but a kind and an id. A distinct kind rather than reusing
    /// <see cref="NotificationDelivery"/> because the two are dispatched by different code
    /// (<c>EventDispatcher</c>, not <c>NotificationService</c>) against a different retry budget.
    /// Appended, never renumbered.
    /// </summary>
    EventDelivery = 12,

    /// <summary>
    /// One VACUUM/ANALYZE/REINDEX/OPTIMIZE run against one logical database (2.3, round-2 market-gaps
    /// plan). The target is a <c>Harbora.Domain.Services.DatabaseMaintenanceRun</c> row, the same "row
    /// IS the queue" shape <see cref="BackupSnapshot"/> already uses — and, like a deployment, it
    /// excludes on something other than its own target: two maintenance runs of one logical database
    /// must not run beside each other, but every run is a fresh row, so
    /// <c>IJobQueue.EnqueueExclusiveAsync</c> is called with the database's own id. Appended, never
    /// renumbered.
    /// </summary>
    DatabaseMaintenance = 13
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

    /// <summary>
    /// Denormalised from whatever the job's own target belongs to, the same way
    /// <c>Deployment.WorkspaceId</c> and <c>NotificationDelivery.WorkspaceId</c> are: stamped at
    /// enqueue time by the caller, who already knows it, rather than resolved later by joining
    /// <see cref="TargetId"/> against nine different aggregate tables.
    ///
    /// <para>
    /// <c>Job</c> stays a platform-wide, unfiltered table (<c>HarboraDbContext.ApplyWorkspaceFilters</c>)
    /// — this column does not carry a global query filter, and does not turn it into one. A caller
    /// like <c>/activity</c> that wants only its own workspace's rows filters explicitly, the same
    /// way <c>NotificationsController</c> filters <c>UserNotification</c> by hand instead of relying
    /// on EF to do it: a filter of <c>WorkspaceId == null || WorkspaceId == CurrentWorkspaceId</c>
    /// would leak every platform-level job (a null <see cref="WorkspaceId"/>, same as
    /// <c>NotificationDelivery</c>'s transactional rows) into every tenant at once, which is worse
    /// than the absent filter this table has always had.
    /// </para>
    ///
    /// <para>
    /// Null for work that belongs to nobody in particular — a <c>BillingHour</c> tick processes every
    /// workspace at once — and for the two password/verification emails queued before a workspace is
    /// even known. Everything else is stamped by the caller that enqueued it.
    /// </para>
    /// </summary>
    public Guid? WorkspaceId { get; set; }

    /// <summary>
    /// What this job must not share with another job of the same kind running at the same time.
    /// Null — the ordinary case — means its own <see cref="TargetId"/>.
    ///
    /// <para>
    /// It exists because for one kind the target is not the thing that must not double up. A
    /// deployment's target is the <c>Deployment</c> row, and every redeploy is a new row, so two
    /// deployments of one app are two different targets: without this they would be free to run
    /// beside each other, and one app would get two <c>docker build</c>s, two containers under one
    /// name, two host-port reservations and two proxy applies. What must not double up there is the
    /// <b>app</b>, which the caller queuing the deployment already knows.
    /// </para>
    ///
    /// <para>
    /// Stamped at enqueue rather than worked out at claim time on purpose: the worker's claim runs as
    /// SQL, and a key it had to join to <c>Deployments</c> for could not stay a term in that query.
    /// </para>
    /// </summary>
    public Guid? ExclusiveWith { get; set; }

    /// <summary>
    /// The value the queue actually excludes on, with the fallback applied. Every job has one; most
    /// jobs' is their own target. Not stored — see <see cref="ExclusiveWith"/>.
    /// </summary>
    public Guid ExcludesOn => ExclusiveWith ?? TargetId;

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

    /// <summary>
    /// Earliest moment this job may be claimed again; null means "as soon as a worker is free".
    /// Set when a transient failure sends the job back to Pending with a backoff, so the worker
    /// waits instead of claiming the same doomed work in a tight loop.
    /// </summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

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
