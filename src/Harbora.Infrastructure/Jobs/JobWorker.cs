using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Jobs;

/// <summary>
/// Drains the durable job table: claim the oldest Pending job, run it in its own DI scope, record
/// the outcome. Waits on <see cref="JobSignal"/> so an enqueue in this process is picked up
/// immediately, with a poll interval as the backstop that also catches rows written by the
/// reconciler or another instance.
/// </summary>
public class JobWorker(
    IServiceScopeFactory scopeFactory,
    IJobCancellationRegistry cancellations,
    JobSignal signal,
    ISystemClock clock,
    ILogger<JobWorker> logger) : BackgroundService
{
    /// <summary>
    /// Seam over <see cref="JobDispatcher"/> so the queue's own behaviour — claiming, cancellation,
    /// settling — can be tested without standing up the deployment and backup engines behind it.
    /// </summary>
    protected virtual Task DispatchAsync(Job job, IServiceProvider scope, CancellationToken ct)
        => JobDispatcher.ExecuteAsync(job, scope, ct);

    /// <summary>Backstop poll — the signal handles the common case.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Harbora job worker {Worker} started.", _workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            bool ranSomething;
            try
            {
                ranSomething = await RunNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failure to read/claim must not kill the worker — the platform would silently
                // stop deploying.
                logger.LogError(ex, "Job worker loop failed; retrying after the poll interval.");
                ranSomething = false;
            }

            // Only idle when there was nothing to do, so a backlog drains without pausing.
            if (!ranSomething) await signal.WaitAsync(PollInterval, stoppingToken);
        }
    }

    /// <summary>
    /// Claims and executes at most one job. Returns false when the queue was empty. Public so tests
    /// can drive the worker deterministically instead of racing a background loop.
    /// </summary>
    public async Task<bool> RunNextAsync(CancellationToken stoppingToken)
    {
        var claim = await ClaimNextAsync(stoppingToken);
        if (claim is not { } job) return false;

        using var scope = scopeFactory.CreateScope();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var _ = cancellations.Register(job.Id, linked);

        string? error = null;
        JobStatus outcome;
        try
        {
            await DispatchAsync(job, scope.ServiceProvider, linked.Token);
            outcome = JobStatus.Succeeded;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // Cancelled on request, or the host is shutting down. Either way this job did not fail
            // on its own merits, and must not be recorded as if it had.
            outcome = stoppingToken.IsCancellationRequested ? JobStatus.Pending : JobStatus.Cancelled;
            error = stoppingToken.IsCancellationRequested ? null : "Cancelled by request.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId} ({Kind}) failed.", job.Id, job.Kind);
            outcome = JobStatus.Failed;
            error = ex.Message;
        }

        await SettleAsync(job.Id, outcome, error, CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Takes the oldest Pending job. The ClaimStamp concurrency token turns a race between two
    /// workers into a lost update for one of them, so a job is never executed twice.
    /// </summary>
    private async Task<Job?> ClaimNextAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

        var candidate = await db.Jobs
            .Where(j => j.Status == JobStatus.Pending)
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (candidate is null) return null;

        // Cancel was requested but the job is Pending again — either it was never claimed, or a
        // shutdown released the claim after the request. Settle it without running the work.
        if (candidate.CancelRequested)
        {
            candidate.Status = JobStatus.Cancelled;
            candidate.FinishedAt = clock.UtcNow;
            candidate.ClaimStamp++;
            try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { }
            return null;
        }

        candidate.Status = JobStatus.Running;
        candidate.StartedAt = clock.UtcNow;
        candidate.ClaimedBy = _workerId;
        candidate.Attempts++;
        candidate.ClaimStamp++;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another worker claimed it first; let the loop pick up the next one.
            return null;
        }

        return candidate;
    }

    private async Task SettleAsync(Guid jobId, JobStatus outcome, string? error, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
            if (job is null) return;

            job.Status = outcome;
            job.Error = error;
            // Returning to Pending means "resume after restart", so it must not look finished.
            job.FinishedAt = outcome == JobStatus.Pending ? null : clock.UtcNow;
            if (outcome == JobStatus.Pending) job.ClaimedBy = null;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // The work itself already happened; losing the bookkeeping is bad but not fatal — the
            // reconciler will settle the row on next startup.
            logger.LogError(ex, "Could not record the outcome of job {JobId}.", jobId);
        }
    }
}
