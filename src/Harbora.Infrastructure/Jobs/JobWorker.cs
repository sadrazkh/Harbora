using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Jobs;

/// <summary>
/// Drains the durable job table: claim the oldest Pending job that is due, run it in its own DI
/// scope under the deadline <see cref="JobExecutionPolicy"/> gives its kind, record the outcome.
/// Waits on <see cref="JobSignal"/> so an enqueue in this process is picked up immediately, with a
/// poll interval as the backstop that also catches rows written by the reconciler or another
/// instance — and a job serving a retry backoff, for which nothing signals at all.
///
/// <para>
/// Up to <see cref="JobQueueOptions.MaxConcurrency"/> of those run at once, and never two for one
/// target. The loop claims and hands off; it does not wait for the work. What it waits for is a free
/// slot, which is the only thing that bounds the platform now — before this, the bound was one, and
/// a single tenant's build was the whole install's background capacity for as long as it took.
/// </para>
/// </summary>
public class JobWorker(
    IServiceScopeFactory scopeFactory,
    IJobCancellationRegistry cancellations,
    JobSignal signal,
    JobStartupGate startupGate,
    ISystemClock clock,
    IOptions<JobQueueOptions> options,
    ILogger<JobWorker> logger) : BackgroundService
{
    /// <summary>
    /// Seam over <see cref="JobDispatcher"/> so the queue's own behaviour — claiming, cancellation,
    /// settling — can be tested without standing up the deployment and backup engines behind it.
    /// </summary>
    protected virtual Task DispatchAsync(Job job, IServiceProvider scope, CancellationToken ct)
        => JobDispatcher.ExecuteAsync(job, scope, ct);

    /// <summary>
    /// How long this job may run. A seam for the same reason as <see cref="DispatchAsync"/>: the
    /// real deadlines are quarter-hours and upwards, and a test has to be able to reach one.
    /// </summary>
    protected virtual TimeSpan TimeoutFor(Job job) => JobExecutionPolicy.TimeoutFor(job.Kind);

    /// <summary>Backstop poll — the signal handles the common case.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    /// <summary>How many jobs this process may have in flight at once. Read once, at start.</summary>
    private readonly int _maxConcurrency = options.Value.EffectiveMaxConcurrency;

    /// <summary>
    /// The targets this worker is running right now. Instance state rather than a shared service
    /// because exactly one worker is registered per process, and "in flight in this process" is
    /// precisely what it means: a second instance is kept off the same row by the ClaimStamp
    /// concurrency token in the claim, not by this.
    /// </summary>
    private readonly InFlightTargets _inFlight = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Harbora job worker {Worker} started, running up to {Limit} job(s) at once.",
            _workerId, _maxConcurrency);

        // Nothing is claimed until the startup reconcilers have finished. This is a BackgroundService
        // and they are not: their StartAsync runs to completion, while the host does not wait for
        // this method at all — so without the gate the loop below would be claiming work at the same
        // moment DeploymentReconciler was deciding that work is over. The token is what keeps a host
        // that never finishes starting from waiting on a worker that is waiting on it.
        try { await startupGate.WaitAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Harbora job worker {Worker} stopped before startup finished.", _workerId);
            return;
        }

        // Not disposed until every job below has finished with it — each one releases its slot on
        // the way out, and the drain at the end of this method is what guarantees they all have.
        using var slots = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
        var running = new List<Task>();

        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait for capacity before looking at the queue, not after: claiming a row this process
            // has no room to run would mark it Running and then sit on it.
            try { await slots.WaitAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }

            Job? job;
            try
            {
                job = await ClaimNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                slots.Release();
                break;
            }
            catch (Exception ex)
            {
                // A failure to read/claim must not kill the worker — the platform would silently
                // stop deploying.
                logger.LogError(ex, "Job worker loop failed; retrying after the poll interval.");
                job = null;
            }

            if (job is null)
            {
                // Nothing claimable: the queue is empty, or everything left is backing off or
                // belongs to a target already in flight here. Give the slot back and idle until
                // something is enqueued, something finishes, or the poll comes round.
                slots.Release();
                await signal.WaitAsync(PollInterval, stoppingToken);
                continue;
            }

            // Started rather than awaited — this is the change the whole phase is named for. Only
            // the completed entries are pruned, so the list stays short over a long backlog while
            // still holding everything that is genuinely in flight.
            running.RemoveAll(t => t.IsCompleted);
            running.Add(StartInBackground(job, slots, stoppingToken));
        }

        // The host is stopping. Every claimed job is being cancelled by the same token and is on its
        // way to being settled Pending; leaving now would abandon those writes half-made and leave
        // rows reading Running with nothing running them until the next restart reconciled them.
        var inFlight = running.Where(t => !t.IsCompleted).ToArray();
        if (inFlight.Length > 0)
            logger.LogInformation(
                "Harbora job worker {Worker} is waiting for {Count} job(s) to be recorded before it stops.",
                _workerId, inFlight.Length);

        // Nothing above lets a job task fault — each one catches its own — but a background service
        // that throws on the way out stops the host with the wrong reason on the last line of the
        // log, which is a bad thing to be wrong about during a restart.
        try { await Task.WhenAll(inFlight); }
        catch (Exception ex) { logger.LogError(ex, "A job did not finish cleanly while the worker was stopping."); }
    }

    /// <summary>
    /// Runs one claimed job away from the loop, and gives back what it was holding afterwards. The
    /// order matters: the target is released by <see cref="ExecuteClaimedAsync"/> before the slot is,
    /// so the loop woken by the slot already sees the target as free.
    /// </summary>
    private Task StartInBackground(Job job, SemaphoreSlim slots, CancellationToken stoppingToken) =>
        // Task.Run rather than a bare call: a dispatch target that blocks before its first await
        // would otherwise hold the claim loop itself, and the loop is what every other tenant's work
        // is waiting on. Deliberately not given the stopping token — a job claimed a moment before
        // shutdown must still be executed far enough to be settled, and a Task.Run that refuses to
        // start would leave the row Running for ever.
        Task.Run(async () =>
        {
            try
            {
                await ExecuteClaimedAsync(job, stoppingToken);
            }
            catch (Exception ex)
            {
                // ExecuteClaimedAsync records its own failures; this is the last resort for one
                // raised around them, and its only job is to keep the worker alive.
                logger.LogError(ex, "Job {JobId} ({Kind}) ended outside its own error handling.",
                    job.Id, job.Kind);
            }
            finally
            {
                slots.Release();
                // A row this process was holding back may have just become claimable, and nothing
                // else will signal that: no enqueue happened. Without this the loop would sit out
                // the poll interval with a free slot and eligible work in the table.
                signal.Notify();
            }
        });

    /// <summary>
    /// Claims and executes at most one job, in full. Returns false when nothing was claimable.
    /// Public so tests can drive the worker deterministically instead of racing a background loop —
    /// the concurrency limit belongs to the loop, so this always runs the job it claims.
    /// </summary>
    public async Task<bool> RunNextAsync(CancellationToken stoppingToken)
    {
        var claim = await ClaimNextAsync(stoppingToken);
        if (claim is not { } job) return false;

        await ExecuteClaimedAsync(job, stoppingToken);
        return true;
    }

    /// <summary>Executes a job this worker has already claimed, and settles the row.</summary>
    private async Task ExecuteClaimedAsync(Job job, CancellationToken stoppingToken)
    {
        try
        {
            await RunAndSettleAsync(job, stoppingToken);
        }
        finally
        {
            // Whatever happened, this target is this process's again. A reservation left behind
            // would read as a target that quietly stopped being served until the panel restarted.
            _inFlight.Release(job.Kind, job.TargetId);
        }
    }

    private async Task RunAndSettleAsync(Job job, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var _ = cancellations.Register(job.Id, linked);

        // The job's own deadline, inside the two above. Nothing bounded a dispatched job before, so
        // a build hanging against a live daemon ran until the process was killed. It no longer takes
        // the rest of the queue with it, but it does still hold one of the slots the queue has.
        var deadline = TimeoutFor(job);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        limit.CancelAfter(deadline);

        string? error = null;
        DateTimeOffset? nextAttemptAt = null;
        JobStatus outcome;
        try
        {
            await DispatchAsync(job, scope.ServiceProvider, limit.Token);

            // A clean return is not proof the work finished. Every dispatch target catches
            // Exception at its top level and writes the failure into its own domain row —
            // DeploymentPipeline into the deployment, CronJobRunner into the run,
            // ManagedServiceEngine into the service, the backup module into its snapshot — so a job
            // this worker just killed can come back looking exactly like a finished one. Whether
            // the deadline fired is the worker's own fact, and it decides.
            (outcome, error) = Stopped(stoppingToken, linked, limit)
                ? OutcomeOfStopping(job, stoppingToken, linked, deadline)
                : (JobStatus.Succeeded, null);
        }
        catch (Exception ex) when (Stopped(stoppingToken, linked, limit))
        {
            // The same judgement, for a run that ended by throwing. Deliberately not restricted to
            // OperationCanceledException: a cancelled token usually reaches us wearing another
            // exception's clothes — a socket torn down mid-transfer surfaces as IOException, a cut
            // stream as TimeoutException — and both of those are types the policy calls retryable.
            // Judged on the exception alone, a snapshot killed at its seven-hour deadline would be
            // queued to spend another seven hours.
            logger.LogDebug(ex, "Job {JobId} ({Kind}) ended while it was being stopped.", job.Id, job.Kind);
            (outcome, error) = OutcomeOfStopping(job, stoppingToken, linked, deadline);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId} ({Kind}) failed.", job.Id, job.Kind);
            error = ex.Message;

            // A transient transport fault on work that can safely be repeated goes back into the
            // queue behind a growing wait. Everything else is final, as it has always been.
            if (job.Attempts < JobExecutionPolicy.MaxAttemptsFor(job.Kind) &&
                JobExecutionPolicy.IsRetryable(ex))
            {
                outcome = JobStatus.Pending;
                nextAttemptAt = clock.UtcNow + JobExecutionPolicy.BackoffFor(job.Attempts);
                logger.LogInformation(
                    "Job {JobId} ({Kind}) will be attempted again after {NextAttemptAt:u}.",
                    job.Id, job.Kind, nextAttemptAt);
            }
            else outcome = JobStatus.Failed;
        }

        // Not given the stopping token: this is the write that records what happened, and a host
        // that is on its way out is exactly when it matters most that it is made.
        await SettleAsync(job.Id, outcome, error, nextAttemptAt, CancellationToken.None);
    }

    /// <summary>Whether anything asked this run to stop, by any of the three routes.</summary>
    private static bool Stopped(
        CancellationToken stoppingToken, CancellationTokenSource linked, CancellationTokenSource limit) =>
        stoppingToken.IsCancellationRequested ||
        linked.IsCancellationRequested ||
        limit.IsCancellationRequested;

    /// <summary>
    /// What a stopped run means for the row. Three causes, and the precedence between them matters:
    /// all three tokens are cancelled during a shutdown, so "the host is stopping" has to be asked
    /// first, or a graceful restart would start recording Failed for work it fully intends to resume.
    /// </summary>
    private (JobStatus Outcome, string? Error) OutcomeOfStopping(
        Job job, CancellationToken stoppingToken, CancellationTokenSource linked, TimeSpan deadline)
    {
        // The host is shutting down. This job did not fail on its own merits and must not be
        // recorded as if it had — it is owed, and the next start resumes it.
        if (stoppingToken.IsCancellationRequested) return (JobStatus.Pending, null);

        // Someone asked for it to stop.
        if (linked.IsCancellationRequested) return (JobStatus.Cancelled, "Cancelled by request.");

        // Its own deadline. Failed rather than Pending: the work really did not finish, and an
        // operator only learns that a job hangs if the row says so.
        logger.LogError("Job {JobId} ({Kind}) was still running after {Limit} and was given up on.",
            job.Id, job.Kind, deadline);
        return (JobStatus.Failed,
            $"Still running after {Describe(deadline)} and was given up on, " +
            "so the rest of the queue could carry on.");
    }

    /// <summary>The deadline in the units an operator thinks in, for the message on the row.</summary>
    private static string Describe(TimeSpan limit) => limit switch
    {
        // Hours only once there are enough of them to read as a round number. The cron deadline is
        // 75 minutes, and "1.3 hour(s)" is a worse sentence than "75 minute(s)" by every measure.
        { TotalHours: >= 2 } => $"{limit.TotalHours:0.#} hour(s)",
        { TotalMinutes: >= 1 } => $"{limit.TotalMinutes:0} minute(s)",
        _ => $"{limit.TotalSeconds:0.###} second(s)"
    };

    /// <summary>
    /// Takes the oldest Pending job this worker is allowed to run. The ClaimStamp concurrency token
    /// turns a race between two workers into a lost update for one of them, so a job is never
    /// executed twice — that is what makes a second *process* safe, and it is untouched here.
    ///
    /// <para>
    /// The claim is short on purpose: read a row, write the claim, return. Execution happens outside
    /// it, so a job that runs for an hour is not an hour in which nothing else can be claimed.
    /// </para>
    /// </summary>
    private async Task<Job?> ClaimNextAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

        // A job serving a backoff is Pending but not yet due; claiming it anyway would turn the
        // backoff into a retry loop. Oldest-first still decides among everything that is due.
        var now = clock.UtcNow;
        var claimable = db.Jobs
            .Where(j => j.Status == JobStatus.Pending &&
                        (j.NextAttemptAt == null || j.NextAttemptAt <= now));

        // Rows for a target this process is already running are not skipped over silently — they are
        // excluded from the search, so oldest-first picks the next thing that CAN run instead of the
        // loop idling behind work it is not allowed to start. Written as a term per held target
        // rather than a set membership test because the pair is what excludes, not the id: a backup
        // of one thing and a deployment of another are different work whatever their guids are. The
        // list is bounded by MaxConcurrency, so this stays a handful of terms.
        foreach (var (kind, targetId) in _inFlight.Snapshot())
            claimable = claimable.Where(j => j.Kind != kind || j.TargetId != targetId);

        var candidate = await claimable.OrderBy(j => j.CreatedAt).FirstOrDefaultAsync(ct);

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

        // Taken before the row is written, so that even two claims running at once cannot both come
        // away holding one target. Everything from here to the return either hands the reservation
        // to the caller or gives it back.
        if (!_inFlight.TryReserve(candidate.Kind, candidate.TargetId)) return null;

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
            _inFlight.Release(candidate.Kind, candidate.TargetId);
            return null;
        }
        catch
        {
            _inFlight.Release(candidate.Kind, candidate.TargetId);
            throw;
        }

        return candidate;
    }

    private async Task SettleAsync(
        Guid jobId, JobStatus outcome, string? error, DateTimeOffset? nextAttemptAt, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
            if (job is null) return;

            job.Status = outcome;
            job.Error = WithinTheColumn(error);
            // Always assigned, so a shutdown that releases a job which had been backing off does
            // not leave it waiting out a wait that no longer means anything.
            job.NextAttemptAt = nextAttemptAt;
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

    /// <summary>Matches the cap on Job.Error in <c>HarboraDbContext</c>.</summary>
    private const int MaxErrorChars = 2048;

    /// <summary>
    /// The column is bounded and an exception message is not — a build failure quotes the failing
    /// command's own output. Postgres rejects the over-long value, the catch above swallows the
    /// rejection, and the row is left Running with nothing recorded on it at all: the one outcome
    /// worse than a truncated message. Head kept, because the first line is the one that says what
    /// happened.
    /// </summary>
    private static string? WithinTheColumn(string? error) =>
        error is null || error.Length <= MaxErrorChars
            ? error
            : error[..(MaxErrorChars - 1)] + "…";
}
