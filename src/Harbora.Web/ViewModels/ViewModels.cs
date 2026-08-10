using System.ComponentModel.DataAnnotations;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;

namespace Harbora.Web.ViewModels;

public sealed class SetupViewModel
{
    [Required(ErrorMessage = "Email is required."), EmailAddress(ErrorMessage = "This is not an email address.")] public string Email { get; set; } = string.Empty;
    [Required(ErrorMessage = "A display name is required.")] public string DisplayName { get; set; } = string.Empty;
    [Required(ErrorMessage = "A password is required."), MinLength(8, ErrorMessage = "The password needs at least 8 characters.")] public string Password { get; set; } = string.Empty;
    [Required(ErrorMessage = "Repeat the password."), Compare(nameof(Password), ErrorMessage = "The two passwords do not match.")] public string ConfirmPassword { get; set; } = string.Empty;
    [Required(ErrorMessage = "A platform name is required.")] public string PlatformName { get; set; } = "Harbora";
    public string RootDomain { get; set; } = "localhost";
    public string AcmeEmail { get; set; } = string.Empty;
    public string Culture { get; set; } = "fa";
}

public sealed class LoginViewModel
{
    [Required(ErrorMessage = "Email is required."), EmailAddress(ErrorMessage = "This is not an email address.")] public string Email { get; set; } = string.Empty;
    [Required(ErrorMessage = "A password is required.")] public string Password { get; set; } = string.Empty;
    public string? ReturnUrl { get; set; }
}

