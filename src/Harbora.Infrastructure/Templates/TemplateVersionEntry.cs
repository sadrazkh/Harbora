using Harbora.Domain.Templates;

namespace Harbora.Infrastructure.Templates;

/// <summary>Why a version an operator typed cannot be added.</summary>
public enum VersionEntryRefusal
{
    None = 0,

    /// <summary>Nothing was typed.</summary>
    MissingTag = 1,

    /// <summary>Not a shape a registry tag can have.</summary>
    InvalidTag = 2,

    /// <summary>There is nothing to ask. The template names no image anywhere.</summary>
    UnknownRepository = 3,

    /// <summary>This template already offers that version.</summary>
    AlreadyExists = 4
}

/// <summary>What adding this tag would mean, or why it will not happen.</summary>
public sealed record VersionEntryPlan(VersionEntryRefusal Refusal, string Repository, string Tag)
{
    public bool Allowed => Refusal == VersionEntryRefusal.None;

    public static VersionEntryPlan Refused(VersionEntryRefusal refusal) => new(refusal, string.Empty, string.Empty);
}

/// <summary>
/// An operator putting a version into the dropdown by hand.
///
/// The version list could only be published or withdrawn — the entries themselves came from the
/// shipped manifests and from registry discovery, which follows the shape already in the catalogue
/// and only ever looks forward. So a template that shipped with no versions had an empty dropdown
/// forever, and an older release nobody had thought to include could not be offered at all. Which
/// versions a customer gets to choose from is an operator's decision, and there was no way to make
/// it.
///
/// The tag is checked and resolved before anything is stored, for the same reason discovery does:
/// a version row without a digest is an option on the deploy form that fails every time it is
/// chosen.
/// </summary>
public static class TemplateVersionEntry
{
    /// <summary>
    /// Whether this tag can be added, and against which repository.
    /// </summary>
    /// <param name="tag">What the operator typed.</param>
    /// <param name="repository">
    /// The repository to ask — from a version already stored, or failing that from the template's
    /// own manifest image.
    /// </param>
    /// <param name="existingVersions">Version names already offered for this template.</param>
    public static VersionEntryPlan Plan(
        string? tag, string? repository, IEnumerable<string> existingVersions)
    {
        if (string.IsNullOrWhiteSpace(tag)) return VersionEntryPlan.Refused(VersionEntryRefusal.MissingTag);

        var trimmed = tag.Trim();
        if (!ImageReference.IsUsableTag(trimmed)) return VersionEntryPlan.Refused(VersionEntryRefusal.InvalidTag);

        var repo = ImageReference.RepositoryOf(repository);
        if (repo is null) return VersionEntryPlan.Refused(VersionEntryRefusal.UnknownRepository);

        // Checked here rather than left to the unique index, which would surface as a 500 on a page
        // an operator was using correctly.
        //
        // Case-sensitive, because a container tag is: MinIO publishes
        // "RELEASE.2024-10-13T13-34-11Z", and folding case would refuse a real tag on the grounds
        // that a differently-capitalised one already exists — which is a different image.
        if (existingVersions.Any(v => string.Equals(v?.Trim(), trimmed, StringComparison.Ordinal)))
            return VersionEntryPlan.Refused(VersionEntryRefusal.AlreadyExists);

        return new VersionEntryPlan(VersionEntryRefusal.None, repo, trimmed);
    }

    /// <summary>
    /// The row to store, once the digest has been resolved.
    ///
    /// Published, unlike a discovered one. A registry gaining a tag is not a decision; an operator
    /// typing one is exactly that, and adding it as a draft would mean the button reported success
    /// and changed nothing anybody could see. Lifecycle is Stable rather than Recommended — only
    /// one version may be recommended, and choosing it stays a separate, deliberate act.
    /// </summary>
    /// <param name="basedOn">
    /// The version this template already offers, if any: architectures and manifest come from it,
    /// because a different tag of the same repository is the same software with another number.
    /// </param>
    /// <param name="templateManifestJson">
    /// Used when the template has no versions at all — the first one has nothing else to copy.
    /// </param>
    public static AppTemplateVersion Build(
        Guid templateId,
        VersionEntryPlan plan,
        string digest,
        AppTemplateVersion? basedOn,
        string? templateManifestJson)
    {
        if (!plan.Allowed) throw new InvalidOperationException("A refused plan must not be built.");

        return new AppTemplateVersion
        {
            AppTemplateId = templateId,
            Version = plan.Tag,
            ImageRepository = plan.Repository,
            ImageTag = plan.Tag,
            ImageDigest = digest,
            Lifecycle = VersionLifecycle.Stable,
            Publication = VersionPublication.Published,
            SupportedArchitectures = basedOn?.SupportedArchitectures is { Length: > 0 } arch ? arch : "amd64",
            ManifestJson = Retag(basedOn?.ManifestJson ?? templateManifestJson ?? "{}", plan.Repository, plan.Tag),

            // Deliberately not set: DiscoveredAt means "a registry check turned this up", and the
            // page shows it as such. This was typed by a person.
            DiscoveredAt = null
        };
    }

    /// <summary>The same manifest with its image pointing at this tag.</summary>
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
            return manifestJson;
        }
    }
}
