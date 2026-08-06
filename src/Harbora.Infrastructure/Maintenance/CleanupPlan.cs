using Harbora.Application.Abstractions;

namespace Harbora.Infrastructure.Maintenance;

/// <summary>
/// Which of Harbora's own build images belong to nobody.
///
/// Per-app retention (<see cref="Deployments.DeploymentPlanning.ImagesToPrune"/>) runs after each
/// deployment and keeps a live app's rollback window trimmed. What it can never reach is the app
/// that no longer exists: deleting an app deletes its rows, and with the rows gone nothing ever
/// visits its <c>harbora/slug:build-N</c> images again. On a build-heavy server those orphans are
/// the disk — which is how the platform ended up warning about its own leftovers.
///
/// This rule answers one question: which build-tagged images match no living app. The boundaries
/// are the whole rule:
///
/// <list type="bullet">
/// <item>Only tags under <paramref name="imagePrefix"/> are candidates. <c>nginx:1.27</c> belongs
/// to the user, not to us, and is never even considered.</item>
/// <item>A compose service builds as <c>{prefix}/{slug}-{service}:…</c>, so a live slug protects
/// its own name <em>and</em> anything under <c>{slug}-</c>.</item>
/// <item>Comparison is ordinal. Container tags are case-sensitive; folding case would let
/// <c>Shop</c> protect <c>shop</c>, which are two different apps.</item>
/// </list>
/// </summary>
public static class CleanupPlan
{
    /// <summary>Build images whose app no longer exists, ready to be removed.</summary>
    /// <param name="onHost">Images on the engine, already filtered to the build prefix or not — re-filtered here either way.</param>
    /// <param name="imagePrefix">The build prefix, e.g. "harbora".</param>
    /// <param name="liveSlugs">Every existing app's slug, across all workspaces.</param>
    public static IReadOnlyList<string> OrphanedBuildImages(
        IEnumerable<ImageInfo> onHost,
        string imagePrefix,
        IEnumerable<string> liveSlugs)
    {
        var prefix = imagePrefix + "/";
        var slugs = liveSlugs.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

        return onHost
            .Select(i => i.Tag)
            .Where(t => t.StartsWith(prefix, StringComparison.Ordinal))
            .Where(t => !BelongsToALivingApp(t, prefix, slugs))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool BelongsToALivingApp(string tag, string prefix, IReadOnlyList<string> slugs)
    {
        var name = tag[prefix.Length..];
        var colon = name.IndexOf(':');
        if (colon >= 0) name = name[..colon];

        // A tag with no name part is not something this rule understands; leaving it alone is the
        // safe reading of "unknown".
        if (name.Length == 0) return true;

        foreach (var slug in slugs)
        {
            if (string.Equals(name, slug, StringComparison.Ordinal)) return true;

            // The dash is load-bearing: app "shop" owns "shop-api" (its compose service) but has no
            // claim on "shopx", which is somebody else's app.
            if (name.StartsWith(slug + "-", StringComparison.Ordinal)) return true;
        }

        return false;
    }
}
