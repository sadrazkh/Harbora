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

    /// <summary>
    /// Why this workspace is suspended. Without it a top-up would quietly lift an operator's
    /// deliberate suspension, which is the sort of thing nobody notices until it matters.
    ///
    /// <para>
    /// <see cref="SuspensionReason.None"/> on a suspended workspace is not a gap to be filled in
    /// later: every workspace suspended before this column existed reads as None, so the code that
    /// lifts a suspension asks whether the reason IS <see cref="SuspensionReason.NoBalance"/> rather
    /// than whether it is not <see cref="SuspensionReason.Manual"/>. Those two conditions differ on
    /// exactly the rows nobody thought about.
    /// </para>
    /// </summary>
    public SuspensionReason SuspendedReason { get; set; } = SuspensionReason.None;

    public ICollection<WorkspaceMember> Members { get; set; } = new List<WorkspaceMember>();
}

/// <summary>Why a workspace is suspended. Appended, never renumbered.</summary>
public enum SuspensionReason
{
    /// <summary>Not suspended — or suspended by something that did not say why.</summary>
    None = 0,

    /// <summary>An operator suspended it. A payment does not lift this.</summary>
    Manual = 1,

    /// <summary>The balance reached zero. A payment lifts this.</summary>
    NoBalance = 2
}

public class WorkspaceMember : BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public WorkspaceRole Role { get; set; } = WorkspaceRole.Member;
}
