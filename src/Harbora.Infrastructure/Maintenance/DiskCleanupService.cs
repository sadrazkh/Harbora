using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Infrastructure.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Maintenance;

/// <summary>What one cleanup run did, in figures a person can check against <c>df</c>.</summary>
/// <param name="OrphanRemoved">Build images of deleted apps that were removed.</param>
/// <param name="RetentionRemoved">Superseded build images of living apps that were removed.</param>
/// <param name="Failed">Removals the engine refused — an image still in use is left alone by contract.</param>
/// <param name="FreedBytes">
/// The disk's own before/after difference, or null when the host does not report disk figures.
/// Deliberately measured rather than summed from image sizes: layers are shared, so adding up
/// per-image sizes overstates the win, and a cleanup that reports more than it freed is the kind
/// of number that teaches people to distrust the page.
/// </param>
public sealed record DiskCleanupResult(int OrphanRemoved, int RetentionRemoved, int Failed, long? FreedBytes);

/// <summary>
/// Frees the disk of Harbora's own leftovers, on demand.
///
/// Two sweeps, both over things the platform itself created:
///
/// <list type="number">
/// <item>Build images whose app no longer exists — <see cref="CleanupPlan.OrphanedBuildImages"/>.
/// Nothing else ever removes these, because per-app retention runs inside a deployment and a
/// deleted app deploys nothing.</item>
/// <item>Per-app retention for every living app, the same rule the pipeline runs after a cutover
/// (<see cref="DeploymentPlanning.ImagesToPrune"/>) — reused, not re-implemented, so the button
/// and the pipeline cannot disagree about what rollback keeps. This catches apps that simply have
/// not deployed since the retention setting was lowered.</item>
/// </list>
///
/// Reads are <c>IgnoreQueryFilters</c> throughout: the protection list must contain every
/// workspace's apps, or cleaning as one tenant would delete another tenant's rollback images.
/// Customer images — anything not under the build prefix — are never candidates at any point.
/// </summary>
public sealed class DiskCleanupService(
    HarboraDbContext db,
    IDockerEngine docker,
    IOptions<HarboraRuntimeOptions> options,
    ILogger<DiskCleanupService> logger)
{
    public async Task<DiskCleanupResult> RunAsync(CancellationToken ct)
    {
        var opt = options.Value;
        long? freeBefore = await FreeDiskAsync(ct);

        var apps = await db.Apps.IgnoreQueryFilters()
            .Select(a => new { a.Id, a.Slug, a.ActiveDeploymentId })
            .ToListAsync(ct);

        var onHost = await docker.ListImagesAsync(opt.ImagePrefix + "/", ct);

        var failed = 0;

        // --- 1. Orphans: build images of apps that no longer exist ---
        var orphans = CleanupPlan.OrphanedBuildImages(onHost, opt.ImagePrefix, apps.Select(a => a.Slug));
        var orphanRemoved = 0;

        foreach (var tag in orphans)
        {
            ct.ThrowIfCancellationRequested();
            try { await docker.RemoveImageAsync(tag, ct); orphanRemoved++; }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failed++;
                logger.LogWarning(e, "Cleanup could not remove orphaned image {Tag}.", tag);
            }
        }

        // --- 2. Retention for the living, exactly as the pipeline runs it ---
        var retentionRemoved = 0;

        if (opt.ImageRetentionCount > 0)
        {
            foreach (var app in apps)
            {
                ct.ThrowIfCancellationRequested();

                var history = await db.Deployments.IgnoreQueryFilters()
                    .Where(d => d.AppId == app.Id).ToListAsync(ct);

                var prunable = DeploymentPlanning.ImagesToPrune(
                    onHost, history, app.ActiveDeploymentId, opt.ImagePrefix, app.Slug, opt.ImageRetentionCount);

                foreach (var tag in prunable)
                {
                    try { await docker.RemoveImageAsync(tag, ct); retentionRemoved++; }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        failed++;
                        logger.LogWarning(e, "Cleanup could not remove superseded image {Tag}.", tag);
                    }
                }
            }
        }

        long? freeAfter = await FreeDiskAsync(ct);
        long? freed = freeBefore is { } b && freeAfter is { } a ? Math.Max(0, a - b) : null;

        logger.LogInformation(
            "Disk cleanup removed {Orphans} orphaned and {Retention} superseded image(s), {Failed} refused, freed {Freed}.",
            orphanRemoved, retentionRemoved, failed, freed is { } f ? Tenancy.ByteSize.Measured(f) : "unknown");

        return new DiskCleanupResult(orphanRemoved, retentionRemoved, failed, freed);
    }

    /// <summary>Free disk as the host reports it; null when it does not — unknown is not zero.</summary>
    private async Task<long?> FreeDiskAsync(CancellationToken ct)
    {
        try
        {
            var info = await docker.GetHostInfoAsync(ct);
            return info.FreeDiskBytes > 0 ? info.FreeDiskBytes : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }
}
