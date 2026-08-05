using Harbora.Domain.Common;
using Harbora.Domain.Identity;

namespace Harbora.Infrastructure.Security;

/// <summary>Which workspace somebody belongs to, or why that cannot be decided.</summary>
/// <param name="WorkspaceId">Null when no single answer exists.</param>
/// <param name="Reason">Set only when it could not be decided, in words somebody can act on.</param>
public sealed record MembershipResolution(Guid? WorkspaceId, string? Reason)
{
    public bool Resolved => WorkspaceId is not null;
}

/// <summary>
/// Which workspace a person is in.
///
/// This exists because of a failure that made the whole non-admin side of the panel look broken.
/// Creating a user wrote a <c>User</c> row and no <c>WorkspaceMember</c>. At sign-in the lookup was
/// <c>FirstOrDefaultAsync()</c> over an empty set, which is <see cref="Guid.Empty"/> — not null, not
/// an error, a perfectly ordinary-looking id. That went into the workspace claim, every query
/// filtered on it, and the person got an empty dashboard, an empty app list and an empty database
/// list. Anything they then created was stamped with <c>Guid.Empty</c> and belonged to nobody.
///
/// Nothing failed. Every page returned 200.
/// </summary>
public static class WorkspaceMembership
{
    /// <summary>
    /// The workspace to sign somebody into.
    /// </summary>
    /// <param name="memberships">Workspaces this user is already a member of.</param>
    /// <param name="allWorkspaces">Every workspace on the installation.</param>
    public static MembershipResolution Resolve(
        IReadOnlyCollection<Guid> memberships, IReadOnlyCollection<Guid> allWorkspaces)
    {
        // The ordinary case, and the one that must never be confused with the others.
        if (memberships.Count > 0) return new MembershipResolution(memberships.First(), null);

        // No membership. On a single-workspace installation — which is every Harbora install today —
        // there is exactly one answer, so joining them to it is not a guess. It also repairs every
        // account created before this was fixed, on their next sign-in, without an administrator
        // having to find them first.
        if (allWorkspaces.Count == 1) return new MembershipResolution(allWorkspaces.First(), null);

        // More than one, and no membership: there is no right answer, and picking the first would
        // put somebody inside another tenant's data. Refused with something an administrator can act
        // on rather than signed in to nothing.
        if (allWorkspaces.Count > 1)
            return new MembershipResolution(null,
                "This account is not a member of any workspace. An administrator needs to add it to one.");

        // No workspaces at all means the installation has not finished its first-run setup.
        return new MembershipResolution(null,
            "This installation has no workspace yet. Complete the setup wizard first.");
    }

    /// <summary>
    /// The membership row to write when somebody is given access to a workspace.
    ///
    /// The workspace role mirrors the system role rather than defaulting to Member: an owner who
    /// appears as an ordinary member of their own workspace is a permission bug waiting to be
    /// reported as one.
    /// </summary>
    public static WorkspaceMember For(Guid workspaceId, Guid userId, SystemRole role) => new()
    {
        WorkspaceId = workspaceId,
        UserId = userId,
        // WorkspaceRole has no Owner: an owner is an admin of the workspace they own.
        Role = role switch
        {
            SystemRole.Owner or SystemRole.Admin => WorkspaceRole.Admin,
            SystemRole.Viewer => WorkspaceRole.Viewer,
            _ => WorkspaceRole.Member
        }
    };
}
