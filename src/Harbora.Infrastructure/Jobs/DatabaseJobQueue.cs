using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Jobs;

/// <summary>
/// <see cref="IJobQueue"/> backed by the <see cref="Job"/> table. Enqueue = insert a row; the worker
/// polls for Pending rows. <see cref="JobSignal"/> lets the worker react immediately in-process
/// without waiting for its next poll, so durability doesn't cost latency.
/// </summary>
public sealed class DatabaseJobQueue(
    HarboraDbContext db,
    ISystemClock clock,
    IJobCancellationRegistry cancellations,
    JobSignal signal) : IJobQueue
{
    public Task<Guid> EnqueueAsync(JobKind kind, Guid targetId, CancellationToken ct = default)
        => AddAsync(kind, targetId, exclusiveWith: null, ct);

    public Task<Guid> EnqueueExclusiveAsync(
        JobKind kind, Guid targetId, Guid exclusiveWith, CancellationToken ct = default)
        => AddAsync(kind, targetId, exclusiveWith, ct);

    private async Task<Guid> AddAsync(JobKind kind, Guid targetId, Guid? exclusiveWith, CancellationToken ct)
    {
        var job = new Job
        {
            Kind = kind,
            TargetId = targetId,
            ExclusiveWith = exclusiveWith,
            Status = JobStatus.Pending,
            CreatedAt = clock.UtcNow
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);

        signal.Notify();
        return job.Id;
    }

    public async Task<bool> RequestCancellationAsync(JobKind kind, Guid targetId, CancellationToken ct = default)
    {
        var live = await db.Jobs
            .Where(j => j.Kind == kind && j.TargetId == targetId &&
                        (j.Status == JobStatus.Pending || j.Status == JobStatus.Running))
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (live is null) return false;

        live.CancelRequested = true;

        if (live.Status == JobStatus.Pending)
        {
            // Never started: settle it here so it can't be claimed later.
            live.Status = JobStatus.Cancelled;
            live.FinishedAt = clock.UtcNow;
            live.ClaimStamp++;
            try
            {
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                // The worker claimed it between our read and our write. The CancelRequested flag is
                // what matters now — reload and fall through to the running path so the work is
                // still interrupted rather than silently continuing.
                db.ChangeTracker.Clear();
                var claimed = await db.Jobs.FirstOrDefaultAsync(j => j.Id == live.Id, ct);
                if (claimed is null || claimed.IsTerminal) return false;
                claimed.CancelRequested = true;
                await db.SaveChangesAsync(ct);
                cancellations.TryCancel(claimed.Id);
                return true;
            }
        }

        await db.SaveChangesAsync(ct);

        // Running: interrupt it if this process owns it. If it doesn't (another instance), the flag
        // is still persisted — the owner observes it on its next checkpoint.
        cancellations.TryCancel(live.Id);
        return true;
    }
}

/// <summary>
/// Wakes the job worker the moment something is enqueued in this process, instead of leaving it to
/// the next poll. Purely a latency optimisation — correctness rests on polling the table.
/// </summary>
public class JobSignal
{
    private readonly SemaphoreSlim _semaphore = new(0);

    public void Notify()
    {
        // A single pending permit is enough: the worker drains every ready job once woken.
        if (_semaphore.CurrentCount == 0) _semaphore.Release();
    }

    /// <summary>
    /// Waits for a nudge, the timeout, or cancellation — and lets the cancellation out.
    ///
    /// <para>
    /// This used to swallow the OperationCanceledException, which quietly made it the only reason
    /// the worker's shutdown drain ever ran: the loop's wait on this signal is where a worker with
    /// one long job in flight and an otherwise empty queue spends nearly all of its life, and the
    /// caller was relying on never being thrown at from here. Tidying the swallow away would have
    /// been an obviously safe two-line change that abandoned every in-flight job on shutdown. A
    /// method handed a token honours it; the worker states what it does about that itself.
    /// </para>
    /// </summary>
    public virtual Task WaitAsync(TimeSpan timeout, CancellationToken ct) =>
        _semaphore.WaitAsync(timeout, ct);
}
