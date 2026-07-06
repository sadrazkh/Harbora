using Harbora.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Monitoring;

/// <summary>Ticks the metrics collector on a fixed interval inside a fresh DI scope.</summary>
public sealed class MetricsCollectorHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<MetricsCollectorHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IMetricsCollector>().CollectAsync(stoppingToken);
            }
            catch (Exception ex) { logger.LogError(ex, "Metrics collection tick failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
