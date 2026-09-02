using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Deployments;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Decides the one thing <see cref="DockerBuildRequest.CacheFrom"/> is allowed to name: the image
/// this exact app's own most recent successful build produced, and only when it is still verifiably
/// on the node the build is about to run on. The only caller of this class is
/// <see cref="DeploymentPipeline"/>'s single-container build path (<c>BuildFromSourceAsync</c>); a
/// Compose stack's per-service builds do not go through it yet (each service would need its own
/// history, not the app's) — <see cref="DeploymentPlanning.PreviousBuildImage"/>'s own doc explains
/// why a Compose app's recorded tag could never have matched anyway.
///
/// Two guarantees, both load-bearing — see <see cref="DockerBuildRequest.CacheFrom"/>'s own doc for
/// why they matter:
/// <list type="number">
/// <item>
/// <b>Never another app's image and never a registry reference.</b> The candidate comes from THIS
/// app's own deployment history (<see cref="DeploymentPlanning.PreviousBuildImage"/>, filtered to
/// <c>AppId == app.Id</c> and to this app's own build-tag prefix) — never a stranger's tag and never
/// a plain pull like <c>nginx:1.27</c>. A stranger's layers are never named, so this can never become
/// a cross-tenant read of another workspace's build output — an app's <c>AppId</c> already implies
/// exactly one workspace, so there is no separate workspace check to duplicate here.
/// </item>
/// <item>
/// <b>Never a tag that might not actually be there.</b> <see cref="IDockerEngine.ImageExistsAsync"/>
/// is checked immediately before the build starts, not once earlier in the pipeline. Retention
/// (<c>DeploymentPipeline.PruneOldImagesAsync</c>, and the same rule again in
/// <c>DiskCleanupService</c>'s periodic sweep) only ever protects the newest
/// <c>ImageRetentionCount</c> ROLLBACK-ELIGIBLE tags for an app — and the newest BUILD tag picked
/// here is not always among them. An app that deployed a non-build source (a prebuilt image, a
/// template pull) more recently than its last real build is the ordinary way this happens: the
/// build tag this method wants to reuse can already have aged out of the retention window and been
/// swept by the time this app builds again. A tag that has just been removed makes
/// <see cref="IDockerEngine.ImageExistsAsync"/> answer false, which this reads as "no usable
/// cache" — never a reason to fail the build.
/// </item>
/// </list>
/// </summary>
public static class BuildCache
{
    /// <summary>
    /// Resolves the cache plan for one build. Never throws for a cache that cannot be used — every
    /// branch below returns a plan with <c>CacheFrom: null</c> and a <see cref="BuildCachePlan.Reason"/>
    /// instead, so a build cache failure can never fail a deploy.
    /// </summary>
    public static async Task<BuildCachePlan> ResolveAsync(
        HarboraDbContext db, IDockerEngine docker, App app, Deployment deployment, string imagePrefix, CancellationToken ct)
    {
        if (deployment.ForceRebuild)
            return new BuildCachePlan(null,
                "cold build requested — ignoring any previous image and the engine's own layer cache.");

        // Same shape as PruneOldImagesAsync's own history read: this app's rows, unfiltered by
        // workspace scope because the pipeline's own DbContext already runs unscoped (system work),
        // and narrowed to AppId here exactly the way that read is.
        var history = await db.Deployments.AsNoTracking()
            .Where(d => d.AppId == app.Id)
            .ToListAsync(ct);

        var candidate = DeploymentPlanning.PreviousBuildImage(history, deployment.Id, imagePrefix, app.Slug);
        if (candidate is null)
            return new BuildCachePlan(null, "no previous image to cache from (first successful build for this app).");

        if (!await docker.ImageExistsAsync(candidate, ct))
            return new BuildCachePlan(null,
                $"the previous image ({candidate}) is no longer on this node — most likely image retention " +
                "reclaimed it since it stopped being a rollback target. Building cold.");

        return new BuildCachePlan([candidate], $"reusing layers from {candidate} (this app's previous successful build).");
    }
}

/// <param name="CacheFrom">
/// Handed straight to <see cref="DockerBuildRequest.CacheFrom"/>. Null is a cold build — the daemon
/// still runs its own build cache normally in that case, this only withholds an extra named source.
/// </param>
/// <param name="Reason">
/// A complete sentence for the deploy log — never blank, and always says why, one way or the other.
/// The one requirement this whole feature exists to satisfy: a fast deploy for an unexplained reason
/// is a mystery, not a feature.
/// </param>
public record BuildCachePlan(IReadOnlyList<string>? CacheFrom, string Reason);
