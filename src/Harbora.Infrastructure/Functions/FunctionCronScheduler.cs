using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Functions;
using Harbora.Infrastructure.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Functions;

/// <summary>
/// Decides when a scheduled function is due, and queues the call.
///
/// <para>
/// The same rules as <see cref="CronRunner"/>, and deliberately the same parser: a five-field
/// expression must mean one thing on this platform, not one thing for a scheduled application and
/// another for a function. Missed runs are not replayed — a panel that was down for a day wakes up
/// and runs the next one, leaving the gap visible in the history rather than firing it twenty-four
/// times.
/// </para>
///
/// <para>
/// It only decides. The call itself goes through the durable queue, so a tick that fires a second
/// before a restart still results in a request being made.
/// </para>
/// </summary>
public sealed class FunctionCronScheduler(
    IServiceScopeFactory scopeFactory, ILogger<FunctionCronScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Function cron tick failed."); }

            try { await SettleAbandonedAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Settling abandoned function invocations failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// How long a queued call may stay queued before it is written off. Generous on purpose: the
    /// queue is concurrency-limited, so a burst legitimately waits, and settling a call that is
    /// merely behind would record a failure for a request that then succeeds.
    /// </summary>
    public static readonly TimeSpan AbandonedAfter = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Writes off calls that were queued and never made.
    ///
    /// <para>
    /// Without this a row whose job died with the process reads as "queued" for ever, and the
    /// history page — the one thing that answers "is this function still firing?" — fills with calls
    /// that are neither running nor finished. The row is the only place that can say so, because by
    /// the time anybody looks the job is gone.
    /// </para>
    /// </summary>
    public async Task<int> SettleAbandonedAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<ISystemClock>();
        var now = clock.UtcNow;

        var abandoned = await db.FunctionInvocations.IgnoreQueryFilters()
            .Where(i => i.CompletedAt == null && i.StartedAt < now - AbandonedAfter)
            .ToListAsync(ct);
        if (abandoned.Count == 0) return 0;

        foreach (var invocation in abandoned)
        {
            invocation.CompletedAt = now;
            invocation.Succeeded = false;
            invocation.Error = "The panel restarted before this call was made.";
            invocation.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        logger.LogWarning("Settled {Count} function invocation(s) that were queued and never made.", abandoned.Count);
        return abandoned.Count;
    }

    /// <summary>One pass over every scheduled function. Public because it is this service's behaviour.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<ISystemClock>();
        var invoker = scope.ServiceProvider.GetRequiredService<IFunctionInvoker>();
        var now = clock.UtcNow;

        // Unfiltered: this has no session, and a workspace-filtered read here would come back empty
        // every minute while reporting a healthy tick.
        var scheduled = await db.FunctionDefinitions.IgnoreQueryFilters()
            .Where(f => f.Trigger == FunctionTrigger.Cron
                     && f.CronExpression != null && f.CronExpression != ""
                     && f.IsEnabled)
            .ToListAsync(ct);

        foreach (var fn in scheduled)
        {
            if (!CronSchedule.TryParse(fn.CronExpression, out var schedule, out var error))
            {
                // Recorded once, not shouted every minute.
                if (fn.NextRunAt is not null)
                {
                    fn.NextRunAt = null;
                    logger.LogWarning("Function {Slug} has an unreadable schedule: {Error}", fn.Slug, error);
                    await db.SaveChangesAsync(ct);
                }
                continue;
            }

            // First sight: work out when it is next due rather than treating "never run" as overdue.
            if (fn.NextRunAt is null)
            {
                fn.NextRunAt = schedule!.NextOccurrence(now);
                await db.SaveChangesAsync(ct);
                continue;
            }

            if (fn.NextRunAt > now) continue;

            // Advance before queueing. A slow call, or a process that dies between here and the
            // request, must not leave the next tick treating this as still due.
            fn.NextRunAt = schedule!.NextOccurrence(now);
            await db.SaveChangesAsync(ct);

            await invoker.QueueAsync(fn.Id, FunctionTrigger.Cron, evt: null, ct);
        }
    }
}
