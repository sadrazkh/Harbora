using Harbora.Domain.Common;

namespace Harbora.Domain.Identity;

/// <summary>A team/tenant boundary. Apps, services and backups belong to a workspace.</summary>
public class Workspace : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public bool IsDefault { get; set; }

    /// <summary>Tenancy plan governing this workspace's quotas (null = platform default plan).</summary>
    public Guid? PlanId { get; set; }

    /// <summary>When suspended (e.g. overdue), new deploys are blocked.</summary>
    public bool IsSuspended { get; set; }

    public ICollection<WorkspaceMember> Members { get; set; } = new List<WorkspaceMember>();
}

public class WorkspaceMember : BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public WorkspaceRole Role { get; set; } = WorkspaceRole.Member;
}
