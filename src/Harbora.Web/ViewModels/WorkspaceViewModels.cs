using Harbora.Domain.Common;

namespace Harbora.Web.ViewModels;

public sealed record WorkspaceSummaryRow(
    Guid Id, string Name, string Slug, bool IsPersonal, bool IsCurrent,
    WorkspaceRole Role, long? BalanceMinor, string Currency);

public sealed record WorkspaceMemberRow(
    Guid UserId, string Email, string DisplayName, WorkspaceRole Role, bool IsOwner, bool IsCurrentUser);

public sealed record WorkspaceInvitationRow(
    Guid Id, string Email, WorkspaceRole Role, string TokenHint, DateTimeOffset ExpiresAt);

public sealed class WorkspaceHubViewModel
{
    public Guid CurrentWorkspaceId { get; init; }
    public string CurrentWorkspaceName { get; init; } = string.Empty;
    public bool CurrentIsPersonal { get; init; }
    public bool CanManageCurrent { get; init; }
    public IReadOnlyList<WorkspaceSummaryRow> Workspaces { get; init; } = [];
    public IReadOnlyList<WorkspaceMemberRow> Members { get; init; } = [];
    public IReadOnlyList<WorkspaceInvitationRow> Invitations { get; init; } = [];
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
