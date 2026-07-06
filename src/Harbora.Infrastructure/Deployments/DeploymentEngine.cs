using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Creates the immutable <see cref="Deployment"/> record and hands the heavy lifting to a
/// queued <see cref="DeploymentPipeline"/> so the HTTP request returns immediately.
/// </summary>
public sealed class DeploymentEngine(
    HarboraDbContext db,
    IBackgroundJobQueue queue,
    ISystemClock clock) : IDeploymentEngine
{
    public async Task<Guid> QueueDeploymentAsync(DeploymentRequest request, CancellationToken ct)
    {
        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == request.AppId, ct)
                  ?? throw new InvalidOperationException("App not found.");

        var nextNumber = await db.Deployments.Where(d => d.AppId == app.Id)
            .Select(d => (int?)d.Number).MaxAsync(ct) ?? 0;

        var deployment = new Deployment
        {
            AppId = app.Id,
            Number = nextNumber + 1,
            Status = DeploymentStatus.Queued,
            Trigger = request.Trigger,
            GitRef = request.GitRef ?? app.GitRef,
            CommitSha = request.CommitSha,
            TriggeredByUserId = request.TriggeredByUserId,
            RolledBackFromId = request.RollbackToDeploymentId,
            CreatedAt = clock.UtcNow
        };
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync(ct);

        var deploymentId = deployment.Id;
        await queue.EnqueueAsync((sp, jobCt) =>
            sp.GetRequiredService<DeploymentPipeline>().ExecuteAsync(deploymentId, jobCt), ct);

        return deploymentId;
    }

    public async Task CancelAsync(Guid deploymentId, CancellationToken ct)
    {
        await db.Deployments.Where(d => d.Id == deploymentId &&
                (d.Status == DeploymentStatus.Queued || d.Status == DeploymentStatus.Building))
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, DeploymentStatus.Cancelled)
                .SetProperty(d => d.FinishedAt, clock.UtcNow), ct);
    }
}
