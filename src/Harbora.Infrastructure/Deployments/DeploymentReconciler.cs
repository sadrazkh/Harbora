using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Crash recovery (ADR-005 / fixes C2). The in-process job queue is not durable: a restart while a
/// deployment is in flight would otherwise leave its row stuck in a non-terminal state forever.
/// On startup this reconciles every in-flight deployment exactly once:
///   • Queued  → re-enqueued (the row survived; only the in-memory channel item was lost).
///   • Building/Pushing/Deploying/HealthChecking → marked Failed ("interrupted by a restart"),
///     because a partially-built/started deployment cannot be safely resumed; the previously
///     running container (if any) keeps serving, so the app stays Running when it had one.
/// Idempotent: terminal deployments are untouched, so running it again is a no-op.
/// </summary>
public sealed class DeploymentReconciler(
    IServiceScopeFactory scopeFactory,
    IBackgroundJobQueue queue,
    ISystemClock clock,
    ILogger<DeploymentReconciler> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await ReconcileAsync(ct);
        }
        catch (Exception ex)
        {
            // Never block startup on reconciliation; log and continue.
            logger.LogError(ex, "Deployment reconciliation failed on startup.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task ReconcileAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

        var inFlightStatuses = DeploymentStateMachine.InFlight.ToArray();
        var stranded = await db.Deployments
            .Include(d => d.App)
            .Where(d => inFlightStatuses.Contains(d.Status))
            .ToListAsync(ct);

        if (stranded.Count == 0) return;

        logger.LogWarning("Reconciling {Count} in-flight deployment(s) after restart.", stranded.Count);

        var requeued = 0;
        var failed = 0;
        foreach (var d in stranded)
        {
            if (d.Status == DeploymentStatus.Queued)
            {
                // The persisted row is the source of truth; re-schedule the work.
                var id = d.Id;
                await queue.EnqueueAsync((sp, jobCt) =>
                    sp.GetRequiredService<DeploymentPipeline>().ExecuteAsync(id, jobCt), ct);
                requeued++;
                continue;
            }

            // Building/Pushing/Deploying/HealthChecking: cannot be safely resumed.
            DeploymentStateMachine.Transition(d, DeploymentStatus.Failed, clock.UtcNow);
            d.ErrorMessage = "Interrupted by a platform restart before completion. Please redeploy.";
            if (d.App is not null)
                d.App.Status = d.App.ActiveDeploymentId is null ? AppStatus.Failed : AppStatus.Running;
            failed++;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Reconciliation complete: {Requeued} re-queued, {Failed} marked failed.", requeued, failed);
    }
}
