using Harbora.Application.Abstractions;
using Harbora.Domain.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Launch;

/// <summary>
/// Moves every launch past its confirm screen along, a few seconds apart.
///
/// <para>
/// A tick rather than a job, because a job that waited for a provision or a deployment would occupy
/// the worker that work needs, and with <c>Jobs:MaxConcurrency</c> of 1 it would wait for ever. All of
/// its state is on the rows, so a panel that restarts mid-launch simply carries on.
/// </para>
/// </summary>
public sealed class LaunchTicker(IServiceScopeFactory scopeFactory, ILogger<LaunchTicker> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ILaunchService>().AdvanceAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Launch tick failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Reading a launch's source: the only step of one that is a job.</summary>
public sealed class LaunchInspectJobHandler(ILaunchService launches) : IJobHandler
{
    public JobKind Kind => JobKind.LaunchInspect;

    public Task ExecuteAsync(Guid targetId, CancellationToken ct) => launches.InspectAsync(targetId, ct);
}
