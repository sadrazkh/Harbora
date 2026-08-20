using Harbora.Domain.Common;
using Harbora.Domain.Functions;
using Harbora.Domain.Git;
using Harbora.Domain.Networking;
using Harbora.Domain.Deployments;

namespace Harbora.Domain.Apps;

/// <summary>
/// A deployable application (the "project" in the UI). Holds source configuration,
/// build settings and runtime configuration; concrete container instances are produced
/// by <see cref="Deployment"/>s.
/// </summary>
public class App : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>
    /// Which project environment this belongs to. Required (P2, 2026-08-17
    /// app-environment-management design): the 2026-07-30 backfill placed every app that existed
    /// before this column did, and every creation path has set it since, so a workload with no
    /// environment cannot be created any more.
    /// </summary>
    public Guid EnvironmentId { get; set; }
    public Harbora.Domain.Projects.Environment? Environment { get; set; }

    /// <summary>
    /// What this unit is for. Everything that existed before this column is <see cref="ServiceKind.Web"/>,
    /// which is the default, so the deploy engine's behaviour is unchanged for every current app.
    /// </summary>
    public ServiceKind Kind { get; set; } = ServiceKind.Web;

    /// <summary>
    /// A command run once from the new image before traffic moves to it — database migrations, most
    /// often. A non-zero exit fails the deployment and the current version keeps serving, which is the
    /// entire reason this runs before the cutover rather than inside the container's own start-up.
    /// </summary>
    public string? ReleaseCommand { get; set; }

    /// <summary>
    /// Whether a push to any other branch gets an environment of its own. Off by default: every
    /// branch quietly becoming a running service is a surprise, and a bill.
    /// </summary>
    public bool PreviewsEnabled { get; set; }

    /// <summary>The service this one is a preview of, or null for an ordinary service.</summary>
    public Guid? PreviewOfAppId { get; set; }

    /// <summary>The branch a preview follows, and when it last saw a push.</summary>
    public string? PreviewBranch { get; set; }
    public DateTimeOffset? PreviewLastPushedAt { get; set; }

    /// <summary>Five-field cron expression for <see cref="ServiceKind.Cron"/> services.</summary>
    public string? CronExpression { get; set; }

    /// <summary>
    /// What a scheduled job actually runs. Separate from <see cref="BuildCommand"/> on purpose: for a
    /// job built from a repository those are two different commands, and running the build command on
    /// a schedule is not what anyone means. Empty runs the image as its author intended.
    /// </summary>
    public string? Command { get; set; }

    /// <summary>Computed by the cron runner so a due job can be found without re-parsing everything.</summary>
    public DateTimeOffset? NextRunAt { get; set; }

    /// <summary>
    /// Which host image runs this app's inline code, for <see cref="AppSourceType.InlineCode"/> —
    /// null for every other kind of app.
    ///
    /// <para>
    /// Here rather than on a table of its own for the same reason <see cref="CronExpression"/> is: a
    /// function app is an app, and giving it a parallel entity would mean re-earning deploy history,
    /// rollback, env vars, domains, quotas and metering that already work on this one.
    /// </para>
    /// </summary>
    public FunctionRuntime? FunctionRuntime { get; set; }

    /// <summary>
    /// Shared secret the panel presents when it invokes a function on a schedule or an event. Stored
    /// through <c>ISecretProtector</c> like every other credential.
    ///
    /// <para>
    /// It exists because the invocation door is inside the tenant's own network: without it, any
    /// container a customer runs beside their function app could fire that app's cron handlers.
    /// </para>
    /// </summary>
    public string? FunctionInvokeSecret { get; set; }

    public Guid ServerId { get; set; }

    public string Name { get; set; } = string.Empty;
    /// <summary>URL/DNS-safe unique slug; also the docker network/label namespace.</summary>
    public string Slug { get; set; } = string.Empty;

    public AppSourceType SourceType { get; set; }
    public AppStatus Status { get; set; } = AppStatus.Created;

    /// <summary>
    /// Whether this app was running at the moment its workspace was suspended, so resumption starts
    /// what the outage stopped and nothing else. An app the customer had stopped themselves must not
    /// come back and start spending the money they just put in.
    ///
    /// <para>
    /// Written before anything is stopped, for the same reason the node agent persists its drain flag
    /// before any stop: a panel that dies halfway through suspending ten apps must still know all ten
    /// were running, or the outage quietly becomes the customer's new configuration.
    /// </para>
    ///
    /// <para>
    /// A suspension that is starting rebuilds the set from what is running; one that is retrying only
    /// adds to it. Both halves are needed and they fail in opposite directions — always rebuilding
    /// erases the record on the second pass, since by then the apps are stopped because the first
    /// pass stopped them, while always adding lets a marker stranded by an unfinished resumption
    /// start an app the customer stopped themselves.
    /// </para>
    /// </summary>
    public bool WasRunningAtSuspension { get; set; }

    // --- Git source (SourceType = GitRepository) ---
    public Guid? GitRepositoryId { get; set; }
    public GitRepository? GitRepository { get; set; }
    public string? GitRef { get; set; }            // branch or tag to track
    public bool AutoDeployOnPush { get; set; } = true;
    public string? DeployOnTagPattern { get; set; } // e.g. "v*"

    // --- Build config ---
    public string? DockerfilePath { get; set; } = "Dockerfile";
    public string? ComposeFilePath { get; set; }
    public string? BuildContextPath { get; set; } = ".";
    public string? BuildCommand { get; set; }
    public string? PrebuiltImage { get; set; }     // SourceType = PrebuiltImage

    // --- Runtime config ---
    public int ContainerPort { get; set; } = 80;   // port the app listens on inside the container

    /// <summary>
    /// What the last deployment did about this app's private name, so the page can say "no address,
    /// and here is why" instead of showing a blank. Recomputing it on render would need a live Docker
    /// call per app, and would answer for the network as it is now rather than as it was when this app
    /// last shipped.
    /// </summary>
    public PrivateAddressOutcome? PrivateAddressState { get; set; }

    /// <summary>Host port a remote node publishes the container on, so Traefik can route to it cross-node.</summary>
    public int? PublishedHostPort { get; set; }
    public int? DesiredReplicas { get; set; } = 1;
    public string? HealthCheckPath { get; set; } = "/";
    /// <summary>Chosen instance-size key; drives the container CPU/memory limits below.</summary>
    public string? InstanceSizeKey { get; set; }
    public long MemoryLimitBytes { get; set; }
    public double CpuLimit { get; set; }

    /// <summary>
    /// The disk the chosen tier comes with, copied here for the same reason memory is: the tier can
    /// be edited or withdrawn later, and a page reporting "18 GB of 40 GB" must keep meaning what
    /// it meant when the app was placed. Zero is no ceiling.
    /// </summary>
    public long DiskLimitBytes { get; set; }

    /// <summary>Id of the deployment currently serving traffic (for rollback comparisons).</summary>
    public Guid? ActiveDeploymentId { get; set; }

    public Guid? TemplateId { get; set; }

    /// <summary>
    /// Which version of that template this app was created from.
    ///
    /// Recorded rather than inferred from the image: a digest alone answers "what is running" but
    /// not "which of our versions is that", so without this column nobody can tell who is on the
    /// release being deprecated. Null for apps created before versions existed, and for templates
    /// that have none.
    /// </summary>
    public Guid? TemplateVersionId { get; set; }

    // --- Maintenance mode (P5, 2026-08-20 platform-options plan) ---

    /// <summary>
    /// Whether visitors to this app's hosts currently see a themed maintenance page instead of the
    /// app itself. The containers keep running — this changes only what the proxy routes to.
    /// Stopping the app is a separate, pre-existing action and this flag says nothing about it.
    ///
    /// <para>
    /// Written only after <c>IProxyEngine.ApplyAllAsync</c> has actually succeeded
    /// (<c>AppOperationsService.SetMaintenanceModeAsync</c>) — never optimistically before the apply
    /// is known to have worked. A flag that reads "on" while the proxy never learned about it would
    /// be the defect class this codebase spent 2026-08-20 removing from the monitoring page, worn
    /// here instead.
    /// </para>
    /// </summary>
    public bool MaintenanceMode { get; set; }

    /// <summary>Optional message shown on the maintenance page (English/default). Null shows a
    /// generic sentence instead.</summary>
    public string? MaintenanceMessage { get; set; }

    /// <summary>The Persian counterpart of <see cref="MaintenanceMessage"/> — independently
    /// optional, per the panel's bilingual-fallback rule (either, both, or neither may be set).</summary>
    public string? MaintenanceMessageFa { get; set; }

    /// <summary>When maintenance mode was last turned on; null while off.</summary>
    public DateTimeOffset? MaintenanceSince { get; set; }

    public ICollection<EnvironmentVariable> EnvironmentVariables { get; set; } = new List<EnvironmentVariable>();
    public ICollection<Volume> Volumes { get; set; } = new List<Volume>();
    public ICollection<DomainName> Domains { get; set; } = new List<DomainName>();
    public ICollection<Deployment> Deployments { get; set; } = new List<Deployment>();

    /// <summary>Shared <see cref="ConfigGroup"/>s this app receives environment variables from,
    /// lowest-to-highest precedence order — see <see cref="ConfigGroupMerge"/>.</summary>
    public ICollection<AppConfigGroup> ConfigGroups { get; set; } = new List<AppConfigGroup>();
}
