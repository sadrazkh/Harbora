using Harbora.Domain.Common;

namespace Harbora.Web.ViewModels;

public sealed record WorkspaceSummaryRow(
    Guid Id, string Name, string Slug, bool IsPersonal, bool IsCurrent,
    WorkspaceRole Role, long? BalanceMinor, string Currency,
    DateTimeOffset? ArchivedAt, bool IsOwner);

public sealed record WorkspaceMemberRow(
    Guid UserId, string Email, string DisplayName, WorkspaceRole Role, bool IsOwner, bool IsCurrentUser,
    bool ScopedToProjects);

public sealed record WorkspaceInvitationRow(
    Guid Id, string Email, WorkspaceRole Role, string TokenHint, DateTimeOffset ExpiresAt);

public sealed record WorkspaceEnvironmentOption(Guid Id, string Name);
public sealed record WorkspaceProjectOption(
    Guid Id, string Name, IReadOnlyList<WorkspaceEnvironmentOption> Environments);
public sealed record WorkspaceProjectGrantRow(
    Guid Id, Guid UserId, Guid ProjectId, Guid? EnvironmentId, SystemRole Role, string Description);

public sealed class WorkspaceHubViewModel
{
    public Guid CurrentWorkspaceId { get; init; }
    public string CurrentWorkspaceName { get; init; } = string.Empty;
    public bool CurrentIsPersonal { get; init; }
    public bool CanManageCurrent { get; init; }
    public IReadOnlyList<WorkspaceSummaryRow> Workspaces { get; init; } = [];
    public IReadOnlyList<WorkspaceMemberRow> Members { get; init; } = [];
    public IReadOnlyList<WorkspaceInvitationRow> Invitations { get; init; } = [];
    public IReadOnlyList<WorkspaceProjectOption> Projects { get; init; } = [];
    public IReadOnlyList<WorkspaceProjectGrantRow> Grants { get; init; } = [];
}

/// <summary>
/// The customer's record of every time somebody from the platform was signed in as one of them.
/// </summary>
public sealed class WorkspaceSupportAccessViewModel
{
    public Guid WorkspaceId { get; init; }
    public IReadOnlyList<WorkspaceSupportSessionRow> Sessions { get; init; } = [];
}

/// <param name="EndedBy">
/// Null while the session is live. "Ended by hand" and "ran out" are kept apart because they read
/// differently to somebody checking whether support let themselves out.
/// </param>
public sealed record WorkspaceSupportSessionRow(
    Guid Id,
    string AdminEmail,
    string Reason,
    DateTimeOffset StartedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? EndedAt,
    Harbora.Domain.Identity.SupportSessionEnding? EndedBy,
    bool IsLive)
{
    /// <summary>What was done while it ran. Empty is a real answer: they looked and left.</summary>
    public IReadOnlyList<WorkspaceSupportActRow> Acts { get; init; } = [];
}

public sealed record WorkspaceSupportActRow(
    DateTimeOffset At, string Action, string? TargetType, string? TargetId);

/// <summary>
/// The workspace-scoped half of HARBORA-0056: what a workspace operator sees in the audit log, as
/// opposed to the platform-wide reader at <c>AuditController</c>.
/// </summary>
public sealed class WorkspaceAuditLogViewModel
{
    public Guid WorkspaceId { get; init; }
    public IReadOnlyList<WorkspaceAuditLogRow> Entries { get; init; } = [];
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
    public int TotalCount { get; init; }

    /// <summary>The export's row cap, shown so the page can warn before the click rather than after.</summary>
    public int ExportMaxRows { get; init; }

    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;

    /// <summary>
    /// Whether exporting today would truncate. Compared against <see cref="TotalCount"/> — the whole
    /// workspace's row count, not this page's — because a workspace can be past the export bound while
    /// the reader is looking at page one.
    /// </summary>
    public bool ExportWouldTruncate => TotalCount > ExportMaxRows;
}

public sealed record WorkspaceAuditLogRow(
    Guid Id, DateTimeOffset At, string ActorEmail, string Action,
    string? TargetType, string? TargetId, string? IpAddress);

public sealed class AcceptWorkspaceInvitationViewModel
{
    public string Token { get; init; } = string.Empty;
    public string WorkspaceName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public WorkspaceRole Role { get; init; }
    public bool IsAuthenticated { get; init; }
    public bool EmailMatches { get; init; }
    public string? Error { get; init; }
}
