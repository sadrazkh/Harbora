namespace Harbora.Web.ViewModels;

/// <summary>
/// What the app shell's header and tab strip need, on every tab.
///
/// <para>
/// A base class rather than ViewData: the shell is typed to this, so a tab that forgets to supply
/// the header fails to compile instead of rendering a page with an empty title.
/// </para>
/// </summary>
public abstract class AppTabViewModel
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string Slug { get; init; } = "";
    public Harbora.Domain.Common.ServiceKind Kind { get; init; }
    public Harbora.Domain.Common.AppStatus Status { get; init; }

    /// <summary>Which tab is drawn as current. One of: overview, usage, volumes, deployments.</summary>
    public string CurrentTab { get; init; } = "overview";

    // The header block moved into _Shell.cshtml verbatim (do not retype it), and its subtitle line
    // and Data button read these four fields on the raw App entity today. The shell can only see
    // members declared on this base class — it does not know which concrete tab model it was handed
    // — so a header field the brief's six-field list left off still had to land here, not on any one
    // tab's own model.

    /// <summary>Where this app's image/build comes from. Drawn in the header's subtitle line.</summary>
    public Harbora.Domain.Common.AppSourceType SourceType { get; init; }

    /// <summary>The linked repository's "owner/name", or null when this app has none.</summary>
    public string? GitRepositoryFullName { get; init; }

    /// <summary>The resource tier's key, or null when the app has none.</summary>
    public string? InstanceSizeKey { get; init; }

    /// <summary>Whether the header's Data button has somewhere to send someone.</summary>
    public bool HasVolumes { get; init; }
}

/// <summary>
/// The Overview tab — today's <c>Details.cshtml</c>, unmoved by this task. Wraps the loaded
/// <see cref="Harbora.Domain.Apps.App"/> rather than re-declaring its many fields on this class,
/// because Overview alone still reads most of them; Tasks 3-5 give Usage, Volumes and Deployments
/// their own narrower models instead of carrying the whole entity.
/// </summary>
public sealed class AppOverviewViewModel : AppTabViewModel
{
    public required Harbora.Domain.Apps.App App { get; init; }
}
