using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Crash recovery (ADR-005 / fixes C2). A restart while a deployment is in flight would otherwise
/// leave its row stuck in a non-terminal state forever. On startup this reconciles every in-flight
/// deployment exactly once:
///   • Queued  → re-enqueued, but ONLY if no live job already covers it. Since Phase D the job
///     table is durable, so the usual case is that the job survived too and re-queueing here would
///     deploy twice.
///   • Building/Pushing/Deploying/HealthChecking → marked Failed ("interrupted by a restart"),
///     because a partially-built/started deployment cannot be safely resumed; the previously
///     running container (if any) keeps serving, so the app stays Running when it had one.
///   • …and the queued job of anything failed above is settled Cancelled, because a shutdown that
///     returned a job to Pending meant "resume this", and there is no longer anything to resume.
/// Idempotent: terminal deployments are untouched, so running it again is a no-op.
/// </summary>
public sealed class DeploymentReconciler(
    IServiceScopeFactory scopeFactory,
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
        var justFailed = new List<Guid>();
        foreach (var d in stranded)
        {
            if (d.Status == DeploymentStatus.Queued)
            {
                // The durable job normally survived the restart and will be picked up on its own.
                // Only heal the case where the deployment row exists without one — re-queueing
                // unconditionally would run the same deployment twice.
                var hasLiveJob = await db.Jobs.AnyAsync(
                    j => j.Kind == JobKind.Deployment && j.TargetId == d.Id &&
                         (j.Status == JobStatus.Pending || j.Status == JobStatus.Running), ct);
                if (!hasLiveJob)
                {
                    db.Jobs.Add(new Job
                    {
                        Kind = JobKind.Deployment, TargetId = d.Id,
                        // Same rule as the engine's own enqueue: a deployment queues behind its app,
                        // not behind its own row. Left unset, a deployment healed here could run
                        // beside another of the same app that survived the restart.
                        ExclusiveWith = d.AppId,
                        Status = JobStatus.Pending, CreatedAt = clock.UtcNow
                    });
                    requeued++;
                }
                continue;
            }

            // Building/Pushing/Deploying/HealthChecking: cannot be safely resumed.
            DeploymentStateMachine.Transition(d, DeploymentStatus.Failed, clock.UtcNow);
            d.ErrorMessage = "Interrupted by a platform restart before completion. Please redeploy.";
            if (d.App is not null)
                d.App.Status = d.App.ActiveDeploymentId is null ? AppStatus.Failed : AppStatus.Running;
            justFailed.Add(d.Id);
            failed++;
        }

        var dropped = await SettleJobsOfAsync(db, justFailed, ct);

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Reconciliation complete: {Requeued} re-queued, {Failed} marked failed, {Dropped} queued job(s) dropped.",
            requeued, failed, dropped);
    }

    /// <summary>
    /// Ends the queued work of deployments this pass has just failed. A graceful shutdown returns a
    /// running job to Pending so the next start resumes it, which is right for work whose target
    /// still has a future — and wrong for a deployment that has, a moment ago and in this same pass,
    /// been declared over. Left alone the job would be claimed later and hand the pipeline a
    /// terminal deployment; the deployment would then be recorded failed a second time, with a
    /// message about an illegal transition rather than about the restart.
    /// </summary>
    private async Task<int> SettleJobsOfAsync(
        HarboraDbContext db, List<Guid> deploymentIds, CancellationToken ct)
    {
        if (deploymentIds.Count == 0) return 0;

        // Pending only. A Running row belongs to JobReconciler, which runs before this one; a
        // terminal row is already settled and must never be rewritten.
        var queued = await db.Jobs
            .Where(j => j.Kind == JobKind.Deployment &&
                        j.Status == JobStatus.Pending &&
                        deploymentIds.Contains(j.TargetId))
            .ToListAsync(ct);

        foreach (var job in queued)
        {
            job.Status = JobStatus.Cancelled;
            job.Error = "The deployment this job would have run was already settled by the restart.";
            job.FinishedAt = clock.UtcNow;
            job.ClaimStamp++;
        }

        return queued.Count;
    }
}
