using Harbora.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Jobs;

/// <summary>Drains the background job queue, running each job inside its own DI scope.</summary>
public sealed class BackgroundJobWorker(
    IBackgroundJobQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<BackgroundJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Harbora background job worker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            Func<IServiceProvider, CancellationToken, Task> job;
            try { job = await queue.DequeueAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }

            using var scope = scopeFactory.CreateScope();
            try
            {
                await job(scope.ServiceProvider, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background job failed.");
            }
        }
    }
}
