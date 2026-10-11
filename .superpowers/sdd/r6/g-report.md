# G — Project and database write tools (wave B) — report

Branch `r6/db-tools`, worktree `../Harbora-r6-g`, on top of `d41247d` (the wave-B base). Nothing outside my own
files was edited except one contiguous block in `DependencyInjection.cs`. No phase-0 service, agent-core or
pane file was touched. No real AI provider was called (the model is `ScriptedAgentChatClient` everywhere).

**Status: DONE_WITH_CONCERNS** — all ten tools and all four scenarios are built and tested; the concerns are two
brief items that did not match the code (scenario 1 and the rotate service name, section 4), a few behaviours I
chose where the brief was silent, and what this machine cannot verify (section 8).

## Commits (owner identity, no trailer)

```
8f8f2a8 Assistant write tools for projects, environments and databases: create, attach, detach, rotate, stop, start and delete, each over the service the matching panel action calls, with planned-resource validation so one plan can create a project, a database in it and wire them together.
ffe7198 Database tools: stop only a running database, never start one still being provisioned, and a rotation that fails after storing the new password says the password changed; plus tests that every tool's validation leaves nothing behind and refuses a viewer or an ungranted member.
e0bc607 start_database no longer asks the billing gate while the card is drawn: the gate is asked only where a container is about to run, which the billing-gate census enforces, so a refusal for money arrives from the engine when the step runs and is told in both languages.
```

plus the commit adding this report.

## 1. Files

`src/Harbora.Infrastructure/Agent/Tools/`: `CreateProjectTool.cs`, `AddEnvironmentTool.cs`, `CreateDatabaseTool.cs`,
`AttachDatabaseTool.cs`, `DetachDatabaseTool.cs`, `RotateDatabasePasswordTool.cs`, `StopDatabaseTool.cs`,
`StartDatabaseTool.cs`, `DeleteDatabaseTool.cs`, `DeleteProjectTool.cs`, and `ProjectDatabaseToolSupport.cs`
(internal: links, planned-resource lookups, target-environment resolution, catalogue lookups, the
resolve-and-guard helper, the engine-call deadline). All derive from `AgentWriteTool`.

DI: one block in `src/Harbora.Infrastructure/DependencyInjection.cs`, commented "Round six, wave B, agent G",
**right after the phase-0b database services** (`IDatabaseAttachmentService`), not beside the agent core's read
tools — beside the services they wrap, and away from where agent F is most likely to put its block, so the two
branches do not insert at the same line. Ten `AddScoped<Agent.IAgentTool, …>()` lines.

Tests: `tests/Harbora.Tests/Agent/DatabaseToolRig.cs` (kit), `ProjectToolsTests.cs`, `DatabaseToolsTests.cs`,
`ProjectDatabaseToolScenarioTests.cs`, `tests/Harbora.Tests/Http/ProjectDatabaseToolsHttpTests.cs`.

## 2. The tools

Capabilities were read off the controller actions, not taken from the brief's summary line. Every tool resolves
names only through `AgentResourceResolver` and, where the service does not, checks the capability on the resource
in both `ValidateAsync` and `ExecuteAsync`. No tool returns a password, connection string or ciphertext; the
services' `ipAddress` is passed as null (the tool context carries none — the plan engine's
`assistant.step_executed` row has the IP).

