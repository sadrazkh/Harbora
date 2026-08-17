using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Creates the immutable <see cref="Deployment"/> record and hands the heavy lifting to a
/// queued <see cref="DeploymentPipeline"/> so the HTTP request returns immediately.
///
/// <para>
/// P7 (2026-08-17 app-environment-management design): two pre-flight checks live here rather than
/// at each of the eleven places that call <see cref="QueueDeploymentAsync"/>, the same reasoning
/// the PAYG start gate's own comment gives for its own single home. <paramref name="scheduler"/>
/// and <paramref name="monitoringOptions"/> are optional constructor parameters — both are always
/// resolved by DI in production, and staying optional is what lets the many direct-construction
/// unit tests of this class keep passing three arguments without also having to fabricate a node
/// and a disk figure they were never testing.
/// </para>
/// </summary>
public sealed class DeploymentEngine(
    HarboraDbContext db,
    IJobQueue jobs,
    ISystemClock clock,
    IQuotaService? quota = null,
    ISchedulerService? scheduler = null,
    IOptions<Monitoring.MonitoringOptions>? monitoringOptions = null) : IDeploymentEngine
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
        await using var quotaReservation = quota is null
            ? NoopQuotaReservation.Instance
            : await quota.AcquireCreationLockAsync(app.WorkspaceId, ct);

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

        if (quota is not null)
        {
            var mayQueue = await quota.CanQueueDeploymentAsync(app.WorkspaceId, ct);
            if (!mayQueue.Allowed)
                throw new QuotaRefusedException(mayQueue);
        }

        // P7: "SchedulerService.CheckAsync already exists and is called once. Calling it at queue
        // time is the whole of the item." The node this app was placed on when it was created can
        // have filled up since — another app's growth, a host that shrank its own headroom — and
        // building a release nobody can run is a worse failure than refusing before the build ever
        // starts.
        //
        // The headroom asked for is the app's own footprint again, not zero: a deploy briefly runs
        // two containers side by side — the new one health-checking, the old one still serving until
        // cutover — and the app's steady-state allocation is already counted as committed on its
        // node. This is asking whether there is room for that transient second copy, which is a real
        // question even though the app "already fits" in its usual, one-copy sense.
        if (scheduler is not null)
        {
            var placement = await scheduler.CheckAsync(app.ServerId, app.MemoryLimitBytes, app.CpuLimit, ct);
            if (!placement.Ok)
                throw new CapacityRefusedException(placement);
        }

        // P7, the owner's answer to §7 Q5: refuses rather than warns — MetricsCollector's own
        // DiskWarnRatio path already warns at this same kind of figure, so warning here would
        // deliver nothing new. Reads Node.FreeDiskBytes, which the platform already collects on
        // every heartbeat; no null and no zero (never reported) is read as "definitely full" — both
        // mean nothing has measured this node yet, the same "unknown means allow" NodeCapacity.CanFit
        // already reads a zero allocatable figure as.
        if (monitoringOptions is not null)
        {
            var threshold = monitoringOptions.Value.DeployMinFreeDiskBytes;
            var freeBytes = await db.Nodes.AsNoTracking()
                .Where(n => n.ServerId == app.ServerId).Select(n => (long?)n.FreeDiskBytes).FirstOrDefaultAsync(ct);

            if (freeBytes is > 0 && freeBytes < threshold)
            {
                var freeText = Services.StorageMeasurement.Describe(freeBytes);
                var thresholdText = Services.StorageMeasurement.Describe(threshold);
                throw new LowDiskRefusedException(freeBytes.Value, threshold,
                    reason: $"Only {freeText} free on this node; deploys are refused below {thresholdText}.",
                    reasonFa: $"تنها {freeText} فضای آزاد روی این سرور مانده؛ استقرار زیر {thresholdText} رد می‌شود.");
            }
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
        //
        // Queued against the APP, not the deployment. The work is this deployment and the pipeline
        // is handed its id, but what must never have two of these running at once is the app: two
        // deployment rows are two different targets, and the coalescing above is a read followed by
        // an insert with nothing between them, so a double-click, a CLI call racing a webhook or a
        // redelivered push can still produce two rows. Serialising them here is what stops that
        // becoming two docker builds, two containers under one name and two proxy applies.
        await jobs.EnqueueExclusiveAsync(
            JobKind.Deployment, deploymentId, exclusiveWith: app.Id, workspaceId: app.WorkspaceId, ct);
        await quotaReservation.CommitAsync(ct);

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
