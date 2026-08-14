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

/// <summary>
/// The Usage tab — today's <c>Details.cshtml</c> "Resources" panel, moved rather than rewritten:
/// what the app is actually consuming against what it was allotted, and the same figures charted
/// over time. Narrower than <see cref="AppOverviewViewModel"/> on purpose — Usage never read most of
/// the entity, only these measurements and the limits they are read against.
/// </summary>
public sealed class AppUsageViewModel : AppTabViewModel
{
    public double? CpuPercent { get; init; }
    public double? MemoryUsed { get; init; }
    public long MemoryLimitBytes { get; init; }
    public double CpuLimit { get; init; }

    // Not in the brief's field list for this class — see the task report. Without it the disk row
    // cannot say "X of Y GB" or draw a share bar, which is what the moved markup did before this tab
    // existed; DiskUsedBytes alone answers "how much" but not "how much of what".
    public long DiskLimitBytes { get; init; }

    public long? DiskUsedBytes { get; init; }
    public string? DiskCaveat { get; init; }
    public DateTimeOffset? MeasuredAt { get; init; }
}

/// <summary>
/// The Volumes tab — today's <c>Details.cshtml</c> "Persistent storage" panel, moved rather than
/// rewritten: the mounted paths themselves, plus the forms that add and remove one. Narrower than
/// <see cref="AppOverviewViewModel"/> on purpose — Volumes never read the rest of the entity, only
/// its own <see cref="Harbora.Domain.Apps.Volume"/> rows.
/// </summary>
public sealed class AppVolumesViewModel : AppTabViewModel
{
    public IReadOnlyList<Harbora.Domain.Apps.Volume> Volumes { get; init; } = [];
}

/// <summary>
/// The Deployments tab — today's <c>Details.cshtml</c> "Deployments" panel, moved rather than
/// rewritten: the release history itself, and the rollback link each succeeded, inactive entry
/// offers. Narrower than <see cref="AppOverviewViewModel"/> on purpose — this tab never read the rest
/// of the entity, only its own <see cref="Harbora.Domain.Deployments.Deployment"/> rows, and only the
/// same windowed twenty <c>Details</c> always loaded.
/// </summary>
public sealed class AppDeploymentsViewModel : AppTabViewModel
{
    public IReadOnlyList<Harbora.Domain.Deployments.Deployment> Deployments { get; init; } = [];

    // Not in the brief's field list for this class — see the task report. The moved markup's
    // rollback link is drawn for a succeeded deployment that is not the one currently serving, and
    // that comparison is against the app's ActiveDeploymentId, not against anything on a Deployment
    // row itself. Without it every succeeded deployment — including the active one — would offer a
    // rollback to itself.
    public Guid? ActiveDeploymentId { get; init; }
}
