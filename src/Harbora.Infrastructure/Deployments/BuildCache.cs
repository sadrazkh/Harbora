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

    /// <summary>
    /// The one place that decides whether a failed build failed because the daemon could not restore
    /// the <c>--cache-from</c> image — the only failure a build is repeated for. Anything else (a
    /// <c>RUN npm ci</c> that exits non-zero, a bad Dockerfile, a full disk) is the app's own
    /// problem, and building it again would only make the deployment fail after twice the wait.
    ///
    /// <para>
    /// It keys on this daemon text, which Docker 29's classic builder produces intermittently while
    /// restoring a cache source it was handed:
    /// <c>failed to restore cached image from "sha256:…" to sha256:…: failed to create cache image: …</c>.
    /// The fixed part is <c>failed to restore cached image</c>; what follows it (the image ids, and
    /// the reason it could not be created) differs every time, so it is not part of the match.
    /// </para>
    ///
    /// <para>
    /// Text is the only signal there is. The daemon reports it as a free-text message with no error
    /// code: DriveUnion #44 got it as the stream's error message, Loomi #9 and #12 as an ordinary
    /// progress line, and the exception type it ends up as depends on which of those it was and on
    /// which engine ran the build — a build on a remote node's agent crosses HTTP, where a
    /// <c>DockerBuildException</c> becomes a plain <c>HttpRequestException</c>, or something else
    /// again. So this reads every message in the exception's chain and never the type, and matches
    /// whether the text is the whole message, one sentence inside a longer one, or the tail of what
    /// <see cref="FailureText.Describe"/> joined together.
    /// </para>
    /// </summary>
    public static bool IsCacheRestoreFailure(Exception? failure)
    {
        if (failure is null) return false;

        if (failure is AggregateException aggregate)
            return aggregate.InnerExceptions.Any(IsCacheRestoreFailure);

        return IsCacheRestoreFailure(failure.Message) || IsCacheRestoreFailure(failure.InnerException);
    }

    /// <summary>The same decision for text that has already been flattened — a stored deployment
    /// error, or a line of build output — see <see cref="IsCacheRestoreFailure(Exception?)"/>.</summary>
    public static bool IsCacheRestoreFailure(string? text) =>
        text is not null && text.Contains(CacheRestoreFailureText, StringComparison.OrdinalIgnoreCase);

    private const string CacheRestoreFailureText = "failed to restore cached image";
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
