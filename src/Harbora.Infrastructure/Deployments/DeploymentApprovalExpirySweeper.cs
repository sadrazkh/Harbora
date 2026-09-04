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
/// Closes an approval request nobody answered (5.2, 2026-09 market-gaps round two). A request that
/// sat open for ever would be the same trap <c>IncidentService.ExpireStaleAsync</c> exists to close
/// for an unattended alert incident — a person who could have decided simply never did, and nothing
/// else in the platform ever revisits it on its own.
///
/// <para>
/// The deadline is not a surprise: <see cref="DeploymentApproval.ExpiresAt"/> is written the moment
/// the request is made and shown on the pending-approval banner from that point on, so this sweep
/// only ever enforces a number the requester and every approver could already see coming.
/// </para>
/// </summary>
public sealed class DeploymentApprovalExpirySweeper(
    IServiceScopeFactory scopeFactory, ISystemClock clock, ILogger<DeploymentApprovalExpirySweeper> logger)
    : BackgroundService
{
    /// <summary>Expiry is measured in hours, so minutes of drift finding out costs nothing.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try { await SweepAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Sweeping expired deployment approvals failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Expires every still-Pending approval whose deadline has passed. Public so a test can exercise
    /// it directly rather than waiting fifteen minutes to observe it.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

        var now = clock.UtcNow;
        // IgnoreQueryFilters: this runs with no session, the same reason every other sweeper in this
        // codebase reads its own table this way — the ordinary workspace filter would find nothing
        // and report a clean pass over an empty table.
        var stale = await db.DeploymentApprovals.IgnoreQueryFilters()
            .Include(a => a.Deployment)
            .Where(a => a.Decision == DeploymentApprovalDecision.Pending && a.ExpiresAt <= now)
            .ToListAsync(ct);

        if (stale.Count == 0) return 0;

        var expired = 0;
        foreach (var approval in stale)
        {
            var deployment = approval.Deployment;
            // The deployment row is gone or already moved on for some other reason (a direct cancel
            // raced this tick) — leave it rather than force a transition the state machine would
            // refuse, or resurrect a row nothing else still points at.
            if (deployment is null
                || !DeploymentStateMachine.CanTransition(deployment.Status, DeploymentStatus.Cancelled))
                continue;

            DeploymentStateMachine.Transition(deployment, DeploymentStatus.Cancelled, now);
            deployment.ErrorMessage = "Nobody approved this deployment before its approval expired.";

            approval.Decision = DeploymentApprovalDecision.Expired;
            approval.DecidedAt = now;
            expired++;
        }

        if (expired > 0) await db.SaveChangesAsync(ct);

        var audit = scope.ServiceProvider.GetService<IAuditLogger>();
        if (audit is not null)
            foreach (var approval in stale.Where(a => a.Decision == DeploymentApprovalDecision.Expired))
                await audit.LogAsync("deployment.approval.expired", "deployment", approval.DeploymentId.ToString(),
                    workspaceId: approval.WorkspaceId, ct: ct);

        if (expired > 0)
            logger.LogInformation("Expired {Count} deployment approval(s) nobody decided in time.", expired);

        return expired;
    }
}
