using Harbora.Application.Abstractions;
using Harbora.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Logging;

/// <summary>
/// Ticks <see cref="ILogIngestionEngine"/> for every app that has persisted log retention turned on,
/// then enforces the shared disk budget across all of them at once — the periodic half of 2.2's
/// design (see <see cref="ILogIngestionEngine"/>'s own doc for the other half, the pre-removal flush).
///
/// <para>
/// The same fixed-interval-inside-a-fresh-scope shape <c>MetricsCollectorHostedService</c> already
/// uses, for the same reason: a long-lived <see cref="HarboraDbContext"/> shared across ticks would
/// accumulate tracked entities forever, and a scope per tick is cheap next to a poll every
/// <see cref="LogIngestionOptions.PollInterval"/>.
/// </para>
/// </summary>
public sealed class LogIngestionHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<LogIngestionOptions> options,
    ISystemClock clock,
    ILogger<LogIngestionHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(LogIngestionOptions.PollInterval);
        do
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Log ingestion tick failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Public so a test can drive exactly one pass rather than waiting on the timer.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var engine = scope.ServiceProvider.GetRequiredService<ILogIngestionEngine>();

        // Unfiltered and sessionless: this loop has no workspace of its own, by design — it serves
        // every workspace's retention-enabled apps in one pass.
        var appIds = await db.Apps.IgnoreQueryFilters()
            .Where(a => a.LogRetentionDays > 0)
            .Select(a => a.Id)
            .ToListAsync(ct);

        foreach (var appId in appIds)
        {
            ct.ThrowIfCancellationRequested();
            try { await engine.IngestAsync(appId, ct); }
            catch (Exception ex)
            {
                // One app's failure must not stop the rest from being polled — the same per-item
                // guard DataRetentionSweeper's own per-table sweep uses, for the same reason.
                logger.LogWarning(ex, "Log ingestion failed for app {AppId}; the remaining apps were still polled.", appId);
            }
        }

        // The global cap, once per tick rather than once per app: it is a cross-app concern, and
        // running it once here (after every app's own ingest and per-app trim has already happened
        // this tick) means it only ever has to reason about one consistent snapshot.
        var opt = options.Value;
        var touched = await LogBudgetEnforcer.EnforceGlobalAsync(db, opt.MaxBytesTotal, ct);
        if (touched.Count > 0)
        {
            var now = clock.UtcNow;
            var apps = await db.Apps.IgnoreQueryFilters()
                .Where(a => touched.Contains(a.Id))
                .ToListAsync(ct);
            foreach (var app in apps)
                await LogBudgetEnforcer.RecomputeBudgetCappedAsync(db, app, now, budgetTrimmedThisPass: true, ct);
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Log retention's shared disk budget trimmed the oldest lines across {Count} app(s).",
                touched.Count);
        }
    }
}
