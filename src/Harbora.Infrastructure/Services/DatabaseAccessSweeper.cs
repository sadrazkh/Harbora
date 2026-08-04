using Harbora.Domain.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// Closes database access that has run out of time.
///
/// Without this, "temporary" is a label on a permanent credential. The expiry stored on the row is
/// only a promise until something acts on it — and the thing that acts on it has to keep running,
/// which is why one grant that will not close is logged and stepped over rather than allowed to
/// stop the sweep.
///
/// The tick is short relative to the shortest window on offer. A fifteen-minute grant closed twenty
/// minutes late is a grant that was open a third longer than the person was told.
/// </summary>
public sealed class DatabaseAccessSweeper(
    IServiceScopeFactory scopeFactory,
    ILogger<DatabaseAccessSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try { await SweepAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Sweeping expired database access failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Closes everything past its time. Public so "which grants go" can be exercised directly
    /// rather than by waiting a minute and hoping.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<DatabaseAccessService>();

        var expired = await access.ExpiredAsync(ct);
        var closed = 0;

        foreach (var grant in expired)
        {
            try
            {
                await access.CloseAsync(grant, DatabaseAccessStatus.Expired, "Access window ended.", null, ct);
                closed++;
            }
            catch (Exception ex)
            {
                // One grant that will not close must not stop the rest — that is how a single stuck
                // row leaves every later expiry open indefinitely.
                logger.LogWarning(ex, "Could not close expired database access {Grant}.", grant.Id);
            }
        }

        if (closed > 0)
            logger.LogInformation("Closed {Count} expired database access grant(s).", closed);

        return closed;
    }
}
