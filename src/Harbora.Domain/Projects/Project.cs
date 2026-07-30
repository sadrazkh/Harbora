using Harbora.Domain.Common;

namespace Harbora.Domain.Projects;

/// <summary>
/// A product: the services, databases and domains that belong together.
///
/// Added because <c>App</c> had been doing four jobs — a deployable unit, a project, an environment
/// and a routing target — which is why staging-versus-production, a worker beside its API, and an
/// architecture view could not be expressed at all.
/// </summary>
public class Project : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>URL-safe identifier, unique within the workspace.</summary>
    public string Slug { get; set; } = string.Empty;

    public string? Description { get; set; }

    public ICollection<Environment> Environments { get; set; } = new List<Environment>();
}

/// <summary>
/// One independent copy of a project: production, staging, a branch preview.
///
/// Environments own the things that must differ between copies — variables, domains, databases and
/// the private network — so promoting a build never means editing production by hand.
/// </summary>
public class Environment : BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Used in the network name and in internal hostnames, so it must stay DNS-safe.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// The environment a project deploys to unless told otherwise. Exactly one per project; every
    /// project gets one at creation so nothing ever has to handle "no environment yet".
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Blocks deploys and destructive actions without an explicit override. Off by default: turning
    /// it on for a customer's production is their decision, not ours.
    /// </summary>
    public bool IsProtected { get; set; }
}
