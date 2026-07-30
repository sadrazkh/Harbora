using Harbora.Domain.Apps;
using Harbora.Domain.Services;
using Environment = Harbora.Domain.Projects.Environment;
using Project = Harbora.Domain.Projects.Project;

namespace Harbora.Web.ViewModels;

/// <summary>One row on the projects list: enough to answer "is anything wrong here?" without opening it.</summary>
public sealed class ProjectSummary
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public int Environments { get; set; }
    public int Services { get; set; }
    public int Databases { get; set; }

    /// <summary>Services that are crashed or whose last deploy failed — the only count worth a colour.</summary>
    public int Unhealthy { get; set; }
}

public sealed class ProjectDetailsViewModel
{
    public Project Project { get; set; } = null!;
    public List<Environment> Environments { get; set; } = [];

    /// <summary>The environment being viewed; null only if a project somehow has none.</summary>
    public Environment? Selected { get; set; }

    public List<App> Services { get; set; } = [];
    public List<ManagedService> Databases { get; set; } = [];
}
