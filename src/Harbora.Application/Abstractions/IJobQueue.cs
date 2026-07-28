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
