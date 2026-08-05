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

    /// <summary>
    /// The repository a hand-typed tag would be looked up on, or null when this template names no
    /// image anywhere. Shown next to the field: "add version 1.71.0" means nothing without saying
    /// of what, and it is also the address somebody may want to open and read the releases on.
    /// </summary>
    public string? Repository { get; init; }
}

/// <summary>
/// One database engine's version list. Separate from the template groups above because a database
/// is not provisioned from a template manifest — it comes from the service catalogue, which is why
/// this list had no admin page at all while the applications next to it had one.
/// </summary>
/// <param name="Shipped">What Harbora offers when nobody has chosen. Shown so an operator can see what they are replacing.</param>
/// <param name="Chosen">The operator's list, empty when they have not made one.</param>
public sealed record ServiceVersionGroupViewModel(
    Harbora.Domain.Common.ManagedServiceType Type,
    string DisplayName,
    string ImageRepository,
    IReadOnlyList<string> Shipped,
    IReadOnlyList<string> Chosen)
{
    public IReadOnlyList<string> Offered => Chosen.Count > 0 ? Chosen : Shipped;
    public bool IsOverridden => Chosen.Count > 0;
}

public sealed class TemplateVersionAdminViewModel
{
    public IReadOnlyList<TemplateVersionGroupViewModel> Templates { get; init; } = [];

    /// <summary>The database engines, whose versions live in a different place entirely.</summary>
    public IReadOnlyList<ServiceVersionGroupViewModel> Services { get; init; } = [];

    /// <summary>Whether Harbora is allowed to ask registries anything at all.</summary>
    public bool DiscoveryEnabled { get; init; }

    public int TotalDrafts => Templates.Sum(t => t.DraftCount);
}
