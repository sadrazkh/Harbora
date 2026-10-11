# F — App tools (wave B) — report

Branch `r6/app-tools`, worktree `../Harbora-r6-f`, base `d41247d` (master 9a99c4a + the wave-B briefs and
`AgentWriteTool`). No rebase was needed; nothing outside my files was touched except one census test (§7).

**Status: DONE_WITH_CONCERNS.** All thirteen tools and the four scenario tests are built and green, and
the full suite passes (Harbora.Tests 7029 = 6828 + 201). The concerns (§9) are mostly about the agent core
and the pre-existing services: a secret value the model passes to a tool is replayed to the provider and
stored in plain text by the core, `AgentTracker.DetachForeignChanges` can throw on a staged graph, and two
operations-service verbs can still report work that did not happen.

## Commits (owner identity, no trailer)

```
f76f5a3 Agent app tools: create, template, env vars, domains, volumes, deploy, restart, stop, rollback and delete, each over the service its panel action calls
ecf4e0c rollback_app refuses while a deployment is in flight instead of letting the engine join it to another rollback, and stop_app refuses an app that was never deployed
1286637 App tool tests: the tools that act on an app as it is now refuse one that is only planned, and a volume made Protected after its card keeps its data and its mount
dd6c408 create_app reads planned projects and environments by the conventions create_project and add_environment use, deploy_template refuses taken slugs instead of letting the service suffix them, and both drop what a throwing service staged
60f7601 Billing-gate census: restart_app is filed as a caller that hands both halves of the refusal on itself
```

There is also the commit that adds this report.

## 1. Files

`src/Harbora.Infrastructure/Agent/Tools/`:
- `AgentAppTool.cs` is the shared base and does not change the contract.
- `CreateAppTool.cs`, `DeployTemplateTool.cs`, `SetEnvVarsTool.cs`, `DeleteEnvVarTool.cs`, `AddDomainTool.cs`,
  `RemoveDomainTool.cs`, `AddVolumeTool.cs`, `RemoveVolumeTool.cs`, `DeployAppTool.cs`, `RestartAppTool.cs`,
  `StopAppTool.cs`, `RollbackAppTool.cs` and `DeleteAppTool.cs`.

The DI block in `DependencyInjection.cs` is one contiguous block of thirteen `AddScoped<Agent.IAgentTool, …>()`
lines. It sits right after the phase-0a app-services block and carries a comment naming this work. It is separate
from G's block, which sits after the phase-0b database services. No `Program.cs` change was needed.

Tests are in `tests/Harbora.Tests/Agent/`. See §8.

## 2. The shared base: `AgentAppTool : AgentWriteTool`

- **`TargetAsync(context, name, capability, scope?, ct)`** returns an `AppTarget`, which is one of: an app, an
  app that a plan will create, or a refusal.
  - It resolves only through `AgentResourceResolver.AppAsync`, so an invisible app and a missing app get the same
    sentence.
  - It then asks `ProjectAccessService.CanTouchAppAsync(app.Id, capability)`. When that fails, the refusal says
    which permission was missing.
  - **With a scope** (validation only), a name that no app has yet is accepted as will-exist when an earlier step
    of the same plan creates it, i.e. `scope.Planned` has kind `app` with that name. The capability is then asked
    with `AllowsAsync` of the environment that step names. If that environment is itself still planned, it is
    asked of `ResourcePlacement(null, null)`, which only an unscoped member passes.
  - **Execution passes no scope**, so by then the app must exist.
- **Links** use the panel's real routes: `/apps/details/{id}`, `/apps/{id}/volumes`, `/deployments/details/{id}`,
  `/projects/{id}` and `/databases/{id}`.
- **Deployments are read back, not assumed.** `ReadDeploymentAsync` and `UnsettledDeploymentAsync` read the row.
  `Describe` words it as "deployment #N is queued" or "… is waiting for approval (protected environment)". It
  never says "running" or "done".
