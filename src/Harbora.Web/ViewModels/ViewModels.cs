using System.ComponentModel.DataAnnotations;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;

namespace Harbora.Web.ViewModels;

public sealed class SetupViewModel
{
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;
    [Required] public string DisplayName { get; set; } = string.Empty;
    [Required, MinLength(8)] public string Password { get; set; } = string.Empty;
    [Required, Compare(nameof(Password))] public string ConfirmPassword { get; set; } = string.Empty;
    [Required] public string PlatformName { get; set; } = "Harbora";
    public string RootDomain { get; set; } = "localhost";
    public string AcmeEmail { get; set; } = string.Empty;
    public string Culture { get; set; } = "fa";
}

public sealed class LoginViewModel
{
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;
    [Required] public string Password { get; set; } = string.Empty;
    public string? ReturnUrl { get; set; }
}

public sealed class CreateAppViewModel
{
    [Required, RegularExpression("^[a-z0-9-]{2,50}$", ErrorMessage = "Use 2–50 lowercase letters, digits or hyphens.")]
    public string Slug { get; set; } = string.Empty;
    [Required] public string Name { get; set; } = string.Empty;
    public AppSourceType SourceType { get; set; } = AppSourceType.GitRepository;

    public string? CloneUrl { get; set; }
    public string? GitRef { get; set; } = "main";
    public string? GitToken { get; set; }
    public string? DockerfilePath { get; set; } = "Dockerfile";
    public string? PrebuiltImage { get; set; }
    public int ContainerPort { get; set; } = 80;
    public string? Domain { get; set; }
    public Guid? TemplateId { get; set; }
}

public sealed class CreateServiceViewModel
{
    [Required] public string Name { get; set; } = string.Empty;
    public ManagedServiceType Type { get; set; } = ManagedServiceType.PostgreSql;
    public string Version { get; set; } = string.Empty;
}

public sealed class DashboardViewModel
{
    public int AppCount { get; set; }
    public int RunningCount { get; set; }
    public int FailedDeployments { get; set; }
    public double CpuPercent { get; set; }
    public long MemoryUsed { get; set; }
    public long MemoryTotal { get; set; }
    public long DiskUsed { get; set; }
    public long DiskTotal { get; set; }
    public string DockerVersion { get; set; } = "—";
    public bool DockerAvailable { get; set; }
    public List<Deployment> RecentDeployments { get; set; } = new();
    public List<App> Apps { get; set; } = new();
}
