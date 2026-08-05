using Harbora.Application.Abstractions;
using Harbora.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Storage;

/// <summary>
/// Keeps every bucket's usage figure current.
///
/// Without it the number is only ever what somebody last pressed a button for, which on a platform
/// people are billed against is not a figure at all. The button stays: an operator who has just
/// deleted a terabyte wants to see it now, not in six hours.
///
/// A few at a time, oldest first. Measuring runs a container against the storage server, and an
/// installation with two hundred buckets would otherwise spend its life starting them —
/// <see cref="BucketMeasurementSchedule"/> is where that decision lives and is tested.
/// </summary>
public sealed class BucketMeasurementSweeper(
    IServiceScopeFactory scopeFactory,
    IOptions<ObjectStorageOptions> options,
    ILogger<BucketMeasurementSweeper> logger) : BackgroundService
{
    /// <summary>
    /// How often to look. Much shorter than the staleness window on purpose: the tick decides how
    /// promptly a newly created bucket gets its first figure, and the window decides how often an
    /// existing one is asked again.
    /// </summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.IsConfigured)
        {
            // Said once, plainly, like the other modules that can be switched off. A sweep that
            // silently does nothing every fifteen minutes is indistinguishable from a broken one.
            logger.LogInformation(
                "Object storage is not configured, so bucket usage is not measured. Missing: {Missing}.",
                options.Value.WhatIsMissing());
            return;
        }

        // Not in the first seconds of startup: a control plane restarting in a loop would otherwise
        // launch a batch of containers on every restart.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try { await SweepAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Measuring bucket usage failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// One pass. Public so it can be exercised directly rather than by waiting a quarter of an hour
    /// and hoping.
    /// </summary>
    /// <returns>How many buckets were measured.</returns>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<ObjectStorageAdmin>();
        var clock = scope.ServiceProvider.GetRequiredService<ISystemClock>();

        // Unfiltered, and this is the whole reason the sweep would otherwise appear to work: there
        // is no signed-in person on a timer, so the workspace filter matches nothing and the query
        // returns an empty list. Every tick would report a clean pass over zero buckets.
        var buckets = await db.StorageBuckets.IgnoreQueryFilters()
            .Select(b => new { b.Id, b.MeasuredAt })
            .ToListAsync(ct);

        var due = BucketMeasurementSchedule.Due(
            buckets.Select(b => new MeasurableBucket(b.Id, b.MeasuredAt)), clock.UtcNow);

        var measured = 0;

        foreach (var id in due)
        {
            if (ct.IsCancellationRequested) break;

            var bucket = await db.StorageBuckets.IgnoreQueryFilters().FirstOrDefaultAsync(b => b.Id == id, ct);
            if (bucket is null) continue;

            try
            {
                var used = await storage.MeasureAsync(bucket.Name, ct);

                // The timestamp moves either way. Without that, a bucket the server will not answer
                // for is due on every tick forever and blocks the batch behind it — the figure
                // stays unknown, which is honest, but the sweep stops reaching anything else.
                bucket.MeasuredAt = clock.UtcNow;
                if (used is not null) { bucket.UsedBytes = used; measured++; }

                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                // One bucket that will not measure must not stop the rest.
                logger.LogWarning(ex, "Bucket {Bucket} could not be measured.", bucket.Name);
            }
        }

        return measured;
    }
}