- **`DeployRefusal(ex)`** maps the engine's three preflight refusals (quota, capacity, low disk) to their own
  Persian. For any other `InvalidOperationException` from the engine (a rollback/deploy intent mismatch), the
  Persian is «استقرار در صف قرار نگرفت: …» followed by the English detail. This is the shape phase 0a used for
  English-only texts.
- **`DropStaged(db, before)`** detaches whatever a throwing service staged, starting with the rows that other
  rows depend on (leaves first). See concern 2 for why it exists.
- **Helpers:**
  - `AllowedSizeKeysAsync` applies the same plan filter as `get_catalog` and the panel's choosers.
  - `MayChargeAsync` plus `FirstHourWarning` produce the first-hour card warning when billing is on and the
    workspace is not the provider's.
  - `CheckRepositoryUrl` accepts only an http(s) URL with no user info, which refuses tokens embedded in the URL.
  - `ProtectedEnvironmentWarning`.
- `ActorOf(context)` returns `new AppActor(workspaceId)` with **no client IP**: `AgentToolContext` carries none
  (concern 6).

## 3. The tools

The capability column was read off the matching controller action's `[Authorize(Policy = …)]`. A test reflects
over `AppsController` and asserts that the policy equals the tool's `Capability`. In addition to the declared
capability, every tool re-asks `CanTouchAppAsync` (or `AllowsAsync` for creation) in both `ValidateAsync` and
`ExecuteAsync`.

| Tool | Kind | Capability | Panel action | Calls |
|---|---|---|---|---|
| `create_app` | SafeWrite | AppsCreate | `AppsController.Create` | `IAppProvisioningService.ValidateCreateAsync` / `CreateAsync` |
| `deploy_template` | SafeWrite | AppsCreate (+ DatabasesManage asked in code) | `TemplatesController.CanDeployAsync` | `TemplateDeploymentService.DeployAsync(DeployNow: false)` + `IDeploymentEngine.QueueDeploymentAsync` |
| `set_env_vars` | SafeWrite | AppsEnv | `AddEnv` | `ValidateSetEnvAsync` / `SetEnvAsync` |
| `delete_env_var` | DangerousWrite | AppsEnv | `DeleteEnv` | `ValidateDeleteEnvAsync` / `DeleteEnvAsync` |
| `add_domain` | SafeWrite | AppsEnv | `AddDomain` | `ValidateAddDomainAsync` / `AddDomainAsync` |
| `remove_domain` | DangerousWrite | AppsEnv | `DeleteDomain` | `ValidateRemoveDomainAsync` / `RemoveDomainAsync` |
| `add_volume` | SafeWrite | AppsEnv | `AddVolume` | `ValidateAddVolumeAsync` / `AddVolumeAsync` |
| `remove_volume` | Destructive | AppsEnv | `RemoveVolume` | `ValidateRemoveVolumeAsync` / `RemoveVolumeAsync` |
| `deploy_app` | SafeWrite | AppsDeploy | `Deploy` | quota pre-check as `Deploy` does + `QueueDeploymentAsync` |
| `restart_app` | SafeWrite | AppsOperate | `Restart` | `IAppOperationsService.RestartAsync` |
| `stop_app` | DangerousWrite | AppsOperate | `Stop` | `IAppOperationsService.StopAsync` |
| `rollback_app` | DangerousWrite | AppsDeploy | `Rollback` | `IRollbackPlanner.PrepareAsync` + `QueueDeploymentAsync(RollbackToDeploymentId)` |
| `delete_app` | Destructive | AppsDelete | `Delete` | `IAppOperationsService.DeleteAsync(appId, removeVolumes)` |

The kinds are the brief's, with no change.

The controllers write some audit entries themselves rather than leaving them to the services. The tools write the
same entries with the same target and metadata: `app.deploy` (deploy_app), `app.rollback` with `{toDeploymentId}`,
`app.delete` with `{removeVolumes}`, and `template.deploy` with `{projectId, dependencies}`. The services' own
audits (`app.created`, `app.env_*`, `app.domain_*`, `app.volume_*`) come from the services. The plan engine adds
`assistant.step_executed` on top.

