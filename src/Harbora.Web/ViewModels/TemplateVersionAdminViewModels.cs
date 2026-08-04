using Harbora.Domain.Templates;

namespace Harbora.Web.ViewModels;

/// <summary>One template and every version it has, drafts first.</summary>
public sealed class TemplateVersionGroupViewModel
{
    public required AppTemplate Template { get; init; }
    public IReadOnlyList<AppTemplateVersion> Versions { get; init; } = [];

    /// <summary>Waiting for a decision. The number worth showing on the page header.</summary>
    public int DraftCount => Versions.Count(v => v.Publication == VersionPublication.Draft);

    /// <summary>
    /// True when nothing here can be deployed. A template with versions and none offered looks
    /// identical on the catalogue page to one that works, right up until somebody tries.
    /// </summary>
    public bool NothingOffered => Versions.Count > 0 && Versions.All(v =>
        v.Publication != VersionPublication.Published || v.Lifecycle == VersionLifecycle.Unsupported);
}

public sealed class TemplateVersionAdminViewModel
{
    public IReadOnlyList<TemplateVersionGroupViewModel> Templates { get; init; } = [];

    /// <summary>Whether Harbora is allowed to ask registries anything at all.</summary>
    public bool DiscoveryEnabled { get; init; }

    public int TotalDrafts => Templates.Sum(t => t.DraftCount);
}
