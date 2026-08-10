using Harbora.Domain.Common;

namespace Harbora.Domain.Authorization;

/// <summary>
/// Workspace roles grant tenant-scoped powers only. They can never grant platform, server, plan or
/// cross-tenant administration; those remain properties of the user's system role.
/// </summary>
public static class WorkspaceRolePermissions
{
    private static readonly HashSet<string> Member = new(StringComparer.Ordinal)
    {
        Capabilities.AppsCreate, Capabilities.AppsDeploy, Capabilities.AppsOperate,
        Capabilities.AppsDelete, Capabilities.AppsEnv, Capabilities.DatabasesManage,
        Capabilities.RoutesManage, Capabilities.GitManage
    };

    private static readonly HashSet<string> Admin = new(Member, StringComparer.Ordinal)
    {
        Capabilities.AlertsManage, Capabilities.BackupsRun,
        Capabilities.BackupsRestore, Capabilities.BackupsManage
    };

    private static readonly HashSet<string> Operator = new(StringComparer.Ordinal)
    {
        Capabilities.AppsOperate, Capabilities.BackupsRun
    };

    public static bool Allows(WorkspaceRole role, string capability) => role switch
    {
        WorkspaceRole.Admin => Admin.Contains(capability),
        WorkspaceRole.Member => Member.Contains(capability),
        WorkspaceRole.Operator => Operator.Contains(capability),
        WorkspaceRole.Viewer => false,
        _ => false
    };
}