Every deploy a tool queues uses `DeploymentTrigger.Assistant` and the person's user id. That covers the first
deploy of `create_app` and `deploy_template`, `deploy_app` and `rollback_app`.

### create_app
- **Arguments:**
  - `name` (required) and `slug`.
  - Either `image` or `repository`. If `repository`, also `gitRef` (default main), `rootDirectory` and
    `dockerfilePath`.
  - `port` (default 80), `project`, `environment`, `size` and `deployNow` (default true).
- **The slug is always sent explicitly.** It is derived from the name when not given, using the rule
  `[a-z0-9-]`, at most 50 characters. A taken slug is therefore refused in the service's own words instead of
  becoming "web-2", because the card and later steps name "web". A name that yields no Latin slug is refused, with
  a request for a transliterated one.
- **No credentials, in any shape.**
  - Arguments named `gitToken`, `token`, `accessToken`, `password`, `credential` or `credentials` are refused
    before anything else is read.
  - Repository URLs with user info are refused, and so are non-http(s) URLs.
  - The description tells the model that private repositories belong to the panel form.
  - A test asserts that the stored `GitProvider.EncryptedCredential` is null.
- **The environment is resolved here, by name.**
  - No `project`: one visible project means it is used. Several means the tool asks which, listing their slugs.
    None, in an empty workspace, means the service makes the default project, as the form does.
  - **Planned project or environment** (per the coordinator's conventions): `create_project` plans kind `project`
    with its first environment's slug in `Environment`. Only that environment is offered unless `add_environment`
    planned another (kind `environment`, `Project` set). Placement access is asked of the existing project, or of
    `(null, null)` when the project is planned. The service's placement refusal (`Field == EnvironmentId`) is
    dropped only in that case, because the service judged the workspace's default environment instead.
- A `size` must be enabled and allowed by the plan. The service would silently treat an unknown key as no size.
- **The card:**
  - Title: "Create app web" / «ساخت اپ web».
  - Detail: source, port, place, size and deploy-now.
  - Warnings: the planned place, the first hour, and the protected environment when deployNow is set.
  - `Creates = (app, slug, project, environment)`.
- **Execution:**
  - Re-runs every check and then calls `CreateAsync`.
  - Compares the created app's environment with the one it asked for. A mismatch is a failure that states where
    the app actually landed.
  - Reads the first deployment back: queued, or pending approval.
  - Reports a deploy refused after the create as "created, first deployment did not start: …", in both
    languages.
  - Links the app and the deployment.
  - An exception from the service (the platform-wide unique-slug race it documents) runs `DropStaged`, then
    rethrows.

### deploy_template
- **Arguments:** `template` (key or name from `get_catalog`), `name`, `project`, `repository`, `gitRef`,
  `variables` (object), `size`, `deployNow`.
- **The service has no validate method**, so validation asks read-only what it would refuse first:
  - the template is visible (`TemplateCatalog.IsVisibleTo`) and its manifest parses;
  - access, as described below;
  - the dependency types are supported;
  - a repository is given when the template builds from Git (with the URL rule), and refused when it does not;
  - **every `NeedsAValue` variable is given**;
  - **a variable the service would silently ignore is refused**, whether undeclared or one with a default or a
    generated secret;
  - **a value referring to an unknown service (`${{redis.host}}`) is refused**;
  - a local server exists, and some version is deployable there;
  - the size is allowed;
  - the quotas: governed project and environment, app, service.
- **Access** asks `AllowsAsync(new(null, null), AppsCreate)` and, when the template brings databases,
  `DatabasesManage`. This is `CanDeployAsync`'s pair of capabilities, asked the way `ProjectsController.Create`
  asks for a new project. It is therefore stricter than the template page for scoped members (§6).
- **Slugs:** the new project's slug and the app's slug, both by `ProjectService.Slugify`, are **refused when
  taken** instead of being suffixed (per the coordinator: later steps name the slug they were promised).
  `Creates` is `(app, slug, project-slug, production)`, or `(database, name, …)` for a database template.
- **The card:**
  - Says plainly that it creates a new project.
  - Shows plain variable values (shortened) and secret ones as "value hidden".
  - Adds the first-hour warning.
- **Execution:**
  - Calls the service with `DeployNow: false`, then queues the first deploy itself as `Assistant`. Done this way,
    a queue-time refusal is reported as "project exists, first deploy did not start" instead of escaping after the
    commit, and the deploy is not recorded as Manual.
  - When the service throws `InvalidOperationException` after staging the project, environment and databases,
    `DropStaged` detaches them and the step fails. `QuotaRefusedException` and `CreationPaymentRequiredException`
    get their Persian; anything else gets «قالب مستقر نشد: …». Any other exception: `DropStaged`, then rethrow.
  - The summary says the databases are "queued to be provisioned".
  - Links: project, app, databases and deployment.

### set_env_vars
- **Arguments:** `app`, plus `variables: [{key, value, secret?, availableAtBuild?}]`.
- Keys containing `=`, whitespace or control characters are refused. Values are not trimmed. A repeated key keeps
  its last value, which is what the service does.
- The planned app is supported: the only check is that keys are given. The detail says the remaining checks run
  later.
- **The card:**
  - Title: "Set environment variable K on web" or "Set N environment variables on web".
  - Detail: "K = value" (shortened) for plain variables, "K (secret — value hidden)" for secret ones,
    ", replaces the current value", ", also at build".
  - Warnings: "Takes effect on the app's next deploy", plus one when a secret would become plain.
- **No secret value appears** in the card, the title the model reads, `ResultJson` (keys and flags only), the
  summaries or the audit entries. Tests assert this.

### delete_env_var / add_domain / remove_domain / add_volume / remove_volume
- **delete_env_var** names the variable and says whether it is secret. Warning: removing configuration can break
  the next deploy.
- **add_domain** has `ssl` default true.
  - The card says whether HTTPS is on and whether the domain becomes the primary address.
  - Warning: it is routed on the next deploy, and DNS must point at the server.
  - For a planned app, validation checks that the host is given, not in use platform-wide, and within the plan's
    room. The reserved-host check runs at execution (concern 7).
- **remove_domain**'s card names what happens to the primary address, using the service's own succession rule:
  "It is the primary address; www… becomes the primary address", "… stays the primary address", or "It is the
  app's only domain: web is left with no address".
- **add_volume** works for a planned app, with `MountPath.Check` and a quota check. It says the volume is attached
  on the next deployment. Its link goes to the Volumes tab.
- **remove_volume:**
  - Arguments: `deleteData`, default false. Phrase: the app's slug.
  - The detail says whether the data is deleted or stays on the server in the named volume.
  - A Protected volume refuses `deleteData`, at the card and again at execution.
  - If Docker did not delete data that was asked to be deleted, the result is **`Ok = false`**, saying the volume
    was unmounted and the data is still in volume X.

### deploy_app
- **Arguments:** `app`, `gitRef`. A `gitRef` for an app not built from Git is refused.
- **Validation:** `AppsController.Deploy`'s pre-check `quota.CanAddAppAsync(…, excludeAppId: app.Id)` runs at
  both validation and execution. The card warns:
  - that the running containers are replaced;
  - about the protected environment;
  - "Deployment #N is still in progress; this request joins it".
- **Execution:**
  - Records which deployment was unsettled before queueing. If the engine returns that same one, the result is
    `joinedExisting: true` and says "nothing new was queued".
  - Otherwise: "Deploy of 'web' requested: deployment #3 is queued", or "… waiting for approval".
  - Preflight refusals come back in both languages, and nothing is audited for them.
- A planned app is supported.

### restart_app / stop_app
- **restart_app:**
  - Refuses an app that was never deployed (`ActiveDeploymentId` null), because `RestartAsync` would restart
    nothing and still write Running.
  - Asks the billing gate at validation and returns Reason and ReasonFa.
  - At execution, catches the service's `QuotaRefusedException` and passes on its Persian.
- **stop_app:** refuses an app that is already stopped or was never deployed. Warning: "web stops serving requests
  until it is started again".

### rollback_app
- **Target:** `number`, or by default the newest Succeeded/RolledBack deployment older than the live one.
- `PrepareAsync` runs at validation and at execution, because the image can be pruned in between.
- When `CanRollback` is false, the planner's reason (English only) is given in Persian as «بازگردانی ممکن نیست: …»
  with the English detail.
- **Refused outright while any deployment is unsettled.** The engine refuses rollback-onto-deploy, but it joins a
  rollback onto a rollback, whatever that one restores.
- **The card** names the target, commit and message, and the live number it replaces. Warnings: the containers are
  replaced; only the code goes back (env vars, databases and data stay as they are); the protected environment.
- Queued with `Trigger = Assistant` and `RollbackToDeploymentId`. The pipeline decides that a deploy is a rollback
  from `RolledBackFromId`, not from the trigger (checked in `DeploymentPipeline`). The controller uses
  `DeploymentTrigger.Rollback`; I followed wave-b's rule (§6).

### delete_app
- **Arguments:** `removeVolumes`, default false. Phrase: the app's slug.
- **The card** lists what goes: the container, deployment history, env vars, the domains and the volumes. It says
  whether volume data is deleted or kept, and that the attached databases stay because they are only detached.
  Warnings: cannot be undone; data deleted; the addresses stop answering.
- **Protected volumes:** a Protected volume with `removeVolumes` is refused at the card and re-checked before the
  service is called. The service's `VolumeProtectedException` is also mapped.
- **Success** is reported only after checking (ignoring query filters) that the row is gone, because `DeleteAsync`
  returns quietly for an app it cannot find. It then audits `app.delete` and links the project.

### Planned resources, summarised
- **Accept a planned app:** `set_env_vars`, `add_domain`, `add_volume`, `deploy_app`.
- **Accept a planned project or environment:** `create_app`.
- **Refuse a planned app**, with the not-found sentence, because they need the app as it is now: `delete_env_var`,
  `remove_domain`, `remove_volume`, `restart_app`, `stop_app`, `rollback_app`, `delete_app`. There is a test for
  this.
- **Declare `Creates`:** `create_app` (app) and `deploy_template` (app or database).

## 4. Validation changes nothing

Every validation calls only read-only service methods, or reads `AsNoTracking`. The resolver tracks unchanged rows,
which is not a change. Every app tool has a test that compares `Snapshot()` before and after:
- table contents (env values, domains, volumes);
- counts of projects, environments, services, deployments and ledger rows;
- app statuses;
- audit entries;
- queued deploys, operations calls, provisioning and Docker destructive calls;
- pending tracked changes.

## 5. Results

`ResultJson` is small: the slugs, numbers, states, keys and flags. It never contains a password, token, connection
string or secret env value. Summaries are bilingual and say "queued" or "waiting for approval" for anything
long-running. Every tool saves through its service or engine before returning; a test asserts that no tracked
changes remain after `create_app`.

## 6. Decisions to look at

1. **Rollback trigger:** `Assistant`, not `Rollback`. This follows wave-b's "deploys queued by a tool use
   Assistant". It is still a rollback via `RolledBackFromId`, but the deployments list labels it "Assistant".
2. **deploy_template access** is stricter than the template page. A project-scoped member, even one with a project
   grant, is refused, because the template always creates a new project, which no grant covers. This matches
   `ProjectsController.Create` and G's `create_project`. The tool also declares `AppsCreate` for database-only
   templates, where the page asks only `DatabasesManage`.
3. **Stricter than the panel, on purpose:**
   - `rollback_app` refuses while anything is in flight.
   - `restart_app` and `stop_app` refuse never-deployed apps.
   - `create_app` refuses a taken slug instead of suffixing it, refuses a non-Latin slug, and refuses an unknown
     size.
   - `deploy_template` refuses variables the service would ignore, and taken slugs.
   - `remove_volume` fails the step when the requested data deletion did not happen.
4. **Audit entries the controllers write themselves** are written by the tools too (§3), so a step leaves the same
   trail as a click.
5. **Persian for English-only engine texts** (the planner's reasons, the engine's `InvalidOperationException`, the
   template service's exceptions) is a Persian sentence that carries the English detail. This follows phase 0a's
   precedent.

## 7. The one change outside my files

`tests/Harbora.Tests/Billing/QuotaRefusalBilingualismTests.cs`, in `QuotaRefusalReachabilityTests`.

**Why:** the census requires every `.CanStartAsync(` caller to either throw `QuotaRefusedException` or be filed as
English by design. `restart_app`'s validation asks the gate and returns both halves itself. A validation is a
returned value, so there is no request-scoped catch to pick a language from.

**What changed:**
- Added a third, explicit list, `HandsBothLanguagesOn`, with the reason for each entry.
- An entry passes only if its file still reads `ReasonFa`.
- The stale-entry check covers this list as well.
- The call site carries the matching comment.

The first full run caught this; the fix is `60f7601`.

## 8. Tests (201 new, all in `tests/Harbora.Tests/Agent/`)

| File | Tests | What |
|---|---|---|
| `AppToolsKit.cs` | — | `AppToolsHarness`: the agent harness, the real app services, the template service, the rollback planner and the operations service's real delete over the same InMemory context. `RowDeployments` writes Queued or PendingApproval rows and joins or refuses like the engine. `RecordingOperations` records restart and stop, which use `ExecuteUpdate`, which InMemory cannot run. Also `RecordingManagedServices`, `RefusingGate`, `SeedApp`, `SeedForeignApp`, `SeedProject`, `Grant` and `Snapshot` |
| `AppToolsCommonTests.cs` | 89 | (a) kind and capability per tool, with the capability reflected off the controller (13); (b) for all 11 app-targeting tools: a valid call validates with bilingual card text and changes nothing; another workspace's app is not found and nothing changes; Viewer refused at validation and execution; scoped member without a grant cannot see the app; scoped member with a read-only grant sees it but cannot touch it; scoped member with a member grant may; (c) Operator may restart and stop only; the seven "needs the app now" tools refuse a planned app; execution does not accept a planned app; a planned app in an environment the person may not touch is refused |
| `AppToolRegistrationTests` (same file) | 1 | The panel's own container resolves all 13 tools plus the 6 read tools, each name once |
| `AppCreateToolsTests.cs` | 46 | create_app: card and Creates; execute (row, environment, Assistant trigger, links, summaries, audit, tracker clean); Git (ref, folder, no credential); 11 refusals; taken slug; two projects; foreign project; sizes; planned project, planned environment, environment conventions; same app twice; protected and first-hour warnings; pending approval; refused at execution (slug taken in between); deployNow false; deploy refused after create; Viewer and scoped member; granted scoped member; a throwing service leaves nothing tracked. deploy_template: card hides secrets; execute (project, app, database, encrypted secret, provisioning queued, Assistant deploy, links, audit); 6 refusals; taken slugs; foreign private template; Git template; three access cases; staged rows dropped when the service refuses late |
| `AppConfigToolsTests.cs` | 36 | set_env_vars (secret hidden everywhere and stored encrypted; replaces and warns when a secret would become plain; 7 refusals; planned app; app deleted after the card). delete_env_var (card, delete, not set, gone after the card). add_domain (card, attach; first domain becomes primary; 3 service refusals; planned app; host claimed after the card). remove_domain (primary succession; only domain; secondary; not attached and gone after the card). add_volume (card; 3 refusals; planned; mounted after the card). remove_volume (keep data; delete data; Protected; Protected after the card; Docker failed to delete; not mounted) |
| `AppLifecycleToolsTests.cs` | 25 | deploy_app (queued as Assistant with link and audit; protected means waiting for approval; joined in-flight deployment; gitRef on an image app; quota; preflight refusal bilingual and not audited; planned app). restart (card, ops call; never deployed; billing gate at validation and execution). stop (card, ops; already stopped; never deployed). rollback (default target and exact card; queued with `RollbackToDeploymentId`; audit; no such number and live number; pruned image with Persian wrapper; nothing earlier; refused while in flight; pruned after the card; engine mismatch). delete (card lists what goes and what stays; delete keeps data and attached database, audits, links project; with volumes; Protected at the card and after it) |
| `AppToolsScenarioTests.cs` | 4 | The brief's four scenarios through `AgentConversationService` and `AgentPlanService` (below) |

**Scenarios:**
1. "create an app from image nginx:alpine called web, port 80, and add domain web.example.com" gives one plan with
   two safe steps. The second step's detail says it validated against the planned app. Nothing exists before
   approval. On approval both steps are done, in order; the app and the domain exist; the deploy is Assistant;
   both steps carry links; there are two `assistant.step_executed` entries.
2. `set_env_vars` is approved with "don't ask again", which is then remembered. The next turn runs without a card
   (`started, message, executed, done`) and the secret is stored encrypted. Across three turns, the secret value is
   in no message text of any role in any model request. Its only occurrence is the arguments of the model's own
   `set_env_vars` call (concern 1). It is also absent from every event payload, from every stored message
   text and summary, from plan `ResultsJson` and from audit metadata.
3. `delete_app` produces a destructive card with phrase "web". Typing "Web" gives `confirmation_mismatch`: the app
   is still there and there were no operations calls. Typing "web" deletes it and writes the `app.delete` audit.
4. With every safe tool remembered, `set_env_vars` + `rollback_app` is carded: the safe step is auto-approved but
   nothing runs, the rollback is not rememberable, and the remembered environment variable is not written. On
   approval, `rollback_app` is not remembered and the rollback is queued.

**Mutation checks.** Each mutant was run by hand and then restored with `git checkout`, followed by a touch and a
rebuild:
- Skipping `CanTouchAppAsync` in `TargetAsync` made 30 tests fail: every Viewer and read-only-grant case, the
  Operator test, and others.
- Rollback with `Trigger = Rollback` failed the rollback test.
- `create_app` without an explicit slug failed the taken-slug, refused-at-execution and card tests.
- `DropStaged` swapped for `AgentTracker.DetachForeignChanges` failed the template staged-rows test with EF's
  "association … severed" exception (concern 2).

After restoring, the app tool run was `Passed! 187/187` at that point, and 201/201 later.

## 9. Commands and output

All runs used `MSBUILDDISABLENODEREUSE=1` and `-p:UseSharedCompilation=false`, with `LOG=/tmp/test-r6-app-tools.log`.

- **Focused runs while iterating:** `dotnet test tests/Harbora.Tests --no-build --filter "FullyQualifiedName~Harbora.Tests.Agent.App"`.
  - Last result: `Passed! - Failed: 0, Passed: 201, Skipped: 0, Total: 201`.
  - Per class: Common 89, Create 46, Config 36, Lifecycle 25, Scenario 4, Registration 1.
- **D's agent tests plus app-service tests plus `AssistantHttpTests`:** `Passed! 365/365` (before the last
  additions).
- **First full run** (on `dd6c408`): `Harbora.Tests.dll Failed! - Failed: 1, Passed: 7028, Total: 7029`.
  - The failure was `QuotaRefusalReachabilityTests.Every_caller_of_the_billing_gate…`, fixed in `60f7601`.
  - Census, app tool and billing-gate filter afterwards: `Passed! 233/233`.
- **Final:**
  - `dotnet build Harbora.slnx --nologo -v q -p:UseSharedCompilation=false` gave `BUILD_EXIT=0`, `28 Warning(s)`
    and `0 Error(s)`. These are the baseline's NU1900/NU1903 package warnings; `grep -c "warning CS"` gave 0.
  - `dotnet test Harbora.slnx --nologo -v q --no-build` gave `EXIT=0`:
    ```
    Skipped! - Failed:     0, Passed:     0, Skipped:   108, Total:   108 - Harbora.Postgres.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   500, Skipped:    17, Total:   517 - Harbora.NodeAgent.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:    15, Skipped:     0, Total:    15 - Harbora.NodeIngress.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:  7029, Skipped:     0, Total:  7029 - Harbora.Tests.dll (net10.0)
    ```
  - `grep -c "Harbora.Tests.dll"` returned 2.
  - 7029 = the 6828 baseline (master `9a99c4a`, per progress.md) + 201 new tests.

**Not verified here:**
- No Postgres-lane test was written. The tools add no SQL of their own, and the creation-lock behaviour is the
  services'.
- Nothing ran against Docker, a real deployment engine or a real model.
- No HTTP-level test exercises the new tools beyond DI resolution in the panel's container. They plug into D's
  endpoints, which are tested with fakes.

## 10. Concerns

1. **Secret values in tool-call arguments (agent core).** The core sends the model's own tool-call arguments back
   to the provider: within the turn, and on later turns through the plan's outcome message, which re-states each
   step's `ArgumentsJson`. It also stores them in plain text in `agent_messages.tool_calls_json` and
   `agent_plans.steps_json`.
   - The tools keep secret values out of everything they write.
   - But a secret given to `set_env_vars` (or a secret template variable) sits **unencrypted at rest** in those two
     columns, and is re-sent to the provider for as long as it stays in the 20-message history.
   - Scenario 2 pins this exact boundary.
   - This needs a core decision, for example encrypting step arguments, or masking secret fields in replayed and
     stored tool calls after validation.
2. **`AgentTracker.DetachForeignChanges` can throw.** It detaches in tracker order. With an added Environment and
   an added ManagedService that points at it (what `TemplateDeploymentService` stages), EF throws "The association
   between entity types 'Environment' and 'ManagedService' has been severed…". This was observed through the
   mutation above.
   - The plan engine uses that method after a failed step. It would then fall into `FinishBrokenRunAsync`, which
     clears the whole tracker.
   - My tools never leave such a graph behind (`DropStaged`, leaves first).
   - The core may want leaf-first ordering too.
3. **`AppOperationsService.RestartAsync`/`StopAsync` write status even when no container is found** (pre-existing).
   - The tools refuse never-deployed apps.
   - An app whose container vanished would still be reported as restarted or stopped.
   - Not fixed: it is the panel's behaviour too, and the panel's actions would need to handle a new failure.
4. **`TemplateDeploymentService` (pre-phase-0) has no validate method and throws English-only
   `InvalidOperationException`s.** Its governed-quota refusal drops the check's `ReasonFa`. The tool's validation
   duplicates its cheap checks, and its late refusals arrive with the Persian wrapper. A proper
   `ValidateAsync`/`ServiceOutcome` extraction would remove the duplication.
5. **Rollback default target.** The target is computed at carding, again at re-validation and again at execution.
   If a newer deployment succeeds between the card and the approval, "previous" can name a different number than
   the card showed. Re-validation and execution agree within one approval, and the model can pass `number` to pin
   the target.
6. **No client IP** reaches audit entries written by tools or by services called from tools. `AgentToolContext`
   carries none, although `AgentActor` does.
7. **Planned `add_domain`** does not run the reserved-host check at validation. That check is private to
   `AppConfigurationService`. It runs at execution, so a plan can fail at that step after the app was created.
8. **The first-hour warning is a heuristic:** billing enabled and not the provider workspace. It does not price
   the size.
9. **Deployments list labelling:** rollbacks queued by the assistant show "Assistant" as their trigger (decision
   6.1).
