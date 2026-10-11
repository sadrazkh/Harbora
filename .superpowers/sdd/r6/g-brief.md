# G — Project and database tools (wave B)

Read `wave-b-common.md` first: it is binding for every tool here.

## Tools

| Tool | Kind | Calls | Notes |
|---|---|---|---|
| `create_project` | SafeWrite | `ProjectService.CreateAsync` (validate: name/slug rules and `IQuotaService.CanAddGovernedResourcesAsync(Projects:1, Environments:1)` without writing) | Capability AppsCreate, checked like `ProjectsController.Create` (`access.AllowsAsync(new(null, null), AppsCreate)`). Says a Production environment is created with it. `Creates` = project (and its default environment). |
| `add_environment` | SafeWrite | `ProjectService.AddEnvironmentAsync` | Project by name (may be planned); slug unique per project; quota Environments:1; access as the controller checks it. `Creates` = environment. |
| `create_database` | SafeWrite | `IDatabaseProvisioningService.ValidateCreateAsync` / `CreateAsync` | Engine and version from the effective catalog (`ServiceCatalogReader`), name, target environment by project + environment name (may be planned), instance size, Redis memory policy when engine is redis. **Resolve the environment yourself and compare the created database's environment with the one asked for** (the service silently falls back to the default — see the common rules). Never return the password or connection string. `Creates` = database. |
| `attach_database` | SafeWrite | `IDatabaseAttachmentService.ValidateAttachAsync` / `AttachAsync` | Database and app by name (either may be planned); optional alias / logical database. Warning: takes effect on the app's next deploy. |
| `detach_database` | DangerousWrite | `ValidateDetachAsync` / `DetachAsync` | The app loses its connection settings on the next deploy. |
| `rotate_database_password` | DangerousWrite | `LogicalDatabaseService.RotatePasswordAsync` | Lists the apps that must be redeployed to pick up the new password; never returns the password. Capability DatabasesManage + the service access check. |
| `stop_database` | DangerousWrite | `IManagedServiceEngine.StopAsync` | Apps using it lose their database. |
| `start_database` | SafeWrite | `IManagedServiceEngine.StartAsync` | Billing gate refusal mapped bilingually. |
| `delete_database` | Destructive | `IDatabaseProvisioningService.ValidateRemoveAsync` / `RemoveAsync(serviceId, deleteData)` | Phrase = the database's exact name; `deleteData` argument defaults **false** and the card says plainly whether the data volume is deleted; structural refusals (attached apps, replicas) come from the service. |
| `delete_project` | Destructive | `ProjectDeletionService.PlanAsync` / `DeleteAsync` | Phrase = the project's exact name (`plan.IsConfirmed` must be satisfied with it); detail lists every app and database that goes with it. |

Capabilities: verify each against the matching `ProjectsController`/`DatabasesController` action policy
(`AppsCreate` for projects/environments, `DatabasesManage` for databases, `AppsEnv` on the app side of
attach/detach as the attachment service requires). Do not guess.

## Scenario tests through the agent loop

1. "create project shop, a Postgres 16 database shop-db in its production environment, and attach it to
   app web" (web exists) → one plan, three safe steps validated against the planned project and database
   → approve → all executed, in order, with links; a forced failure of step 2 skips step 3 and the result
   says so.
2. `create_database` remembered → the next turn with only `create_database` executes without a card.
3. `delete_database` with `deleteData` true → card with phrase and the data-loss warning; wrong text is
   refused and nothing is removed; right text removes it. A database with attached apps is refused at
   validate with the service's reason.
4. `stop_database` is carded even when every safe tool is remembered.

## Do not touch

The phase-0 services' behaviour (fix a real bug only if a tool exposes one, and say so), the agent core's
loop/plan engine (report problems instead), app tools (agent F, in parallel), the pane.
