using Harbora.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Modules.Sync.Infrastructure;

/// <summary>
/// Keeps each sync space's status and conflict list current.
///
/// <para>
/// A poll rather than a subscription to Syncthing's event stream: the event API is long-polling, and
/// a timer that cannot wedge is worth more here than freshness measured in seconds. Sync status is
/// something people look at, not something anything else depends on.
/// </para>
/// <para>
/// <b>Runs unscoped, and must.</b> It resolves the DbContext from a background scope with no
/// HttpContext, which the platform reports as unscoped, so every tenant's spaces are visible. With a
/// request scope it would read an EMPTY set and log a successful tick having refreshed nothing —
/// every space would sit on whatever status it was last given, looking healthy (ARCHITECTURE.md § 6).
/// </para>
/// </summary>
public sealed class SyncStatusRefresher(
    IServiceScopeFactory scopeFactory,
    IOptions<SyncFeatureOptions> features,
    IOptions<SyncthingOptions> syncthing,
    ILogger<SyncStatusRefresher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!features.Value.Sync)
        {
            logger.LogInformation("Sync module is off; the status refresher is not running.");
            return;
        }

        if (string.IsNullOrWhiteSpace(syncthing.Value.ApiKey))
        {
            // Said once, loudly. A refresher that silently fails every tick would leave every space
            // showing whatever it was last given.
            logger.LogWarning(
                "Sync is enabled but no Syncthing API key is configured, so no status can be read.");
            return;
        }

        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(syncthing.Value.StatusRefreshInterval);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad tick must not take the refresher down for the process's lifetime.
                logger.LogError(ex, "The sync status refresh failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var spaces = scope.ServiceProvider.GetRequiredService<SyncSpaceService>();

        var ids = await db.SyncSpaces
            .Where(s => s.EngineFolderId != null)
            .Select(s => s.Id)
            .ToListAsync(ct);

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();

            // One space failing must not stop the rest: an unreachable folder is exactly the kind of
            // thing the other spaces' status is needed to diagnose.
            try
            {
                await spaces.RefreshAsync(id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not refresh sync space {SpaceId}.", id);
            }
        }
    }
}
