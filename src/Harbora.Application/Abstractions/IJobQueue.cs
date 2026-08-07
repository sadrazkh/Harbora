using Harbora.Domain.Jobs;

namespace Harbora.Application.Abstractions;

/// <summary>
/// Durable background work queue: a job row is persisted before the work is attempted, so a
/// restart resumes from the database rather than losing whatever was in memory (completes P3).
/// </summary>
public interface IJobQueue
{
    /// <summary>
    /// Persist a job and wake the worker. Returns the job id. The job excludes on its own target: no
    /// other job of this kind for this target runs beside it.
    /// </summary>
    Task<Guid> EnqueueAsync(JobKind kind, Guid targetId, CancellationToken ct = default);

    /// <summary>
    /// The same, for work whose target is not the thing that must not double up.
    ///
    /// <para>
    /// A deployment is the case this exists for: its target is the immutable <c>Deployment</c> row
    /// and every redeploy makes a new one, so two deployments of one app are two different targets
    /// and nothing in the queue would keep them apart. Passing the app id as
    /// <paramref name="exclusiveWith"/> is what makes a deploy and the redeploy behind it serial
    /// again — a promise the platform relied on when the worker ran one job at a time, and which
    /// must now be said out loud.
    /// </para>
    /// </summary>
    /// <param name="targetId">The aggregate the work acts on — still what the handler is given.</param>
    /// <param name="exclusiveWith">
    /// What no two concurrently running jobs of this kind may share.
    /// </param>
    Task<Guid> EnqueueExclusiveAsync(
        JobKind kind, Guid targetId, Guid exclusiveWith, CancellationToken ct = default);

    /// <summary>
    /// Ask the live job for a target to stop: a Pending job is cancelled before it ever starts, and
    /// a Running job is signalled through its cancellation token. Returns false when there was
    /// nothing live to cancel.
    /// </summary>
    Task<bool> RequestCancellationAsync(JobKind kind, Guid targetId, CancellationToken ct = default);
}

/// <summary>
/// Executes one kind of persisted job.
///
/// <para>
/// The seam that lets a module own its own background work. <c>JobDispatcher</c> maps a stored
/// (kind, target) pair back to a call, and it lives in Infrastructure — which cannot see the module
/// projects that reference it. Without this, every new job kind would have to be wired into a switch
/// in the core, and the core would need a reference to every module that has one.
/// </para>
/// <para>
/// Handlers are resolved per job, inside the worker's own scope, and must be idempotent: a job whose
/// process crashed mid-run is claimed again and re-executed.
/// </para>
/// </summary>
public interface IJobHandler
{
    Domain.Jobs.JobKind Kind { get; }

    Task ExecuteAsync(Guid targetId, CancellationToken ct);
}

/// <summary>
/// Remembers what an <c>Idempotency-Key</c> already produced, so a retried request returns the
/// original result instead of starting the work a second time.
///
/// <para>
/// Platform-level rather than per-module: more than one module's API needs it, and the alternative
/// was one module depending on another to reuse a table.
/// </para>
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>The id the original call produced, or null if this key is new or has expired.</summary>
    Task<Guid?> FindAsync(string endpoint, string key, CancellationToken ct);

    /// <summary>Record what this key produced. Losing a race with an identical request is not an error.</summary>
    Task RememberAsync(Guid workspaceId, string endpoint, string key, Guid resultId, CancellationToken ct);
}

/// <summary>
/// Tracks the cancellation token sources of jobs executing in THIS process, so a cancel request can
/// actually interrupt work already underway instead of only marking a row.
/// </summary>
public interface IJobCancellationRegistry
{
    /// <summary>Registers a running job; dispose the returned scope when it finishes.</summary>
    IDisposable Register(Guid jobId, CancellationTokenSource cts);

    /// <summary>Signals the job if it is running here. False when it isn't (e.g. another instance).</summary>
    bool TryCancel(Guid jobId);
}
