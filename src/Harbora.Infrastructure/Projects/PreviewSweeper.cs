using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Projects;

/// <summary>
/// Removes previews whose branches went quiet.
///
/// The branch-deleted webhook covers the tidy case. This covers the ordinary one: a branch abandoned
/// rather than deleted, a repository disconnected, a webhook that never arrived. Without it the
/// feature is a slow leak — services nobody remembers, running for months, counted against a
/// tenant's plan.
/// </summary>
public sealed class PreviewSweeper(IServiceScopeFactory scopeFactory, ILogger<PreviewSweeper> logger)
    : BackgroundService
{
    /// <summary>Nothing here is urgent — the lifetime is measured in days.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try { await SweepAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Sweeping expired previews failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Removes everything past its idle lifetime. Public because "which ones go" is worth being able
    /// to exercise directly rather than waiting six hours to observe.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var previews = scope.ServiceProvider.GetRequiredService<PreviewEnvironmentService>();

        var expired = await previews.ExpiredAsync(ct);
        var removed = 0;

        foreach (var preview in expired)
        {
            try
            {
                await previews.RemoveAsync(preview.Id, ct);
                removed++;
            }
            catch (Exception ex)
            {
                // One preview that will not go must not stop the rest — that is how a leak becomes
                // permanent.
                logger.LogWarning(ex, "Could not remove expired preview {Slug}.", preview.Slug);
            }
        }

        if (removed > 0)
            logger.LogInformation("Removed {Count} preview(s) whose branches went quiet.", removed);

        return removed;
    }
}
