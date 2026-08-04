using Harbora.Domain.Jobs;

namespace Harbora.Application.Abstractions;

/// <summary>
/// Durable background work queue: a job row is persisted before the work is attempted, so a
/// restart resumes from the database rather than losing whatever was in memory (completes P3).
/// </summary>
public interface IJobQueue
{
    /// <summary>Persist a job and wake the worker. Returns the job id.</summary>
    Task<Guid> EnqueueAsync(JobKind kind, Guid targetId, CancellationToken ct = default);

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
