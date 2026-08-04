using Harbora.Domain.Templates;

namespace Harbora.Infrastructure.Templates;

/// <summary>
/// Which registry tags are worth adding as new versions of a template.
///
/// The bar is deliberately high. Everything this lets through becomes a row an administrator has to
/// look at, and a discovery job that adds forty variants of the same release the first time it runs
/// is a job somebody turns off — after which nothing is ever discovered again.
///
/// Nothing here publishes anything. A registry gaining a tag is not an operator deciding their
/// customers should run it.
/// </summary>
public static class RegistryDiscovery
{
    /// <summary>
    /// The most a single run will add for one template. A repository that has published two hundred
    /// releases since the catalogue was written should not produce two hundred draft rows.
    /// </summary>
    public const int MaximumPerRun = 5;

    /// <summary>
    /// The tags that should become draft versions, newest first.
    /// </summary>
    /// <param name="existing">Versions already stored for this template.</param>
    /// <param name="tags">Everything the registry lists.</param>
    public static IReadOnlyList<string> Candidates(
        IReadOnlyCollection<AppTemplateVersion> existing, IEnumerable<string> tags, int maximum = MaximumPerRun)
    {
        // Nothing readable to compare against. Guessing a shape from an empty catalogue would pull
        // in every tag the repository has ever had, in whatever form it happens to publish.
        var known = existing
            .Select(v => (Version: v, Parsed: RegistryTag.Parse(v.Version)))
            .Where(v => v.Parsed is not null)
            .ToList();

        if (known.Count == 0) return [];

        // The shape the operator already chose. A repository publishes 16, 16.4 and 16.4-alpine for
        // one release; following the shape already in the catalogue keeps offering the same kind of
        // thing rather than three names for one piece of software.
        var newest = known.MaxBy(v => v.Parsed!)!;

        return tags
            .Select(tag => (Tag: tag, Parsed: RegistryTag.Parse(tag)))
            .Where(t => t.Parsed is not null)
            .Where(t => RegistryTag.SameShape(t.Parsed!, newest.Parsed!))

            // Strictly newer, which is also what stops a tag already stored being offered again:
            // anything in the catalogue is at most the newest, so it can never be past it. An
            // explicit "not already stored" filter was written here first and removed — mutation
            // testing showed deleting it changed no outcome, and a redundant guard reads as the one
            // doing the work until somebody weakens the one that is.
            //
            // Backfilling releases older than anything offered would hand a customer a downgrade
            // dressed as a new option.
            .Where(t => t.Parsed!.CompareTo(newest.Parsed!) > 0)
            .OrderByDescending(t => t.Parsed!)
            .Select(t => t.Tag)
            .Take(Math.Max(0, maximum))
            .ToList();
    }

    /// <summary>
    /// A discovered version, ready to store.
    ///
    /// Draft, and lifecycle Stable rather than Recommended: exactly one version per template may be
    /// recommended, and choosing which is an operator's decision about their customers, not a
    /// registry's about its tags. The manifest is copied from the version it follows, because a new
    /// tag is the same software with a different number until somebody says otherwise.
    /// </summary>
    public static AppTemplateVersion Build(
        AppTemplateVersion basedOn, string tag, string digest, DateTimeOffset discoveredAt) =>
        new()
        {
            AppTemplateId = basedOn.AppTemplateId,
            Version = tag,
            ImageRepository = basedOn.ImageRepository,
            ImageTag = tag,
            ImageDigest = digest,
            Lifecycle = VersionLifecycle.Stable,
            Publication = VersionPublication.Draft,
            SupportedArchitectures = basedOn.SupportedArchitectures,
            ManifestJson = Retag(basedOn.ManifestJson, basedOn.ImageRepository, tag),
            DiscoveredAt = discoveredAt
        };

    /// <summary>
    /// The same manifest with its image pointing at the new tag.
    ///
    /// The deploy path pins by digest and would ignore this, so leaving the old tag in place would
    /// change nothing that runs — and would leave every discovered version describing the release it
    /// was copied from on every page that reads a manifest. A record that is wrong but harmless is
    /// still the record somebody will eventually rely on.
    /// </summary>
    private static string Retag(string manifestJson, string repository, string tag)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(manifestJson);
            if (node is not System.Text.Json.Nodes.JsonObject obj) return manifestJson;

            obj["image"] = $"{repository}:{tag}";
            return obj.ToJsonString();
        }
        catch (System.Text.Json.JsonException)
        {
            // A manifest that does not parse is a problem for the version it came from, not
            // something to make worse here by writing a rewritten copy of it.
            return manifestJson;
        }
    }
}
