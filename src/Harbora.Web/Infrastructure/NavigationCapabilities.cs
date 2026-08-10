using System.Security.Claims;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// Bridges the signed-in principal to the navigation map's filter.
///
/// Deliberately built on the same <see cref="RolePermissions"/> matrix that
/// <c>CapabilityAuthorizationHandler</c> evaluates, rather than a second list kept in step by hand.
/// Two sources of truth here means a sidebar that offers a section the request pipeline then
/// refuses — or, worse, hides one the user is entitled to and nobody can explain why.
/// </summary>
public static class NavigationCapabilities
{
    public static Func<string, bool> For(ClaimsPrincipal user)
    {
        var roleValue = user.FindFirstValue(ClaimTypes.Role);
        var workspaceRoleValue = user.FindFirstValue(HarboraClaims.WorkspaceRole);

        // An unparseable or absent role grants nothing. Deny by default: the alternative is a
        // half-signed-in request seeing the platform administration menu.
        if (!Enum.TryParse<SystemRole>(roleValue, ignoreCase: true, out var role))
            return _ => false;

        var hasWorkspaceRole = Enum.TryParse<WorkspaceRole>(
            workspaceRoleValue, ignoreCase: true, out var workspaceRole);
        return capability => role switch
        {
            SystemRole.Owner or SystemRole.Admin => RolePermissions.Allows(role, capability),
            SystemRole.Member when hasWorkspaceRole => WorkspaceRolePermissions.Allows(workspaceRole, capability),
            _ => RolePermissions.Allows(role, capability)
        };
    }
}
