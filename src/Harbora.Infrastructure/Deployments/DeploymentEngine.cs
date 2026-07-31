using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Creates the immutable <see cref="Deployment"/> record and hands the heavy lifting to a
/// queued <see cref="DeploymentPipeline"/> so the HTTP request returns immediately.
/// </summary>
public sealed class DeploymentEngine(
    HarboraDbContext db,
    IJobQueue jobs,
    ISystemClock clock) : IDeploymentEngine
{
    public async Task<Guid> QueueDeploymentAsync(DeploymentRequest request, CancellationToken ct)
    {
        // Queuing runs for whoever asked: a controller that has already checked ownership and
        // capability, or a webhook, which has no session at all. Under the tenant filter that second
        // caller sees no apps, so a push deployed nothing and said "App not found" about an app that
        // exists. The workspace is not assumed — it is read off the app below and stamped on the
        // deployment, so the row still belongs to exactly one tenant.
        var app = await db.Apps.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == request.AppId, ct)
                  ?? throw new InvalidOperationException("App not found.");

        // At most one active deployment per app (H3). Coalescing is only correct when both the
        // in-flight deployment and the new request want the SAME thing — deduping double-clicks and
        // webhook storms. A rollback is a different intent: silently handing back the id of the
        // forward deploy that is currently running would look like the rollback succeeded while it
        // was never queued, exactly when the user needs it most. Same in reverse for a deploy
        // arriving while a rollback runs. Those cases fail loudly instead.
        var inFlightStatuses = DeploymentStateMachine.InFlight.ToArray();
        var inFlight = await db.Deployments.IgnoreQueryFilters()
            .Where(d => d.AppId == app.Id && inFlightStatuses.Contains(d.Status))
            .OrderByDescending(d => d.Number)
            .Select(d => new { d.Id, d.Number, d.RolledBackFromId })
            .FirstOrDefaultAsync(ct);

        if (inFlight is not null)
        {
            var inFlightIsRollback = inFlight.RolledBackFromId is not null;
            var requestIsRollback = request.RollbackToDeploymentId is not null;

            if (inFlightIsRollback != requestIsRollback)
                throw new InvalidOperationException(
                    requestIsRollback
                        ? $"Deployment #{inFlight.Number} is still running. Wait for it to finish or cancel it, then roll back."
                        : $"A rollback (deployment #{inFlight.Number}) is still running. Wait for it to finish, then deploy.");

            return inFlight.Id;
        }

        // Filtered, this returns nothing for a webhook and every push is "deployment #1", colliding
        // with the numbers the panel already showed.
        var nextNumber = await db.Deployments.IgnoreQueryFilters().Where(d => d.AppId == app.Id)
            .Select(d => (int?)d.Number).MaxAsync(ct) ?? 0;

        var deployment = new Deployment
        {
            AppId = app.Id,
            WorkspaceId = app.WorkspaceId,
            Number = nextNumber + 1,
            Status = DeploymentStatus.Queued,
            Trigger = request.Trigger,
            GitRef = request.GitRef ?? app.GitRef,
            CommitSha = request.CommitSha,
            TriggeredByUserId = request.TriggeredByUserId,
            RolledBackFromId = request.RollbackToDeploymentId,
            SourceArchivePath = request.SourceArchivePath,
            // An explicit image is recorded up front so the pipeline releases exactly it.
            ImageTag = request.ImageOverride,
            CreatedAt = clock.UtcNow
        };
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync(ct);

        var deploymentId = deployment.Id;
        // The job row is persisted alongside the deployment, so a restart resumes it from the
        // database instead of relying on anything held in memory.
        await jobs.EnqueueAsync(JobKind.Deployment, deploymentId, ct);

        return deploymentId;
    }

    /// <summary>
    /// Cancels a deployment. Goes through <see cref="DeploymentStateMachine"/> like every other
    /// status change (ADR-004) rather than writing the column directly, so an already-terminal
    /// deployment is a silent no-op instead of an illegal backwards transition.
    /// </summary>
    public async Task CancelAsync(Guid deploymentId, CancellationToken ct)
    {
        var deployment = await db.Deployments.FirstOrDefaultAsync(d => d.Id == deploymentId, ct);
        if (deployment is null) return;
        if (!DeploymentStateMachine.CanTransition(deployment.Status, DeploymentStatus.Cancelled)) return;

        DeploymentStateMachine.Transition(deployment, DeploymentStatus.Cancelled, clock.UtcNow);
        await db.SaveChangesAsync(ct);

        // Stop the work as well as the record: a queued job is settled before it starts, and a
        // running pipeline is signalled through its cancellation token.
        await jobs.RequestCancellationAsync(JobKind.Deployment, deploymentId, ct);
    }
}
