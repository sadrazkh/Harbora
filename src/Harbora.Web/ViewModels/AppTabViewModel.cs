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

    // ---- specifics: what the panel already stores about size, placement and the live version ----
    //
    // B3 Task 1. Overview said almost nothing about what the app actually is beyond the prebuilt
    // image reference, so this carries the rest of what was already on the row: the size, the
    // replica count, the port, where it runs, the container it runs as, and the version currently
    // live. Task 2 adds a Docker inspect capability; Task 3 uses it for health and uptime — neither
    // belongs here.

    /// <summary>
    /// The resolved size for <c>App.InstanceSizeKey</c>, or null when the key matches no row.
    /// That is not the same as "no limit" — it means the panel does not know this app's limits, and
    /// has to say so rather than render a row of zeroes that reads as "unlimited".
    /// </summary>
    public Harbora.Domain.Tenancy.InstanceSize? InstanceSize { get; init; }

    /// <summary>How many container instances this app is configured to run.</summary>
    public int Replicas { get; init; }

    /// <summary>The port the app listens on inside its own container.</summary>
    public int ContainerPort { get; init; }

    /// <summary>The server this app is placed on, or null if that row no longer exists.</summary>
    public Harbora.Domain.Servers.Server? Server { get; init; }

    /// <summary>
    /// The name of the container the app's current deployment runs as — versioned when there has
    /// been one, the pre-versioning legacy name otherwise. The name somebody would look for on the
    /// host, either way.
    /// </summary>
    public string ContainerName { get; init; } = string.Empty;

    /// <summary>
    /// The deployment currently serving traffic, falling back to the most recent succeeded one, or
    /// null when this app has never deployed. An app with no deployment has no live version — that
    /// is "none", not a blank field.
    /// </summary>
    public Harbora.Domain.Deployments.Deployment? LatestDeployment { get; init; }

    /// <summary>
    /// What the engine said about <see cref="ContainerName"/> just now — how long it has been up,
    /// how often it has restarted, whether its health check passes, and the digest of the image it
    /// is actually running. B3 Task 2's capability, asked here.
    ///
    /// <para>
    /// One nullable record rather than loose fields, because null is itself the state this exists to
    /// carry: a throw, a timeout, or an engine that simply cannot answer (today, always true for a
    /// remote node — its agent has no inspect verb yet) all collapse to the same "we do not know",
    /// which the card renders as unknown rather than as a zero nobody actually reported.
    /// </para>
    /// </summary>
    public Harbora.Application.Abstractions.ContainerDetail? LiveContainer { get; init; }

    // ---- instant backup (sub-project E, Task 2) ----
    //
    // "Back up now" has to say what it would capture before it does anything — an archive of nothing
    // presented as a success is exactly the failure the spec calls out. Volumes and environment
    // variables live off this app's App.EnvironmentVariables (already loaded by Details) and this
    // list; the image reference is read the same way ApplicationTargetStager itself reads it — off
    // App.ActiveDeploymentId — so the card and the stager can never disagree about whether one exists.

    /// <summary>
    /// This app's volumes, loaded here rather than assumed from <see cref="AppTabViewModel.HasVolumes"/>
    /// so the card can name them instead of just knowing there are some.
    /// </summary>
    public IReadOnlyList<Harbora.Domain.Apps.Volume> BackupVolumes { get; init; } = [];

    /// <summary>
    /// Whether the workspace has an enabled backup repository the module can queue a snapshot into.
    /// False is not "hide the control" — B3's rule for an empty state applies here too: the card says
    /// what it would capture regardless, and only the action itself is withheld until there is
    /// somewhere for it to go.
    /// </summary>
    public bool HasBackupRepository { get; init; }
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

    /// <summary>
    /// The chart window this render answers, already resolved by
    /// <see cref="Harbora.Infrastructure.Monitoring.UsageRangeWindow.Clamp"/> — never an arbitrary
    /// value from the query string, always one of the three the range control offers. Both the
    /// control's own selected state and the chart islands' initial fetch read this, not
    /// <c>Request.Query</c> directly.
    /// </summary>
    public int SelectedMinutes { get; init; } = Harbora.Infrastructure.Monitoring.UsageRangeWindow.OneHour;
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

    // ---- how far back "instant rollback" actually reaches (sub-project F) ----

    /// <summary>
    /// <see cref="Harbora.Infrastructure.Deployments.HarboraRuntimeOptions.ImageRetentionCount"/>,
    /// carried here rather than read a second time by the view so the boundary sentence and the
    /// per-row marker below are always answering about the same configured number.
    /// </summary>
    public int ImageRetentionCount { get; init; }

    /// <summary>
    /// Which of <see cref="Deployments"/> still have their build image, by the exact rule
    /// <see cref="Harbora.Infrastructure.Deployments.DeploymentPlanning.ImagesToPrune"/> prunes
    /// against — see <see cref="Harbora.Infrastructure.Deployments.DeploymentPlanning.RollbackEligibleDeploymentIds"/>,
    /// computed once in the controller so a Rollback link marked "instant" here can never be one the
    /// pruner has already emptied. A deployment missing from this set still keeps its Rollback link
    /// (do-not-change item 23): the view marks it as needing a redeploy instead of hiding it.
    /// </summary>
    public IReadOnlySet<Guid> InstantRollbackEligibleIds { get; init; } = new HashSet<Guid>();
}