| Tool | Kind | Capability (verified against) | Arguments | Calls |
|---|---|---|---|---|
| `create_project` | SafeWrite | `AppsCreate` (`ProjectsController.Create`) + `AllowsAsync(new(null,null), AppsCreate)` | `name`, `slug?` | validate: slug rules + `IQuotaService.CanAddGovernedResourcesAsync(Projects:1, Environments:1)`; execute: `ProjectService.CreateAsync` |
| `add_environment` | SafeWrite | `AppsCreate` (`ProjectsController.AddEnvironment`) + `AllowsAsync(new(projectId,null), AppsCreate)` | `project`, `name` | validate: quota `Environments:1`; execute: `ProjectService.AddEnvironmentAsync` |
| `create_database` | SafeWrite | `DatabasesManage` (`DatabasesController.Create` POST); placement by the service | `name`, `engine`, `version?`, `project`, `environment?`, `size?`, `redisEvictionPolicy?`, `redisMaxMemoryMb?` | `IDatabaseProvisioningService.ValidateCreateAsync` / `CreateAsync` |
| `attach_database` | SafeWrite | `DatabasesManage` (`DatabasesController.Attach`); `AppsEnv` on the app by the service | `database`, `app`, `project?`, `environment?`, `alias?`, `logicalDatabase?` | `IDatabaseAttachmentService.ValidateAttachAsync` / `AttachAsync` |
| `detach_database` | DangerousWrite | `DatabasesManage` (`Detach`); `AppsEnv` by the service | `database`, `app`, `project?`, `environment?`, `logicalDatabase?` | `ValidateDetachAsync` / `DetachAsync` |
| `rotate_database_password` | DangerousWrite | `DatabasesManage` (`Rotate`) + `CanTouchServiceAsync` | `database`, `project?`, `environment?` | `IManagedServiceEngine.RotatePasswordAsync` |
| `stop_database` | DangerousWrite | `DatabasesManage` (`Stop`) + `CanTouchServiceAsync` | same | `IManagedServiceEngine.StopAsync` |
| `start_database` | SafeWrite | `DatabasesManage` (`Start`) + `CanTouchServiceAsync` | same | `IManagedServiceEngine.StartAsync` |
| `delete_database` | Destructive | `DatabasesManage` (`Remove`); access by the service | `database`, `project?`, `environment?`, `deleteData?` (default **false**) | `ValidateRemoveAsync` / `RemoveAsync(serviceId, deleteData)` |
| `delete_project` | Destructive | **`AppsDelete`** (`ProjectsController.Delete`) + `AllowsAsync(new(projectId,null), AppsDelete)` | `project` | `ProjectDeletionService.PlanAsync` / `DeleteAsync` |

Card titles (EN / FA): "Create project {name}" / «ساخت پروژهٔ {name}»; "Add environment {slug} to {project}" /
«افزودن محیط {slug} به پروژهٔ {project}»; "Create database {name}" / «ساخت دیتابیس {name}»; "Attach database {db}
to {app}" / «اتصال دیتابیس {db} به اپ {app}»; "Detach database {db} from {app}" / «جدا کردن دیتابیس {db} از اپ
{app}»; "Rotate the password of database {db}" / «تعویض رمز دیتابیس {db}»; "Stop database {db}" / «توقف دیتابیس
{db}»; "Start database {db}" / «راه‌اندازی دیتابیس {db}»; "Delete database {db}, keeping its data" / "… and its
data" (title says which) / «حذف دیتابیس {db} با نگه‌داشتن داده‌هایش» / «… همراه با داده‌هایش»; "Delete project
{name}" / «حذف پروژهٔ {name}».

Details and warnings, per tool:

- **create_project** — detail: slug, comes with environment `production` (Production). `Creates` =
  `(project, slug, Environment: "production")` — see section 5 for why the environment rides on the project.
