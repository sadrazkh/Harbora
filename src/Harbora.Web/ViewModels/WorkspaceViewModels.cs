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
