using Harbora.Domain.Common;
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
    public Guid ServerId { get; set; }

    public string Name { get; set; } = string.Empty;
    /// <summary>URL/DNS-safe unique slug; also the docker network/label namespace.</summary>
    public string Slug { get; set; } = string.Empty;

    public AppSourceType SourceType { get; set; }
    public AppStatus Status { get; set; } = AppStatus.Created;

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
    public int? DesiredReplicas { get; set; } = 1;
    public string? HealthCheckPath { get; set; } = "/";
    public long MemoryLimitBytes { get; set; }
    public double CpuLimit { get; set; }

    /// <summary>Id of the deployment currently serving traffic (for rollback comparisons).</summary>
    public Guid? ActiveDeploymentId { get; set; }

    public Guid? TemplateId { get; set; }

    public ICollection<EnvironmentVariable> EnvironmentVariables { get; set; } = new List<EnvironmentVariable>();
    public ICollection<Volume> Volumes { get; set; } = new List<Volume>();
    public ICollection<DomainName> Domains { get; set; } = new List<DomainName>();
    public ICollection<Deployment> Deployments { get; set; } = new List<Deployment>();
}