public sealed class RegisterViewModel
{
    [Required(ErrorMessage = "Email is required."), EmailAddress(ErrorMessage = "This is not an email address.")]
    public string Email { get; set; } = string.Empty;
    [Required(ErrorMessage = "A display name is required."), MaxLength(128)]
    public string DisplayName { get; set; } = string.Empty;
    [Required(ErrorMessage = "A password is required."), MinLength(8, ErrorMessage = "The password needs at least 8 characters.")]
    public string Password { get; set; } = string.Empty;
    [Required(ErrorMessage = "Repeat the password."), Compare(nameof(Password), ErrorMessage = "The two passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
    public string? InvitationToken { get; set; }
}

public sealed class TotpViewModel
{
    [Required(ErrorMessage = "A code is required.")] public string Code { get; set; } = string.Empty;
    public string? ReturnUrl { get; set; }
}

public sealed class ResetPasswordViewModel
{
    [Required] public string Token { get; set; } = string.Empty;
    [Required(ErrorMessage = "A password is required."), MinLength(8, ErrorMessage = "The password needs at least 8 characters.")]
    public string Password { get; set; } = string.Empty;
    [Required(ErrorMessage = "Repeat the password."), Compare(nameof(Password), ErrorMessage = "The two passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public sealed class CreateAppViewModel
{
    [Required(ErrorMessage = "A name is required.")] public string Name { get; set; } = string.Empty;
    /// <summary>Optional; auto-derived from the name when left blank.</summary>
    public string? Slug { get; set; }
    public AppSourceType SourceType { get; set; } = AppSourceType.GitRepository;

    public string? CloneUrl { get; set; }
    public string? GitRef { get; set; } = "main";
    public string? GitToken { get; set; }
    public string? DockerfilePath { get; set; } = "Dockerfile";
    public string? ComposeFilePath { get; set; }
    public string? PrebuiltImage { get; set; }
    public int ContainerPort { get; set; } = 80;
    public string? Domain { get; set; }
    public Guid? TemplateId { get; set; }

    /// <summary>
    /// Which project environment to create this in. Null falls back to the workspace's default, so a
    /// link that predates projects — or a CLI that has never heard of them — still works.
    /// </summary>
    public Guid? EnvironmentId { get; set; }

    /// <summary>
    /// What the service is for. Web is the default and is exactly what every app created before this
    /// existed already was.
    /// </summary>
    public ServiceKind Kind { get; set; } = ServiceKind.Web;

    /// <summary>Command run from the new image before traffic switches; a failure keeps the old version.</summary>
    public string? ReleaseCommand { get; set; }

    /// <summary>Five-field cron expression, for a Cron service.</summary>
    public string? CronExpression { get; set; }

    /// <summary>Give every other branch an environment of its own.</summary>
    public bool PreviewsEnabled { get; set; }

    /// <summary>What a scheduled job runs each time it fires.</summary>
    public string? Command { get; set; }
    /// <summary>Target node; defaults to the local server when unset.</summary>
    public Guid? ServerId { get; set; }
    /// <summary>Resource tier; sets the container CPU/memory limits and is quota-checked.</summary>
    public string? InstanceSizeKey { get; set; }
    /// <summary>Build + deploy immediately after creating (go straight to live logs).</summary>
    public bool DeployNow { get; set; } = true;
}

public sealed class CreateServiceViewModel
{
    /// <summary>Which project environment to create the database in; null uses the default.</summary>
    public Guid? EnvironmentId { get; set; }

    [Required(ErrorMessage = "A name is required.")] public string Name { get; set; } = string.Empty;
    public ManagedServiceType Type { get; set; } = ManagedServiceType.PostgreSql;
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// The resource plan. A database used to take whatever it wanted while the application beside
    /// it was capped to the byte and counted against the workspace's quota.
    /// </summary>
    public string? InstanceSizeKey { get; set; }

    /// <summary>
    /// Which machine to put it on, or null to let the scheduler choose the one with the most room.
    /// Databases used to be pinned to the control plane's own host with no way to say otherwise,
    /// so a fleet of nodes filled with applications while every database piled onto the panel.
    /// </summary>
    public Guid? ServerId { get; set; }
}

public sealed class DashboardViewModel
{
    /// <summary>What needs a person's attention, most serious first. Empty is the good case.</summary>
    public IReadOnlyList<Harbora.Infrastructure.Dashboard.AttentionItem> Attention { get; set; } = [];

    /// <summary>The workspace's projects, which are now the way into everything else.</summary>
    public List<ProjectSummary> Projects { get; set; } = [];

    public int AppCount { get; set; }
    public int ProjectCount { get; set; }
    public int DatabaseCount { get; set; }
    public int HealthyDatabaseCount { get; set; }
    public int RunningCount { get; set; }
    public int FailedDeployments { get; set; }
    public int DeploymentsTotal { get; set; }
    /// <summary>Null when nothing has been sampled. Never 0 for "we did not look".</summary>
    public double? CpuPercent { get; set; }
    /// <summary>Null when nothing has been sampled.</summary>
    public long? MemoryUsed { get; set; }

    /// <summary>Installed, enabled templates people can start from — never a hardcoded menu.</summary>
    public List<StarterTemplate> StarterApps { get; set; } = [];
    public List<StarterDatabase> StarterDatabases { get; set; } = [];
    public long MemoryTotal { get; set; }
    public long DiskUsed { get; set; }
    public long DiskTotal { get; set; }
    public int ContainersRunning { get; set; }
    public string DockerVersion { get; set; } = "—";
    public bool DockerAvailable { get; set; }

    /// <summary>
    /// Bytes per second in and out, or null when it could not be worked out — no samples yet, or a
    /// container restarted and the counters are not comparable across it. Never 0 for "unknown".
    /// </summary>
    public double? NetworkInPerSecond { get; set; }
    public double? NetworkOutPerSecond { get; set; }

    // Platform health strip
    public int ServersOnline { get; set; }
    public int ServersTotal { get; set; }
    /// <summary>null = unknown (Docker unreachable / dev machine).</summary>
    public bool? TraefikRunning { get; set; }
    public int DomainsTotal { get; set; }
    public int DomainsSsl { get; set; }

    public List<Deployment> RecentDeployments { get; set; } = new();
    public List<App> Apps { get; set; } = new();
    public List<DashboardError> RecentErrors { get; set; } = new();
    public int BackupsThisMonth { get; set; }
    public int BackupSchedulesEnabled { get; set; }
    public DateTimeOffset? LatestBackupAt { get; set; }
    public List<DashboardDomain> RecentDomains { get; set; } = [];
    public List<DashboardTeamMember> TeamMembers { get; set; } = [];
}

public sealed record DashboardError(string Title, string Detail, DateTimeOffset At, string Link);
public sealed record DashboardDomain(string Host, bool SslEnabled, string AppName, Guid AppId);
public sealed record DashboardTeamMember(string Name, string Email, string Role);

/// <summary>Backs the rollback confirmation screen: what would be restored, or why it can't be.</summary>
public sealed record RollbackViewModel(
    Guid AppId,
    string AppName,
    Guid TargetDeploymentId,
    Harbora.Application.Abstractions.RollbackPlan Plan);

/// <summary>Backs the audit log page: a filtered, paged view of the trail.</summary>
public sealed class AuditPageViewModel
{
    public List<Harbora.Domain.Auditing.AuditLog> Entries { get; set; } = new();
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 100;
    public int TotalCount { get; set; }
    public string? ActionFilter { get; set; }
    public string? ActorFilter { get; set; }
    public List<string> Actions { get; set; } = new();

    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;
}

/// <summary>
/// The public landing page. Marketing copy is static (it describes the product), but plans come
/// from the database so the page reflects what this installation actually offers.
/// </summary>
public sealed class LandingViewModel
{
    public List<Harbora.Domain.Tenancy.Plan> Plans { get; set; } = new();

    public IReadOnlyList<LandingFeature> Features { get; } =
    [
        new("🚀", "Deploy from anywhere",
            "Git repository, Dockerfile, prebuilt image, static site or a one-click template. The stack is auto-detected when there's no Dockerfile."),
        new("🔒", "SSL without the paperwork",
            "Every app gets a domain and a Let's Encrypt certificate, renewed automatically. HTTP redirects to HTTPS by default."),
        new("♻️", "Zero-downtime releases",
            "The new container starts alongside the old one. Traffic switches only after health checks pass, so a failed deploy never takes the site down."),
        new("⏪", "Instant rollback",
            "Rollback re-releases the previous image instead of rebuilding — you get the exact bytes that were working, in seconds."),
        new("🗄", "Databases & backups",
            "Provision PostgreSQL, MySQL, Redis and more in one click. Scheduled backups are encrypted, checksummed and verifiable before you restore."),
        new("📊", "Logs & monitoring",
            "Live build and runtime logs, CPU and memory per app, and alerts to email, Telegram or a webhook when something breaks.")
    ];

    public IReadOnlyList<LandingStep> Steps { get; } =
    [
        new("Connect your source", "Link a Git repository or point at an image. Harbora detects the stack and prepares the build."),
        new("Pick a size and a domain", "Choose CPU and memory, then use the free subdomain you get or bring your own."),
        new("Deploy", "Watch the build stream live. When health checks pass, traffic switches over — and stays on the old version if they don't.")
    ];

    public IReadOnlyList<LandingFaq> Faqs { get; } =
    [
        new("Do I need to know Docker?",
            "No. If your repository has no Dockerfile, the stack is detected and a production image is generated for you. If you do have one, it's used as-is."),
        new("What happens if a deployment fails?",
            "Nothing visible to your users. The new container is removed and the previous version keeps serving traffic — it is never stopped before the replacement is healthy."),
        new("Can I move away later?",
            "Yes. Everything runs as standard Docker containers behind Traefik on your own server, and your images and volumes stay on that machine."),
        new("Is my data isolated from other users?",
            "Each workspace gets its own Docker network, and every query is scoped to the workspace at the database level, not just in the UI."),
        new("How are backups protected?",
            "Archives are encrypted before they leave the server and checksummed. A restore verifies the archive first and refuses to run if it doesn't match.")
    ];
}

public sealed record LandingFeature(string Icon, string Title, string Body);
public sealed record LandingStep(string Title, string Body);
public sealed record LandingFaq(string Question, string Answer);

/// <summary>One tile on the dashboard's starter rows.</summary>
public sealed record StarterTemplate(Guid Id, string Key, string Name, string? NameFa, string Category, string? IconUrl);

/// <summary>One database engine tile — from the catalog, so it can actually be provisioned.</summary>
public sealed record StarterDatabase(string Type, string Name, string? NameFa);
