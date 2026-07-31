using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Tenancy;

/// <summary>
/// Measures one volume at a time, so a disk quota is checked against something real.
///
/// A quota that depends on a figure somebody has to remember to refresh is not a quota. But
/// measuring means walking a whole directory inside a container, which is minutes on a large volume
/// and competes with the work the server is actually there to do — so this takes the oldest one it
/// knows about, once every ten minutes, and no more. Everything ends up measured; nothing is
/// measured urgently.
/// </summary>
public sealed class StorageMeasurer(IServiceScopeFactory scopeFactory, ILogger<StorageMeasurer> logger)
    : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(10);

    /// <summary>How long a figure is good for before it is worth taking again.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    /// <summary>Small image whose only job is to add up what is on a volume.</summary>
    private const string MeasuringImage = "alpine:3.20";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try { await MeasureOneAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Measuring a volume failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Measures the volume that has gone longest without it, or does nothing when they are all
    /// fresh. Public because "which one is next" is worth exercising directly rather than waiting
    /// ten minutes to watch.
    /// </summary>
    public async Task MeasureOneAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<ISystemClock>();
        var engines = scope.ServiceProvider.GetRequiredService<IServerEngineFactory>();

        var stale = clock.UtcNow - StaleAfter;

        // Never measured first, then oldest. Nulls sort before dates, so one ordering does both.
        var volume = await db.Volumes.IgnoreQueryFilters()
            .Where(v => v.StorageMeasuredAt == null || v.StorageMeasuredAt < stale)
            .OrderBy(v => v.StorageMeasuredAt)
            .Select(v => new { v.Id, v.Name, ServerId = v.App!.ServerId })
            .FirstOrDefaultAsync(ct);

        if (volume is null) return;

        var docker = await engines.ResolveAsync(volume.ServerId, ct);
        var output = new System.Text.StringBuilder();

        // Read-only: measuring must not be able to change what it is measuring.
        var exit = await docker.RunOneOffAsync(new DockerOneOffRequest(
            MeasuringImage, StorageMeasurement.Command, [(volume.Name, "/data", true)]),
            new InlineProgress<string>(line => { lock (output) output.AppendLine(line); }), ct);

        var bytes = exit == 0 ? StorageMeasurement.Parse(output.ToString()) : null;

        var row = await db.Volumes.IgnoreQueryFilters().FirstOrDefaultAsync(v => v.Id == volume.Id, ct);
        if (row is null) return;

        // The timestamp is written even when the figure is not, so a volume that cannot be measured
        // does not get retried every ten minutes for ever.
        row.StorageBytes = bytes;
        row.StorageMeasuredAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        if (bytes is null)
            logger.LogWarning("Could not measure volume {Name} (exit {Exit}).", volume.Name, exit);
    }
}
