# F — App tools (wave B)

Read `wave-b-common.md` first: it is binding for every tool here.

## Tools

| Tool | Kind | Calls | Notes |
|---|---|---|---|
| `create_app` | SafeWrite | `IAppProvisioningService.ValidateCreateAsync` / `CreateAsync` | Sources: Git (clone URL + ref, optional root directory / Dockerfile path) and prebuilt image; port; target project/environment by name; instance size from the catalog; `deployNow` default true. Never accept a Git token from the model — private repos are out of scope for v1 (say so in the description). `Creates` = app. |
| `deploy_template` | SafeWrite | `TemplateDeploymentService.DeployAsync` | Template by key/name from the visible catalog; required variables as arguments. It always creates a new project — say so on the card. Capability: as `TemplatesController.CanDeployAsync` (AppsCreate, plus DatabasesManage when the template creates databases). |
| `set_env_vars` | SafeWrite | `IAppConfigurationService.ValidateSetEnvAsync` / `SetEnvAsync` | One or more key/value pairs, `secret` flag, `availableAtBuild`. Secret values must never appear in card text, `ResultJson` or summaries — keys only. Warning: takes effect on the next deploy. |
| `delete_env_var` | DangerousWrite | `ValidateDeleteEnvAsync` / `DeleteEnvAsync` | Removing config can break the app on its next deploy. |
| `add_domain` | SafeWrite | `ValidateAddDomainAsync` / `AddDomainAsync` | Host + ssl. |
| `remove_domain` | DangerousWrite | `ValidateRemoveDomainAsync` / `RemoveDomainAsync` | Say which host becomes primary. |
| `add_volume` | SafeWrite | `ValidateAddVolumeAsync` / `AddVolumeAsync` | Mount path. |
| `remove_volume` | Destructive | `ValidateRemoveVolumeAsync` / `RemoveVolumeAsync` | Phrase = the app's slug; detail says the data on that volume is deleted when requested; honour volume protection. |
| `deploy_app` | SafeWrite | `IDeploymentEngine.QueueDeploymentAsync` | `DeploymentTrigger.Assistant`; optional git ref; returns the deployment link; warning: replaces running containers; a protected environment queues for approval (say so when it does). Preflight refusals (quota, capacity, low disk) mapped bilingually. |
| `restart_app` | SafeWrite | `IAppOperationsService.RestartAsync` | Capability AppsOperate. |
| `stop_app` | DangerousWrite | `IAppOperationsService.StopAsync` | The app stops serving. |
| `rollback_app` | DangerousWrite | `IRollbackPlanner.PrepareAsync` + `QueueDeploymentAsync(RollbackToDeploymentId)` | Target: the previous successful deployment by default, or a deployment number; refuse when `CanRollback` is false with its reason. |
| `delete_app` | Destructive | `IAppOperationsService.DeleteAsync(appId, removeVolumes)` | Phrase = the app's slug; `removeVolumes` argument defaults false; detail lists domains, volumes and attached databases (which stay); `VolumeProtectedException` mapped bilingually. Capability AppsDelete. |

Resizing and replicas are **not** in v1 (their logic is still inline in `AppsController`); do not add them.

Capabilities: use the policy the matching `AppsController`/`DeploymentsController` action carries
(`AppsCreate`, `AppsEnv` for env and domains, the one volume actions use, `AppsDeploy` for deploy/rollback,
`AppsOperate` for restart/stop, `AppsDelete`). Verify each in the controller; do not guess.

## Scenario tests through the agent loop

1. "create an app from image nginx:alpine called web, port 80, and add domain web.example.com" → one plan,
   two safe steps (the domain step validates against the planned app) → approve → both executed, app and
   domain exist, links returned.
2. `set_env_vars` remembered ("don't ask again") on a first approve → the next turn with only
   `set_env_vars` executes without a card (`executed` event); a secret value appears in no model request
   and no summary.
3. `delete_app` → card with phrase; approving with the wrong text is refused and nothing is deleted; the
   right text deletes.
4. `rollback_app` is carded even when the user has remembered every safe tool.

## Do not touch

The phase-0 services' behaviour (fix a real bug only if a tool exposes one, and say so), the agent core's
loop/plan engine (report problems instead), project/database tools (agent G, in parallel), the pane.
