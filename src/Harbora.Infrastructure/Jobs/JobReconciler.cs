using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Jobs;

/// <summary>
/// Settles jobs stranded in <see cref="JobStatus.Running"/> by a crash or restart: nothing is
/// executing them any more, so they would sit Running forever and block the target's own recovery.
///
/// Runs BEFORE <c>DeploymentReconciler</c> (registration order) so that by the time deployments are
/// reconciled, no job claims to be running one. Both of them run before <c>JobStartupGateOpener</c>,
/// which is what lets <see cref="JobWorker"/> start claiming — the worker is a
/// <c>BackgroundService</c>, so registration order alone would not have held it back.
/// </summary>
public sealed class JobReconciler(
    IServiceScopeFactory scopeFactory,
    ISystemClock clock,
    ILogger<JobReconciler> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try { await ReconcileAsync(ct); }
        catch (Exception ex)
        {
            // Never block startup on reconciliation.
            logger.LogError(ex, "Job reconciliation failed on startup.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task<int> ReconcileAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

        var orphaned = await db.Jobs.Where(j => j.Status == JobStatus.Running).ToListAsync(ct);
        if (orphaned.Count == 0) return 0;

        logger.LogWarning("Settling {Count} job(s) left running by a restart.", orphaned.Count);

        foreach (var job in orphaned)
        {
            // Deliberately NOT retried. Deployments, backups and provisioning all have side effects
            // (containers started, archives written) that a blind re-run could compound; each
            // target's own reconciler decides what its half-finished state means.
            job.Status = JobStatus.Failed;
            job.Error = "Interrupted by a platform restart before completion.";
            job.FinishedAt = clock.UtcNow;
            job.ClaimStamp++;
        }

        await db.SaveChangesAsync(ct);
        return orphaned.Count;
    }
}
