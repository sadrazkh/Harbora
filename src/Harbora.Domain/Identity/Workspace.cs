using Harbora.Domain.Common;

namespace Harbora.Domain.Identity;

/// <summary>A team/tenant boundary. Apps, services and backups belong to a workspace.</summary>
public class Workspace : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public bool IsDefault { get; set; }

    /// <summary>The account that owns this workspace and may manage its team.</summary>
    public Guid? OwnerUserId { get; set; }
    public User? OwnerUser { get; set; }

    /// <summary>
    /// True for the private workspace every account receives. A user may additionally own or join
    /// any number of shared workspaces, but has at most one personal workspace.
    /// </summary>
    public bool IsPersonal { get; set; }

    /// <summary>Soft lifecycle stop. Archived workspaces can be recovered by their owner.</summary>
    public DateTimeOffset? ArchivedAt { get; set; }
    public Guid? ArchivedByUserId { get; set; }

    /// <summary>Irreversible from the customer panel; retained as a tombstone for safe cleanup.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedByUserId { get; set; }

    /// <summary>Tenancy plan governing this workspace's quotas (null = platform default plan).</summary>
    public Guid? PlanId { get; set; }

    /// <summary>When suspended (e.g. overdue), new deploys are blocked.</summary>
    public bool IsSuspended { get; set; }

    /// <summary>Optional soft monthly budget in minor units; crossing it is visible but does not stop workloads.</summary>
    public long? MonthlyBudgetMinor { get; set; }

    /// <summary>Optional hard monthly spend ceiling in minor units; zero/null disables it.</summary>
    public long? MonthlySpendLimitMinor { get; set; }

    /// <summary>UTC boundary at which a spend-limit suspension may automatically reset.</summary>
    public DateTimeOffset? SpendLimitResetsAt { get; set; }

    /// <summary>The hard limit that caused the current suspension, used to detect an explicit raise.</summary>
    public long? SpendLimitAtSuspensionMinor { get; set; }

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
    public ICollection<WorkspaceInvitation> Invitations { get; set; } = new List<WorkspaceInvitation>();
}

/// <summary>Why a workspace is suspended. Appended, never renumbered.</summary>
public enum SuspensionReason
{
    /// <summary>Not suspended — or suspended by something that did not say why.</summary>
    None = 0,

    /// <summary>An operator suspended it. A payment does not lift this.</summary>
    Manual = 1,

    /// <summary>The balance reached zero. A payment lifts this.</summary>
    NoBalance = 2,

    /// <summary>The workspace reached its own hard monthly spend ceiling.</summary>
    SpendLimit = 3,

    /// <summary>The owner archived the workspace.</summary>
    Archived = 4
}

public class WorkspaceMember : BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public WorkspaceRole Role { get; set; } = WorkspaceRole.Member;

    /// <summary>
    /// When true, this membership only reaches projects explicitly granted in this workspace.
    /// This belongs to the membership, not the account: one person may be an administrator in one
    /// workspace and a contractor limited to staging in another.
    /// </summary>
    public bool ScopedToProjects { get; set; }
}

/// <summary>A single-use invitation to one workspace. Only the token digest is persisted.</summary>
public class WorkspaceInvitation : BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }
    public string Email { get; set; } = string.Empty;
    public WorkspaceRole Role { get; set; } = WorkspaceRole.Member;
    public string TokenHash { get; set; } = string.Empty;
    public string TokenHint { get; set; } = string.Empty;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public Guid? AcceptedByUserId { get; set; }
    public bool IsRevoked { get; set; }
}
