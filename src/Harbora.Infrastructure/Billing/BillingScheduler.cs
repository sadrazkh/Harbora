using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Billing;
using Harbora.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Harbora.Infrastructure.Billing;

/// <summary>
/// Finds ended UTC hours and places them on the existing durable queue. The timer only discovers
/// work; the BillingRun and Job rows are the state that survives a restart.
/// </summary>
public sealed class BillingScheduler(
    IServiceScopeFactory scopeFactory,
    IOptions<BillingOptions> options,
    ILogger<BillingScheduler> logger) : BackgroundService
{
    internal static readonly Guid ExclusiveKey = new("00000000-0000-0000-0000-00000000b111");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ScheduleDueAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Scheduling the hourly billing pass failed.");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Clamp(options.Value.SchedulerPollSeconds, 10, 3600)),
                    stoppingToken);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Public so the restart, backfill and duplicate-discovery contract can be tested directly.</summary>
    public async Task ScheduleDueAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<ISystemClock>();
        var now = clock.UtcNow.ToUniversalTime();
        var endedHour = TopOfHour(now).AddHours(-1);
        var retryBefore = now.AddMinutes(-Math.Clamp(options.Value.IncompleteRetryMinutes, 1, 24 * 60));

        // Retry incomplete accounting before adding later hours. All billing jobs share one
        // ExclusiveWith key, so the ordinary FIFO claim order keeps the older repair ahead of the
        // newly ended hour even when both are discovered in this pass.
        var retryable = await db.BillingRuns
            .Where(r => r.Status == BillingRunStatus.Queued
                        || (r.Status == BillingRunStatus.Incomplete && r.UpdatedAt <= retryBefore)
                        || (r.Status == BillingRunStatus.Running && r.UpdatedAt <= retryBefore))
            .OrderBy(r => r.BillingHour)
            .ToListAsync(ct);

        foreach (var run in retryable)
        {
            var live = await db.Jobs.AnyAsync(j =>
                j.Kind == JobKind.BillingHour && j.TargetId == run.Id &&
                (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct);
            if (!live) Queue(db, run, now);
        }

        var latest = await db.BillingRuns
            .OrderByDescending(r => r.BillingHour)
            .Select(r => (DateTimeOffset?)r.BillingHour)
            .FirstOrDefaultAsync(ct);

        // First activation starts with the hour that just ended. It must not retroactively charge
        // every hour since the software was installed while billing was deliberately switched off.
        var next = latest is { } last ? TopOfHour(last).AddHours(1) : endedHour;
        var limit = Math.Max(1, options.Value.MaxBackfillHours);

        for (var count = 0; next <= endedHour && count < limit; count++, next = next.AddHours(1))
        {
            var run = new BillingRun
            {
                BillingHour = next,
                Status = BillingRunStatus.Queued,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.BillingRuns.Add(run);
            Queue(db, run, now);
        }

        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
    }

    internal static void Queue(HarboraDbContext db, BillingRun run, DateTimeOffset now)
    {
        run.Status = BillingRunStatus.Queued;
        run.UpdatedAt = now;
        db.Jobs.Add(new Job
        {
            Kind = JobKind.BillingHour,
            TargetId = run.Id,
            ExclusiveWith = ExclusiveKey,
            Status = JobStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    private static DateTimeOffset TopOfHour(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }
}

public sealed record BillingRunRetryResult(bool Queued, bool AlreadyQueued);

/// <summary>
/// Gives an operator one safe path for retrying an incomplete billing hour. The database's partial
/// unique index on live billing jobs is the final guard when two administrators click together.
/// </summary>
public sealed class BillingRunRetryService(HarboraDbContext db, ISystemClock clock)
{
    public async Task<BillingRunRetryResult> RetryAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.BillingRuns.FirstOrDefaultAsync(r => r.Id == runId, ct)
                  ?? throw new InvalidOperationException("Billing run does not exist.");

        if (run.Status == BillingRunStatus.Succeeded)
            throw new InvalidOperationException("A completed billing run cannot be retried.");

        if (await HasLiveJobAsync(runId, ct))
            return new BillingRunRetryResult(false, true);

        BillingScheduler.Queue(db, run, clock.UtcNow.ToUniversalTime());
        try
        {
            await db.SaveChangesAsync(ct);
            return new BillingRunRetryResult(true, false);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another request won the race. Do not report an error for the safe, desired outcome.
            db.ChangeTracker.Clear();
            if (await HasLiveJobAsync(runId, ct))
                return new BillingRunRetryResult(false, true);
            throw;
        }
    }

    private Task<bool> HasLiveJobAsync(Guid runId, CancellationToken ct) => db.Jobs.AnyAsync(j =>
        j.Kind == JobKind.BillingHour && j.TargetId == runId &&
        (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct);
}

/// <summary>Executes one persisted BillingRun and leaves an inspectable result for the operator.</summary>
public sealed class BillingRunHandler(
    HarboraDbContext db,
    BillingTick tick,
    ISystemClock clock,
    IOptions<BillingOptions> options,
    ILogger<BillingRunHandler> logger)
{
    public async Task ExecuteAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.BillingRuns.FirstOrDefaultAsync(r => r.Id == runId, ct)
                  ?? throw new InvalidOperationException($"Billing run {runId} does not exist.");

        // The durable queue may replay a job whose handler committed before the worker could mark
        // the job itself complete. The ledger is idempotent too, but a completed run has no work to
        // rediscover and should retain its original counters and timestamps.
        if (run.Status == BillingRunStatus.Succeeded) return;

        run.Status = BillingRunStatus.Running;
        run.StartedAt = clock.UtcNow;
        run.CompletedAt = null;
        run.Attempts++;
        run.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        if (!options.Value.Enabled)
        {
            Incomplete(run, "Billing was disabled before this queued hour ran; it will be retried after billing is enabled.");
            await db.SaveChangesAsync(ct);
            return;
        }

        try
        {
            var result = await tick.ChargeHourAsync(run.BillingHour, ct);
            run.WorkspacesCharged = result.WorkspacesCharged;
            run.LinesWritten = result.LinesWritten;
            run.WorkspacesSuspended = result.WorkspacesSuspended;
            run.FailureSummary = Truncate(string.Join(Environment.NewLine, result.Failures));
            run.Status = result.AccountingComplete
                ? BillingRunStatus.Succeeded
                : BillingRunStatus.Incomplete;
            run.CompletedAt = clock.UtcNow;
            run.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);

            if (!result.AccountingComplete)
                logger.LogWarning(
                    "Billing hour {Hour} was incomplete and will be retried: {Failures}",
                    run.BillingHour, run.FailureSummary);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Incomplete(run, ex.Message);
            await db.SaveChangesAsync(ct);
            logger.LogError(ex, "Billing hour {Hour} failed and will be offered again.", run.BillingHour);
        }
    }

    private void Incomplete(BillingRun run, string failure)
    {
        run.Status = BillingRunStatus.Incomplete;
        run.FailureSummary = Truncate(failure);
        run.CompletedAt = clock.UtcNow;
        run.UpdatedAt = clock.UtcNow;
    }

    private static string Truncate(string value) => value.Length <= 4000 ? value : value[..4000];
}
