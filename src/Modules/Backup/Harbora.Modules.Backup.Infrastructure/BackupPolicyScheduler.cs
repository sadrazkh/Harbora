using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Jobs;
using Harbora.Modules.Backup.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>
/// Fires due policies, then prunes what retention no longer wants.
///
/// <para>
/// <b>Runs unscoped, and must.</b> It resolves <see cref="HarboraDbContext"/> from a background
/// scope with no <c>HttpContext</c>, which the platform's <c>HttpWorkspaceScope</c> reports as
/// unscoped, so the global query filters are bypassed and every tenant's policies are visible. A
/// version of this that somehow ran with a request scope would read an EMPTY set and log a
/// successful tick having done nothing — no exception, no alert, and every backup schedule quietly
/// never running. See ARCHITECTURE.md § 6.
/// </para>
/// </summary>
public sealed class BackupPolicyScheduler(
    IServiceScopeFactory scopeFactory,
    IOptions<BackupFeatureOptions> features,
    ILogger<BackupPolicyScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!features.Value.Backup)
        {
            logger.LogInformation("Backup module is off; the policy scheduler is not running.");
            return;
        }

        // Migrations and seeding finish first. A tick against a half-migrated schema is noise.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad tick must not take the scheduler down for the process's lifetime.
                logger.LogError(ex, "The backup policy scheduler tick failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobQueue>();
        var snapshots = scope.ServiceProvider.GetRequiredService<BackupSnapshotService>();

        var now = DateTimeOffset.UtcNow;

        var due = await db.BackupPolicies
            .Where(p => p.Enabled && (p.NextRunAt == null || p.NextRunAt <= now))
            .ToListAsync(ct);

        foreach (var policy in due)
        {
            ct.ThrowIfCancellationRequested();

            var result = await snapshots.QueueAsync(
                policy.WorkspaceId, policy.RepositoryId, policy.TargetType, policy.TargetRef,
                policy.Id, BackupTrigger.Schedule, ct);

            // NextRunAt advances whether or not the snapshot was queued. Leaving it in the past on
            // failure would re-queue the same policy every minute forever, and a target that is
            // already backing up is an ordinary reason to skip a tick rather than an error.
            policy.LastRunAt = now;
            policy.NextRunAt = BackupPolicyService.NextRun(policy, now);

            if (result.Succeeded)
                logger.LogInformation("Queued scheduled backup for policy {PolicyId} ({Name}).",
                    policy.Id, policy.Name);
            else
                logger.LogWarning("Policy {PolicyId} ({Name}) did not queue: {Error}",
                    policy.Id, policy.Name, result.Error);
        }

        if (due.Count > 0) await db.SaveChangesAsync(ct);

        await QueueMaintenanceAsync(db, jobs, now, ct);
    }

    /// <summary>
    /// Retention and repository health, queued as jobs rather than run inline.
    ///
    /// <para>
    /// Both touch storage and can take a while; doing them on the scheduler's thread would stall
    /// every other policy behind whichever repository is slowest to answer.
    /// </para>
    /// </summary>
    private static async Task QueueMaintenanceAsync(
        HarboraDbContext db, IJobQueue jobs, DateTimeOffset now, CancellationToken ct)
    {
        var pruneCutoff = now.AddHours(-6);
        var policiesToPrune = await db.BackupPolicies
            .Where(p => p.Enabled && p.LastSuccessAt != null && p.LastSuccessAt >= pruneCutoff)
            .Select(p => p.Id)
            .ToListAsync(ct);

        foreach (var policyId in policiesToPrune)
            await jobs.EnqueueAsync(JobKind.BackupPrune, policyId, ct);

        var healthCutoff = now.AddHours(-1);
        var repositories = await db.BackupRepositories
            .Where(r => r.IsEnabled && (r.LastHealthCheckAt == null || r.LastHealthCheckAt <= healthCutoff))
            .Select(r => r.Id)
            .ToListAsync(ct);

        foreach (var repositoryId in repositories)
            await jobs.EnqueueAsync(JobKind.RepositoryHealthCheck, repositoryId, ct);
    }
}
