namespace Harbora.Domain.Authorization;

/// <summary>
/// Named, action-level capabilities used as authorization policy names (ADR/RBAC, doc 10 §2.12).
/// Controllers/API guard privileged actions with <c>[Authorize(Policy = Capabilities.Xxx)]</c>.
/// Read/list actions are covered by the base authenticated policy and are intentionally not listed.
/// </summary>
public static class Capabilities
{
    public const string AppsCreate    = "apps.create";     // create a new app
    public const string AppsDeploy    = "apps.deploy";     // deploy + rollback
    public const string AppsOperate   = "apps.operate";    // restart / stop / start
    public const string AppsDelete    = "apps.delete";     // delete an app
    public const string AppsEnv       = "apps.env";        // edit env/secrets + domains
    public const string DatabasesManage = "databases.manage";
    public const string RoutesManage  = "routes.manage";
    public const string GitManage     = "git.manage";
    public const string AlertsManage  = "alerts.manage";
    public const string BackupsRun    = "backups.run";     // run a backup
    public const string BackupsRestore = "backups.restore"; // destructive restore
    public const string BackupsManage = "backups.manage";  // destinations + schedules
    public const string ServersManage = "servers.manage";
    public const string PlatformManage = "platform.manage"; // platform settings
    public const string TenantsManage = "tenants.manage";
    public const string PlansManage   = "plans.manage";

    /// <summary>Every capability — used to register one policy per capability.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        AppsCreate, AppsDeploy, AppsOperate, AppsDelete, AppsEnv,
        DatabasesManage, RoutesManage, GitManage, AlertsManage,
        BackupsRun, BackupsRestore, BackupsManage,
        ServersManage, PlatformManage, TenantsManage, PlansManage
    ];
}
