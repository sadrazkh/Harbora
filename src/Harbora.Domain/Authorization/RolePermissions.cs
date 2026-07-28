using Harbora.Domain.Common;

namespace Harbora.Domain.Authorization;

/// <summary>
/// The single source of truth for which <see cref="SystemRole"/> may perform which
/// <see cref="Capabilities"/> (deny-by-default). Pure + exhaustively unit-tested so the access
/// matrix can't drift. Owner/Admin get everything; the rest are least-privilege.
/// </summary>
public static class RolePermissions
{
    // Developer-style role (existing enum value "Member"): full app lifecycle + related resources,
    // but no platform/tenant/server/backup administration.
    private static readonly HashSet<string> MemberCaps = new(StringComparer.Ordinal)
    {
        Capabilities.AppsCreate, Capabilities.AppsDeploy, Capabilities.AppsOperate,
        Capabilities.AppsDelete, Capabilities.AppsEnv,
        Capabilities.DatabasesManage, Capabilities.RoutesManage, Capabilities.GitManage
    };

    // Ops role: day-2 operations only.
    private static readonly HashSet<string> OperatorCaps = new(StringComparer.Ordinal)
    {
        Capabilities.AppsOperate, Capabilities.BackupsRun
    };

    public static bool Allows(SystemRole role, string capability) => role switch
    {
        SystemRole.Owner => true,
        SystemRole.Admin => true,
        SystemRole.Member => MemberCaps.Contains(capability),
        SystemRole.Operator => OperatorCaps.Contains(capability),
        SystemRole.Viewer => false,
        _ => false
    };

    /// <summary>All capabilities granted to a role (handy for building UI/menus and for tests).</summary>
    public static IEnumerable<string> CapabilitiesFor(SystemRole role) =>
        Capabilities.All.Where(c => Allows(role, c));
}