- **add_environment** — detail: own private network, not the default. `Creates` = `(environment, slug, Project: projectSlug)`.
- **create_database** — detail: "PostgreSQL 16 in shop/production, size small (1 CPU, 1024 MB memory)" (or "no size
  limit"), Redis policy/cap when Redis. Warnings: provisioning is queued; when billing is on and the workspace is
  not the provider's, the first hour is charged at creation and may still be refused for payment.
  `Creates` = `(database, name, project, environment)`. Result: `status: "provisioning queued"`, never "running".
- **attach_database** — detail: variables prefixed `{ALIAS}_`, the logical database. Warning: takes effect on the
  app's next deploy.
- **detach_database** — detail names the alias; "No data is deleted". Warning: the app loses the settings on its
  next deploy and fails to connect if it still uses the database.
- **rotate_database_password** — detail: a new password, written into the apps using it, never shown. Warnings:
  the apps using it (visible ones, from `ServiceUsageService.AppsUsingAsync`) cannot connect until redeployed;
  Redis/Meilisearch: the container is recreated. Result: `redeploy: [slugs]` = apps the engine rewrote ∪ apps
  attached by join row.
- **stop_database** — warnings: who loses their database; replicas that stop receiving changes.
- **start_database** — warning (billing on): charged by the hour; a workspace that may not start anything is
  refused when the step runs.
- **delete_database** — phrase = the database's exact `Name`; detail and warning say plainly whether the volume
  is deleted or kept (and its volume name).
- **delete_project** — phrase = the project's `Name` exactly as `ProjectRemovalPlan.IsConfirmed` compares it
  (trimmed, ordinal; an always-true assertion of that was removed after review); detail lists every app (with environment and volume count), every
  database, scheduled functions; warnings: everything with its data, domains that stop serving. On success it
  writes the controller's own `project.deleted` audit entry (same metadata); a partial delete is `Ok = false`
  naming what is left, with no `project.deleted` entry.

Links (real routes): `/databases/{id}`, `/apps/details/{id}`, `/projects/{id}`, `/projects/{id}?environmentId={envId}`.

## 3. Planned resources

`ValidateAsync` accepts a project, environment, database or app that an earlier step of the same plan creates:

- **create_database** in a planned project (or planned environment): the service's validation runs with no
  environment id; its placement refusal (judged against the workspace's default environment, the only one it can
  see) is dropped by `Field`, and placement is asked here — `AllowsAsync(new(null,null))` for a planned project,
  `new(projectId,null)` for a planned environment of a real project. Catalogue, quota, duplicate name, scheduler
  and Redis checks still run (except when the service's first group stopped early on that placement refusal; then
  execution checks them). Detail says it is checked again when the step runs.
- **attach_database**: database planned + app real → app access (`CanTouchAppAsync(AppsEnv)`) and whether the
  planned database's place equals the app's (an environment is a private network); app planned + database real →
  database access, replica refusal, logical-database name, place; both planned → place.
- A planned project named by its exact slug **wins over a real project the words only matched by display name**
  (`ProjectDatabaseToolSupport.ProjectAsync`). Found in testing: the harness's project is displayed "Shop", so
  after "create project shop" a later "shop" resolved to the old project. Execution already resolves slug-first,
  so this only aligns validation with it.
- `ExecuteAsync` always resolves for real (scope null).

## 4. Where the brief and the code disagreed

1. **Scenario 1 cannot pass as written.** "Create project shop, a Postgres 16 database shop-db in its production
   environment, and attach it to app web (web exists)": an existing app cannot be in a project that does not exist
   yet, and attaching across environments is refused by `NetworkWiring.CanAttach` (different private networks).
   So scenario 1 is two tests plus the failure variant:
   - *literal*: web in the existing project → plan of two safe steps (project, database) validated against the
     planned project; the attach is refused **while the plan is drawn** with "App web is in shop-…/production, but
     database shop-db is in shop/production. Each environment is its own private network…", fed back to the model;
     approve → both run, in order.
   - *coherent*: web created in the new project by a stand-in `create_app` `FakeTool` (agent F's tool is not on
     this branch; the stand-in uses F's `Creates` convention) → one plan, four safe steps, steps 2–4 validated
     against planned resources → approve → all four `done`, in order (audit order asserted), each with its links.
   - *failure*: same plan, billing switched on after the card (first hour unpriced, which validation cannot see) →
     `done, failed, skipped, skipped`; step 2's error in both languages; "Skipped: an earlier step failed."; the
     project from step 1 stays; nothing half-created; the conversation's tool messages record it.
2. **`LogicalDatabaseService.RotatePasswordAsync` does not exist.** The rotation is `IManagedServiceEngine.RotatePasswordAsync`,
   which is what `DatabasesController.Rotate` calls; the tool uses that.
3. **`delete_project` needs `AppsDelete`, not `AppsCreate`** — that is `ProjectsController.Delete`'s policy (the brief's
   "AppsCreate for projects/environments" covers create/add only).

## 5. Decisions where the brief was silent (please look)

1. **Taken slugs are refused, not suffixed.** `ProjectService` quietly turns "shop" into "shop-2" (and "staging" into
   "staging-2"). Later steps name the slug this step promised, so `create_project`/`add_environment` refuse a taken
   slug at validate and execute, and if it is taken between the tool's check and the service's save (the service
   suffixes it), the step returns `Ok = false` naming the slug it got, which stops the later steps. Tested by a quota
   hook that takes the slug between the two checks.
2. **`Creates` carries one resource**, so `create_project` returns `(project, "shop", Environment: "production")`;
   `TargetAsync` treats a blank or "production" environment in a planned project as will-exist. Agent F's tools
   should read a planned project's default environment from that field (or assume "production").
3. **A Persian project name needs a Latin `slug`** (Slugify of Persian is empty and the service would fall back to
   "project"); the display name may stay Persian.
4. **`size` must be an enabled `InstanceSize` key.** The creation path treats a key it cannot find as "no size" — a
   database with no ceiling — where the form could only post a listed size. Whether the plan allows it stays the
   quota's question.
5. **`stop_database` only on a running database; `start_database` refuses one still provisioning.** The panel shows
   Stop only when running. `ManagedServiceEngine.StartAsync` sets `Running` whether or not it found a container, so
   starting a database that is still provisioning would report a running database that is not there.
6. **`rotate_database_password` refuses a replica** (its password is its primary's), maps `WhyUnsupported` with a
   Persian translation, redacts the engine's error by the database's passwords and by shape (it can carry one-off
   container output), and asks the stored ciphertext afterwards: unchanged → "not changed" even if the engine
   returned quietly; changed but the engine then threw (e.g. a Redis recreate) → `Ok = false` saying the password
   **was** changed and the apps need a redeploy.
7. **`delete_project` refuses a project holding an app with a protected volume.** The deletion service would skip
   that app and delete everything else, leaving a half-deleted project; the panel lets that happen and reports what
   is left. The refusal names the app.
8. **Deadlines.** `start_database`/`stop_database` give the engine call its own deadline (2 minutes, settable for
   tests; a hung node returns `Ok = false, timedOut`). `rotate_database_password`, `delete_database` and
   `delete_project` deliberately have none: cancelling between the database accepting a new password and the engine
   storing it would lock every app out, and a delete cut half-way leaves a worse state than waiting. Those calls are
   the same ones the panel's buttons make inside a request. I did not verify the Docker client's own timeout.
9. **`start_database` does not ask `IBillingGate` at validate.** My first version did; the full suite's
   `QuotaRefusalBilingualismTests.Every_caller_of_the_billing_gate…` census failed on it, and `IBillingGate`'s own
   doc says the gate is asked at the last place before a container runs, never at the button. Removed (`e0bc607`); the
   engine's `QuotaRefusedException` is mapped in both languages at execute, and the card warns when billing is on.
10. **Kinds:** `start_database` SafeWrite (reversible, as the brief says); `detach_database`, `rotate`, `stop`
    DangerousWrite; both deletes Destructive. `attach_database` SafeWrite as briefed (additive; effective on the next
    deploy).

## 6. Tests

119 new test cases, all in `Harbora.Tests`:

| File | Cases | Covers |
|---|---|---|
| `Agent/ProjectToolsTests.cs` | 29 | create_project: card + `Creates` + nothing changed; Persian name needs a slug; missing name / taken slug; quota both languages; planned duplicate; Viewer and scoped member refused (validate and execute); execute: rows, audit, link, both summaries, nothing left tracked; slug taken after validate → nothing created; slug taken while the service runs → `Ok=false` naming `shop-2`. add_environment: card + `Creates`; planned project (and "production" refused there); four refusals; quota; Viewer refused, scoped member "not found"; another workspace's project "not found" (also for delete_project and create_database); execute + link; added meanwhile → nothing added. delete_project: card lists apps/volumes/databases/domains, phrase = name, nothing changed; empty project; protected volume refused; Viewer refused, scoped member not found; execute removes everything + `project.deleted`; protected volume added after validate → nothing deleted; partial delete → `Ok=false` naming what is left, no `project.deleted` |
| `Agent/DatabaseToolsTests.cs` | 66 | create_database: card + no row/lock/provision; size and first-hour warning; 7 refusals (engine, version, size, Redis args on Postgres, no project, no environment, no name); the service's own refusals (duplicate, quota) both languages; planned project / planned environment / unknown environment of a planned project; planned duplicate by container slug; Viewer and scoped member; execute in the asked environment, provisioning queued, audit, link, **no password or ciphertext in ResultJson or summaries**; name taken meanwhile; billing refusal at execute leaves nothing; environment mismatch → `Ok=false` with link. attach: card + nothing written; six refusals; planned database (same / other environment, exact refusal text); planned app, replica, both planned; Viewer / scoped; execute + links; attached meanwhile. detach: card; refusals + Viewer; execute + detached meanwhile. rotate: warnings (apps, recreate); MongoDB/replica/Viewer; execute changes the stored password, redeploy list, no password anywhere; refused rotation redacted, password unchanged; quiet no-op not reported; fails after storing → says it changed. stop/start: warnings; already stopped/running; provisioning refused for both; execute; hung node → timed out; engine billing refusal both languages, nothing changed; started/removed after validation refused. delete_database: keep vs delete-data cards, phrase = exact name; attached/replica refusals (exact text), Viewer; execute; attached meanwhile → not deleted. Theories: every tool's validation accepts a sound step with both languages and leaves no row and no tracked change (10); Viewer and scoped member cannot touch a database (5 tools); another workspace's database not found (6 tools) |
| `Agent/ProjectDatabaseToolScenarioTests.cs` | 22 | the four scenarios through `AgentConversationService` + `AgentPlanService` (section 4 for scenario 1's three tests; scenario 2 remembered `create_database` → next turn `started, message, executed, done`; scenario 3 delete with data: phrase, data-loss warning in both languages, wrong text `confirmation_mismatch` and nothing removed, right text removes; attached database refused while drawing the plan with the service's reason; scenario 4 `stop_database` carded with every safe tool remembered and even a stray approval row); kind + capability of each tool (10); registry offers all ten to Owner/Member and none to Operator/Viewer through the panel's real policies (4); every schema an object whose arguments are described |
| `Http/ProjectDatabaseToolsHttpTests.cs` | 2 | every tool built by the booted panel's own container (catches a missing registration of a constructor dependency); create_project + add_environment planned, approved and executed over HTTP with sign-in, antiforgery and the real quota/project services |

**Mutation check** (five mutants applied together to four files as committed in `8f8f2a8`, built, the three
direct/scenario classes run, then the files restored from copies and compared byte for byte): dropping the environment comparison in create_database, dropping the slug-mismatch check in
create_project, dropping the `CanTouchServiceAsync` guard, making a real display-name match beat a planned slug,
and disabling the cross-environment check in attach → `Failed! - Failed: 10, Passed: 107, Total: 117`, every mutant
caught by at least one named test. The final runs below are on rebuilt, restored code (and a later commit).

## 7. Commands and output

Every build/test with `export MSBUILDDISABLENODEREUSE=1` and `-p:UseSharedCompilation=false`; log
`/tmp/test-r6-db-tools.log`.

- Baseline before any change: `dotnet build Harbora.slnx --nologo -v q -p:UseSharedCompilation=false` →
  `BUILD_EXIT=0`, `28 Warning(s)`, `0 Error(s)`.
- Focused while iterating: `dotnet test tests/Harbora.Tests --no-build --filter "FullyQualifiedName~Harbora.Tests.Agent.ProjectToolsTests|…DatabaseToolsTests|…ProjectDatabaseToolScenarioTests|FullyQualifiedName~ProjectDatabaseToolsHttpTests"`
  → last: `Passed!  - Failed:     0, Passed:   119, Skipped:     0, Total:   119`.
- Related existing classes, two runs: (a) `FullyQualifiedName~Harbora.Tests.Agent.|~AssistantHttpTests|~AssistantPane|~DependencyInjection|~AppScopeCensusTests|~ProjectDatabaseToolsHttpTests`
  → `Passed!  - Failed:     0, Passed:   250, Skipped:     0, Total:   250` (before the first commit);
  (b) after the billing-gate fix, `FullyQualifiedName~Harbora.Tests.Agent.|~ProjectDatabaseToolsHttpTests|~QuotaRefusalBilingualismTests|~QuotaRefusalReachability|~BillingGate`
  → `Passed!  - Failed:     0, Passed:   262, Skipped:     0, Total:   262`.
- First full run (before `e0bc607`): `Failed!  - Failed:     1, Passed:  6946, Total:  6947 - Harbora.Tests.dll` —
  the billing-gate census (section 5, item 9). Fixed, then:
- **Final:** `dotnet build Harbora.slnx --nologo -v q -p:UseSharedCompilation=false` → `BUILD_EXIT=0`,
  `28 Warning(s)`, `0 Error(s)` (all NU1900/NU1903 package warnings, as the baseline; no compiler warnings).
  `dotnet test Harbora.slnx --nologo -v q --no-build` → `EXIT=0`:
  ```
  Skipped! - Failed:     0, Passed:     0, Skipped:   108, Total:   108, Duration: 65 ms - Harbora.Postgres.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:   500, Skipped:    17, Total:   517, Duration: 30 s - Harbora.NodeAgent.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:    15, Skipped:     0, Total:    15, Duration: 55 s - Harbora.NodeIngress.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:  6947, Skipped:     0, Total:  6947, Duration: 3 m 10 s - Harbora.Tests.dll (net10.0)
  ```
  `grep -c "Harbora.Tests.dll"` → 2. 6947 = 6828 (master after D, per `progress.md`) + 119 new.

## 8. Unverified here

- **Docker / nodes:** start, stop, rotate and both deletes ran only against a fake engine and fake app operations.
  The real `ManagedServiceEngine` (container restart/stop/remove, the rotation one-off, Redis recreate) and
  `AppOperationsService` (routes, proxy re-apply) were not exercised with these tools.
- **Postgres lane:** the creation lock and transaction rollback inside `CreateAsync`/`ProjectService` are no-ops on
  EF InMemory; no Postgres-lane test was added for the tools.
- **Billing with a real wallet:** only the "unpriced first hour" refusal was exercised (billing on, no size).

## 9. Concerns and notes for the coordinator

1. Scenario 1 as briefed contradicts the network rule; covered as described in section 4 — please confirm the
   reading.
2. `DatabaseProvisioningService.CreateAsync` calls `db.ChangeTracker.Clear()` on a billing refusal — inside a plan
   run that clears the shared context, agent rows included. The plan engine copes (it re-reads the plan; the
   failure scenario test proves the run finishes `failed` with its record), but it is a broad side effect for a
   service the agent calls in the middle of its own work.
3. `ManagedServiceEngine.StartAsync` writes `Running` even when it found no container (observed while writing the
   provisioning refusal); not changed, reported.
4. `AgentToolContext` has no IP address, so `database.created`, `database.attached`, `project.deleted` etc. written
   for assistant steps carry none (the step audit does). A field on the context would fix it; contracts are fixed,
   so not changed.
5. `AgentHarness` (D's kit) gives every harness made within a few hours the same project slug (a v7 id's timestamp
   prefix), so "another workspace" tests must seed their own slugs — mine do.
6. For agent F: `create_project`'s planned environment rides on `Creates.Environment`; my `attach_database` reads a
   planned app's place from `Creates.Project`/`Creates.Environment` when F fills them and skips the place check when
   they are null.

## 10. Fixes after review

The review approved the branch with no Critical or Important findings and asked for one small pass. All five
points are done in `c8601dc`:

1. **Rotation failures of any kind.** `rotate_database_password` used to catch only `InvalidOperationException`, so
   a Docker/HTTP exception thrown after the new password was stored (during the Redis/Meilisearch recreate) went to
   the plan engine's generic failure, and nobody was told the password had changed. It now catches every exception
   except the caller's own cancellation (`when (!ct.IsCancellationRequested)` — the plan engine passes `None`, so an
   internal timeout is handled too), logs it with its stack (new `ILogger<RotateDatabasePasswordTool>`), and always
   re-reads the stored ciphertext: changed → `Ok = false`, "The password of {db} was changed, but the rest of the
   rotation failed: … the apps that use it need a redeploy", with the database's link; unchanged → "The password
   was not changed."
2. **The new password is redacted too.** In the "changed, then failed" branch the database's secrets are read again
   from the freshly re-read row (`AgentSecrets.ForDatabaseAsync`) and added to the old ones before the engine's
   message is redacted, so a one-off command line naming the new password (`--requirepass x`, which no shape rule
   matches) does not reach the person or the model.
3. **delete_database names the apps using the database.** The card's first warning is now the panel's
   `ConfirmRemove` list (`ServiceUsageService.AppsUsingAsync`, visible apps only, via `VisibleUsersAsync`), which also
   finds apps wired by a copied connection string, which the structural refusal does not see: "1 app: legacy —
   still configured to use it, and will fail to connect once it is gone." / Persian; "No app you can see uses it."
   when there are none (as stop/rotate say). The tool now also takes `ServiceUsageService` and `ProjectAccessService`.
4. **Timeout wording.** Start and stop now say the operation "may still finish on the server" / «ممکن است روی سرور
   همچنان انجام شود».
5. **Polish.** The always-true `plan.IsConfirmed(plan.ProjectName.Trim())` check in `delete_project` is gone. The
   Persian error from a failed rotation uses `QuotaRefusedException.ReasonFa` when the exception is one (redacted
   the same way), and the English text otherwise.

Tests (4 new, 4 changed; `DatabaseToolsTests` now 70 cases):

- new: `A_docker_failure_after_the_new_password_is_stored_is_told_as_changed_with_the_new_password_redacted`
  (an `HttpRequestException` after storing whose message carries the new password: told as changed, password in
  neither language nor the result JSON); `A_non_engine_failure_before_anything_is_stored_says_the_password_did_not_change`;
  `A_billing_refusal_during_the_rotation_reaches_a_persian_reader_in_persian`;
  `Delete_database_names_the_apps_still_wired_to_it_by_a_copied_connection_string` (an app with an encrypted
  `DATABASE_URL` naming the container and no join row).
- changed: `A_rotation_that_fails_after_storing_the_new_password_says_the_password_changed` (the fake engine now
  takes the exception to throw); `A_node_that_never_answers_is_a_failure_after_the_tools_own_deadline` asserts the
  new wording in both languages; `Delete_database_says_plainly_whether_the_data_goes_and_asks_for_the_exact_name`
  and scenario 3's `Deleting_a_database_with_its_data_needs_its_exact_name_typed` allow for the new usage line.
- Mutation check: narrowing the catch back to `InvalidOperationException` (together with dropping `ReasonFa`)
  failed the docker-failure, non-engine and billing tests; dropping only the secret re-read failed the
  docker-failure test (`Failed: 1, Passed: 69, Total: 70`). The file was restored from its copy and compared, and
  everything was rebuilt before the runs below.

Commands (same environment and recipe):

- Focused: `dotnet test tests/Harbora.Tests --no-build --filter "FullyQualifiedName~Harbora.Tests.Agent.|FullyQualifiedName~ProjectDatabaseToolsHttpTests|FullyQualifiedName~QuotaRefusalBilingualismTests"`
  → `Passed!  - Failed:     0, Passed:   243, Skipped:     0, Total:   243`.
- `dotnet build Harbora.slnx --nologo -v q -p:UseSharedCompilation=false` → `BUILD_EXIT=0`, `28 Warning(s)`,
  `0 Error(s)` (NU1900/NU1903 only).
- `dotnet test Harbora.slnx --nologo -v q --no-build` → `EXIT=0`:
  ```
  Skipped! - Failed:     0, Passed:     0, Skipped:   108, Total:   108, Duration: 82 ms - Harbora.Postgres.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:   500, Skipped:    17, Total:   517, Duration: 31 s - Harbora.NodeAgent.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:    15, Skipped:     0, Total:    15, Duration: 57 s - Harbora.NodeIngress.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:  6951, Skipped:     0, Total:  6951, Duration: 3 m 6 s - Harbora.Tests.dll (net10.0)
  ```
  `grep -c "Harbora.Tests.dll"` → 2. 6951 = 6947 + the 4 new tests.

Still unverified, as before: the real engine (Docker, the Redis recreate, node HTTP) and the Postgres lane.
