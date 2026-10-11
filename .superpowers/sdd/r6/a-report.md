# A: app operations out of the controller (phase 0a): report

Branch `r6/app-services`, worktree `../Harbora-r6-a`, base `688cb40`.

**Status: DONE_WITH_CONCERNS.** The work is complete and the whole suite is green. The concerns are
listed at the end: test-file edits that will likely meet agent B's edits at merge, a few behaviour
changes I made on purpose, and some things the agent's tools must do themselves.

## Commits

- `133b5e1`: the services, the thin controller, DI registration, the edits to the census and
  constructor tests.
- `704b371`: unit tests for both services.
- (this report): the report commit.

## What moved where

All of this used to be inline in `src/Harbora.Web/Controllers/AppsController.cs`. The file went from
2385 to 1972 lines.

| Was (AppsController) | Now |
|---|---|
| `Create` POST body (slug, source, root dir, cron, quota lock, `CanAddAppAsync`, cron quota, `ResolveEnvironmentAsync` + `AllowsAsync(AppsCreate)`, scheduler, Custom Git provider + repository, `AppAddressAssigner`, template, governed quota, `ResourceCreationBilling.SaveAsync`, commit, deploy) | `AppProvisioningService.CreateAsync` / `ValidateCreateAsync` |
| `ApplyTemplateAsync`, `UniqueSlugAsync`, `Slugify`, `PlanPoolAsync`, `DeriveRepoName` | private in `AppProvisioningService` |
| `TakenAppSlugRefusal`, `TakenHostRefusal`, `ReservedHostRefusal`, `ExplainMount` | `AppRefusals` (internal static), with the same wording |
| `IsReservedHostAsync` | private in `AppConfigurationService` |
| `AddEnv` / `DeleteEnv` | `AppConfigurationService.SetEnvAsync` / `DeleteEnvAsync` |
| `AddDomain` / `DeleteDomain` | `AppConfigurationService.AddDomainAsync` / `RemoveDomainAsync` |
| `AddVolume` / `RemoveVolume` (and their `app.volume_*` audits, moved and not duplicated) | `AppConfigurationService.AddVolumeAsync` / `RemoveVolumeAsync` |

New files:
- `src/Harbora.Infrastructure/Apps/IAppProvisioningService.cs`: interface, `AppCreateRequest`,
  `AppCreateResult`, `AppNotice`.
- `src/Harbora.Infrastructure/Apps/AppProvisioningService.cs`
- `src/Harbora.Infrastructure/Apps/IAppConfigurationService.cs`: interface, `AppActor`, `AppItemRef`,
  `AppEnvVarInput`, and the result records.
- `src/Harbora.Infrastructure/Apps/AppConfigurationService.cs`
- `src/Harbora.Infrastructure/Apps/AppRefusals.cs`

DI (`DependencyInjection.cs`) is one contiguous block, placed right after the `AppAddressAssigner`
registration:

```csharp
// Round six, phase 0a (in-panel AI agent): creating an app and changing its env vars, domains
// and volumes, moved out of AppsController so the agent's tools call the same code the forms do.
services.AddScoped<Apps.IAppProvisioningService, Apps.AppProvisioningService>();
services.AddScoped<Apps.IAppConfigurationService, Apps.AppConfigurationService>();
```

What each controller action still does:
- It maps the view model or parameters to the service call.
- It keeps its own access check, because that check decides the HTTP answer: 404 for the
  env/domain-delete forms, 403 for AddDomain/AddVolume/RemoveVolume. `AppScopeCensusTests` also
  requires that check in each action body.
- RemoveVolume keeps a volume-exists check, so an unknown volume is still a 404.
- It maps refusals to `ModelState` (Create) or `TempData["Error"]` (the rest), using `IsFa ? MessageFa : Message`.

The controller lost two constructor parameters, `ISchedulerService scheduler` and
`IConfiguration config`. Nothing reads them now, and `.editorconfig` makes CS9113 an error. It gained
`IAppProvisioningService provisioning` and `IAppConfigurationService configuration`.

## Public signatures (exact)

```csharp
namespace Harbora.Infrastructure.Apps;

public sealed record AppCreateRequest
{
    public required Guid WorkspaceId { get; init; }
    public required Guid UserId { get; init; }
    public required string Name { get; init; }
    public string? Slug { get; init; }
    public AppSourceType SourceType { get; init; } = AppSourceType.GitRepository;
    public string? CloneUrl { get; init; }
    public string? GitRef { get; init; } = "main";
    public string? GitToken { get; init; }            // never printed by ToString()
    public string? DockerfilePath { get; init; } = "Dockerfile";
    public string? ComposeFilePath { get; init; }
    public string? RootDirectory { get; init; }
    public string? PrebuiltImage { get; init; }
    public int ContainerPort { get; init; } = 80;
    public string? Domain { get; init; }
    public Guid? TemplateId { get; init; }
    public Guid? EnvironmentId { get; init; }
    public ServiceKind Kind { get; init; } = ServiceKind.Web;
    public string? ReleaseCommand { get; init; }
    public string? CronExpression { get; init; }
    public bool PreviewsEnabled { get; init; }
    public string? Command { get; init; }
    public Guid? ServerId { get; init; }
    public string? InstanceSizeKey { get; init; }
    public bool DeployNow { get; init; } = true;
    public DeploymentTrigger Trigger { get; init; } = DeploymentTrigger.Manual;
    public string? ClientIp { get; init; }
}

public sealed record AppNotice(string Message, string MessageFa);

public sealed record AppCreateResult(
    Guid AppId, string Name, string Slug, string? Host, AppNotice? AddressNotice,
    string? TemplateAdvice, Guid? DeploymentId, ServiceRefusal? DeployRefusal);

public interface IAppProvisioningService
{
    Task<IReadOnlyList<ServiceRefusal>> ValidateCreateAsync(AppCreateRequest request, CancellationToken ct);
    Task<ServiceOutcome<AppCreateResult>> CreateAsync(AppCreateRequest request, CancellationToken ct);
}

public sealed record AppActor(Guid WorkspaceId, string? ClientIp = null);   // no user id: see "Fixes after review"

public sealed record AppItemRef(Guid? Id, string? Name)   // Id wins when both are set
{
    public static AppItemRef ById(Guid id);
    public static AppItemRef ByName(string name);          // env key / domain host / volume mount path
}

public sealed record AppEnvVarInput(string Key, string? Value, bool IsSecret = false, bool AvailableAtBuild = false); // ToString omits Value
public sealed record AppEnvVarChange(string Key, bool Created, bool IsSecret, bool AvailableAtBuild);
public sealed record AppDomainAdded(Guid DomainId, string Host, bool SslEnabled, bool IsPrimary);
public sealed record AppDomainRemoved(string Host, string? NewPrimaryHost);
public sealed record AppVolumeAdded(Guid VolumeId, string MountPath, string VolumeName);
public sealed record AppVolumeRemoved(string MountPath, string VolumeName, bool DataDeletionRequested, bool DataDeleted);

public interface IAppConfigurationService
{
    Task<IReadOnlyList<ServiceRefusal>> ValidateSetEnvAsync(AppActor actor, Guid appId, IReadOnlyList<AppEnvVarInput> variables, CancellationToken ct);
    Task<ServiceOutcome<IReadOnlyList<AppEnvVarChange>>> SetEnvAsync(AppActor actor, Guid appId, IReadOnlyList<AppEnvVarInput> variables, CancellationToken ct);
    Task<IReadOnlyList<ServiceRefusal>> ValidateDeleteEnvAsync(AppActor actor, Guid appId, AppItemRef variable, CancellationToken ct);
    Task<ServiceOutcome<string>> DeleteEnvAsync(AppActor actor, Guid appId, AppItemRef variable, CancellationToken ct);
    Task<IReadOnlyList<ServiceRefusal>> ValidateAddDomainAsync(AppActor actor, Guid appId, string? host, CancellationToken ct);
    Task<ServiceOutcome<AppDomainAdded>> AddDomainAsync(AppActor actor, Guid appId, string? host, bool ssl, CancellationToken ct);
    Task<IReadOnlyList<ServiceRefusal>> ValidateRemoveDomainAsync(AppActor actor, Guid appId, AppItemRef domain, CancellationToken ct);
    Task<ServiceOutcome<AppDomainRemoved>> RemoveDomainAsync(AppActor actor, Guid appId, AppItemRef domain, CancellationToken ct);
    Task<IReadOnlyList<ServiceRefusal>> ValidateAddVolumeAsync(AppActor actor, Guid appId, string? mountPath, CancellationToken ct);
    Task<ServiceOutcome<AppVolumeAdded>> AddVolumeAsync(AppActor actor, Guid appId, string? mountPath, CancellationToken ct);
    Task<IReadOnlyList<ServiceRefusal>> ValidateRemoveVolumeAsync(AppActor actor, Guid appId, AppItemRef volume, bool deleteData, CancellationToken ct);
    Task<ServiceOutcome<AppVolumeRemoved>> RemoveVolumeAsync(AppActor actor, Guid appId, AppItemRef volume, bool deleteData, CancellationToken ct);
}
```

Notes on the semantics:
- **Access is checked by the services themselves.**
  - Create: `ProjectAccessService.AllowsAsync(new ResourcePlacement(projectId, environmentId), Capabilities.AppsCreate)`.
    If the workspace has no environment yet, validate asks about `ResourcePlacement(null, null)`.
  - Every configuration method: `CanTouchAppAsync(appId, Capabilities.AppsEnv)`. AppsEnv is the
    capability all six controller policies use, the volume actions included.
  - Access is judged for `ICurrentUser`, the request's signed-in user. `AppCreateRequest.UserId` is what gets recorded
    as the deployment's `TriggeredByUserId`. `AppActor` has no user id: the access check and the audit actor both read `ICurrentUser`.
- **Workspace scoping.** Reads go through the request's tenant filter, plus `a.WorkspaceId == actor.WorkspaceId`.
  `IgnoreQueryFilters` appears only where the code already used it: the platform-wide slug check and
  the slug derivation.
- **Validate changes nothing.**
  - It never takes the creation lock.
  - It reads the environment without `EnsureDefaultEnvironmentAsync` (`PeekEnvironmentAsync`).
  - It asks the address and template questions of an `App` that is never attached to the context.
  - Config validations read `AsNoTracking`.
  - Stage two (address, governed quota) runs only if stage one passed, the same as create.
- **Validate does not check billing.** `ResourceCreationBilling` has only the saving method. An empty
  validate result means nothing was found to refuse, not that payment will succeed.
- **`EnvironmentId` fallback.** An id that is not one of this workspace's environments falls back to
  the default environment. That is `ProjectService.ResolveEnvironmentAsync`'s rule, kept for parity.
  Wave B's tool must resolve environment names itself and refuse unknown ones before calling.
- **When the payment gate refuses a create,** the service detaches only its own graph (app, repository,
  provider, domains, volumes, env vars, ledger lines for that app). It does not call
  `ChangeTracker.Clear()` the way the controller did, because the agent may be tracking its own plan
  in the same request. A test proves the caller's pending entity survives.
- **A config mutation stops at its first refusal,** as the controller actions did. Create collects
  every stage-one refusal, as the form did.

## Refusals (`ServiceRefusal(Message, MessageFa, Field)`)

The English is unchanged from the controller. "(new fa)" marks Persian I wrote because the controller
only had English.

| Field | English | Persian |
|---|---|---|
| — (config) | That app does not exist, or you are not allowed to change it. (new, service-only; the controller answers 404/403 first) | این اپ وجود ندارد یا اجازهٔ تغییر آن را ندارید. |
| Name | A name is required. | نام لازم است. (new fa) |
| Slug | `'{slug}' is already taken on this platform — app names are shared across every workspace…` | existing Persian (`… در Workspace شما چیزی با این نام نیست.`) |
| CloneUrl | A Git repository URL is required. | آدرس مخزن Git لازم است. (new fa) |
| PrebuiltImage | An image reference is required. | نام ایمیج (image) لازم است. (new fa) |
| RootDirectory | `AppRootDirectory.Explain(value, why)` | one Persian sentence per `PathRejection`, naming the value (new fa) |
| CronExpression | `CronSchedule.TryParse`'s error | `زمان‌بندی پذیرفته نشد: {error}` (new fa; carries the English detail) |
| Command | Enter the command this job should run, for example "php artisan backup:run". | فرمانی را که این کار باید اجرا کند بنویسید، مثلاً «php artisan backup:run». (new fa) |
| — | app quota: `check.Reason ?? "Plan quota exceeded."` | `ReasonFa ?? Reason ?? "سقف پلن پر شده است."` |
| — | cron quota: `Reason ?? "Scheduled-job quota exceeded."` | `ReasonFa ?? Reason ?? "سقف کارهای زمان‌بندی‌شده پر شده است."` |
| EnvironmentId | You do not have permission to create an app in this environment. | اجازهٔ ساختن اپ در این محیط را ندارید. (new fa) |
| — | capacity: `placement.Reason ?? "No server has capacity for this instance size."` | `ReasonFa ?? Reason ?? "هیچ سروری برای این اندازه جا ندارد."` |
| TemplateId | That template does not exist or is not available to this workspace. (new) | این قالب وجود ندارد یا برای این فضای کاری در دسترس نیست. |
| TemplateId | The template '{name}' cannot be used: its manifest does not parse. (new) | قالب «{name}» قابل استفاده نیست: مانیفست آن خوانده نمی‌شود. |
| Domain (create) / Host (add domain) | `'{host}' is one of the platform's own host names and cannot be routed to an app.` | existing Persian |
| Domain | `'{host}' is already in use.` | existing Persian |
| — | governed quota: `Reason ?? "Plan quota exceeded."` | `ReasonFa ?? Reason ?? "سقف پلن پر شده است."` |
| — | billing: `CreationPaymentRequiredException.Message` | `.ReasonFa` |
| — | default-project creation over the project cap: `QuotaRefusedException` | `ReasonFa ?? Message` (previously an unhandled 500) |
| Variables | Give at least one variable to set. (new) | دست‌کم یک متغیر برای تنظیم بدهید. |
| Key | Key is required. | نام متغیر (Key) لازم است. (new fa) |
| Key | That variable is not set on this app. (new) | این متغیر روی این اپ تنظیم نشده است. |
| Host | Host is required. | نام دامنه را بنویسید. (new fa) |
| Host | This domain is already in use. | این دامنه پیش‌تر استفاده شده است. (new fa) |
| Host | That domain is not attached to this app. (new) | این دامنه به این اپ وصل نیست. |
| MountPath | the five `ExplainMount` texts | existing Persian |
| MountPath | `{path} is already mounted.` | existing Persian |
| MountPath | That volume is not mounted on this app. (new) | این والیوم روی این اپ نیست. |
| MountPath | `VolumeProtectedException.Message` | `.ReasonFa` |

Deploy refusals after a create (`QuotaRefusedException`, `CapacityRefusedException`,
`LowDiskRefusedException`) do not refuse the create. They come back in `AppCreateResult.DeployRefusal`
as `(ex.Message, ex.ReasonFa ?? ex.Message)`. Address notices (discriminated, exhausted) come back in
`AddressNotice` with the controller's existing en/fa text. `TemplateAdvice` stays English, exactly as
`TemplateSetup.Advice` writes it.

## Audit actions

Every entry passes `workspaceId: request/actor.WorkspaceId` explicitly, plus the request's IP.

| Action | targetId | metadata |
|---|---|---|
| `app.created` (new) | `{app}:{slug}` | appId, name, slug, sourceType, kind, environmentId, serverId, instanceSize, templateId, host. Never the Git token |
| `app.env_set` (new, one per key) | `{app}:{KEY}` | key, secret, availableAtBuild, created. Never the value |
| `app.env_deleted` (new) | `{app}:{KEY}` | key, secret. Never the value |
| `app.domain_added` (new) | `{app}:{host}` | host, ssl, primary |
| `app.domain_removed` (new) | `{app}:{host}` | host, routesRemoved, newPrimary |
| `app.volume_added` (moved) | `{app}:{path}` | as before |
| `app.volume_deleted` / `app.volume_detached` (moved) | `{app}:{path}` | as before; follows what Docker actually did |

A removal that finds nothing writes no audit entry.

## Behaviour changes, all deliberate

1. **Deleting a variable or domain that is not there** (a second click, another tab, another app's id)
   used to show "Variable removed." / "Domain removed." with nothing removed. It now shows the
   not-found refusal in `TempData["Error"]`. This is the defect class the CONSTRAINTS forbid
   reproducing. No test covered the old behaviour.
2. **Refusals that only had English now show Persian on a Persian page**, as the brief asks. The four
   English-only success banners ("Variable saved…", "Variable removed…", "Domain added…",
   "Domain removed.") also got Persian, matching the existing "با استقرار بعدی…" phrasing. The English
   is unchanged.
3. **App-quota and capacity refusals on Create** used `check.Reason` and `placement.Reason` only. A
   Persian page now shows `ReasonFa` where the check carries one, and falls back to English where it
   does not.
4. **`TemplateId` on Create.** The form never posts this field. An id that does not exist used to be
   silently ignored; an unparseable manifest silently produced a plain app; another tenant's private
   template was applied. All three are now refused. The visibility rule is `TemplateCatalog.IsVisibleTo`,
   the same rule the form's own template list uses.
5. **A Create refused at a later stage no longer carries the address notice.** Previously, if the
   address was discriminated and then governance quota or billing refused, the "reachable at '…'"
   banner rode along on a form whose app was never created.
6. **A workspace with no environment, whose default project would cross the project cap,** used to
   throw an unhandled `QuotaRefusedException` out of Create. It now returns a form refusal.

## Existing tests edited (none of them HTTP tests), and why

The HTTP tests pass without edits. Six other test files needed edits:

- **`AppsControllerLogSearchTests.cs`, `Billing/QuotaRefusalBilingualismTests.cs`** construct
  `AppsController` by hand with named arguments. I removed the `scheduler:` and `config:` arguments,
  because those parameters no longer exist (CS9113 is an error here). I added `provisioning: null!` and
  `configuration: null!`; neither test calls the moved actions.
- **`WorkspaceGovernanceQuotaTests.cs`, `Billing/ResourceCreationBillingTests.cs`** are source-scanning
  censuses that list files which must contain `AcquireCreationLockAsync` / `creationBilling.SaveAsync`.
  `AppsController.cs` no longer contains either, so its entry was replaced by
  `AppProvisioningService.cs`, plus `AppConfigurationService.cs` for the lock census. That adds one
  theory case.
- **`AppScopeCensusTests.cs`** requires every resource-scoped action body to contain a scope check. The
  Create POST's `AllowsAsync` moved into the service along with the environment resolution it depends
  on. I added an `Exempt` entry for `AppsController.cs:Create(CreateAppViewModel model` with that
  reason. The other five actions keep their controller check, so they need no exemption.
- **`NodeChannelDeploymentTests.A_custom_domain_cannot_claim_a_host_the_platform_serves_itself`**
  sliced `AppsController.cs` for the reserved-host guard on AddDomain and on the create path. It now
  reads `AppConfigurationService.cs`: `ReservedHosts.IsReserved`; AddDomainAsync calls
  `CheckDomainAsync(` before `app.Domains.Add`; CheckDomainAsync calls `IsReservedHostAsync(`. It also
  reads `AppProvisioningService.cs`, which needs `ReservedHost` between the `AssignAsync` call and
  `return addressed;`.

## New tests

- `tests/Harbora.Tests/AppServiceHarness.cs` provides one workspace, project and environment and a
  member. It uses EF InMemory with this workspace's tenant filter, the real `QuotaService`,
  `ProjectService`, `ProjectAccessService`, `AppAddressAssigner` and `ResourceCreationBilling`,
  `PassthroughProtector`, and a fixed clock. It adds recording doubles for audit, scheduler and
  deployments, and `FakeDockerEngine`. `Snapshot()` counts every touched table plus pending tracked
  changes.
- `tests/Harbora.Tests/AppProvisioningServiceTests.cs`: 26 cases (18 facts, plus a theory over 8 form
  refusals).
- `tests/Harbora.Tests/AppConfigurationServiceTests.cs`: 21 cases (17 facts, plus a theory over 4
  domain refusals).

Together they cover:
- every refusal, in both languages and against its field, with validate and create or mutate agreeing;
- validate-has-no-side-effects, through snapshot equality, which includes tracked changes, the audit,
  and queued deployments;
- happy paths: an image app, a Git app (the token is encrypted and absent from the audit and from
  `ToString`), a template app, deploy queued with the request's trigger and user, deploy refused
  leaving the app, the default environment created by create but never by validate;
- env set/overwrite/delete by id and by key, domains with primary promotion and route removal, and
  volumes added, protected, deleted, and failing to delete;
- the audit entries, and that secret values never appear in them;
- access: Viewer, scoped member with no grant, scoped member with a grant, and another workspace's
  app, for every method.

Mutation check, run by hand and then reverted. Three mutations, each caught by the tests listed:

| Mutation | Tests that failed |
|---|---|
| Attach the app in `ValidateCreateAsync` | 2 validate tests |
| Replace `Forget` with `ChangeTracker.Clear()` | the billing test |
| Bypass the AppsEnv access check | the role/grant test |

4 tests failed in that run; after the revert and a rebuild, all 47 passed.

## Commands run and their output

```
export MSBUILDDISABLENODEREUSE=1
LOG=/tmp/test-r6-app-services.log
dotnet build Harbora.slnx --nologo -v q -p:UseSharedCompilation=false > "$LOG.build" 2>&1; echo "BUILD_EXIT=$?"
  BUILD_EXIT=0      (warnings: NU1900 x48, NU1903 x8 — identical to the baseline build before any change)
dotnet test Harbora.slnx --nologo -v q --no-build > "$LOG" 2>&1; echo "EXIT=$?"
  EXIT=0
  Skipped! - Failed:     0, Passed:     0, Skipped:   104, Total:   104, Duration: 55 ms - Harbora.Postgres.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:   500, Skipped:    17, Total:   517, Duration: 31 s - Harbora.NodeAgent.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:    15, Skipped:     0, Total:    15, Duration: 54 s - Harbora.NodeIngress.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:  6477, Skipped:     0, Total:  6477, Duration: 1 m 59 s - Harbora.Tests.dll (net10.0)
grep -c "Harbora.Tests.dll" "$LOG"   ->  2
```

6477 = 6429 baseline + 47 new cases + 1 new InlineData case in the lock census.

Focused runs while iterating:
- the existing HTTP and census tests (filter on AppCreate, AppAddress, AppDetailTabs,
  BuildArgCheckbox, StatusPageDomain, VolumeProtection, VolumeBackup, CapabilityPolicy,
  SupportRestriction, AppScopeCensus, NodeChannelDeployment, WorkspaceGovernanceQuota,
  ResourceCreationBilling, AppsControllerLogSearch, QuotaRefusalBilingualism, SizePicker,
  AppsListRedesign, CustomerDns, ReservedHosts, CrossTenant):
  `Passed! - Failed: 0, Passed: 241, Total: 241`
- the new tests: `Passed! - Failed: 0, Passed: 47, Total: 47`

Not verified locally: `Harbora.Postgres.Tests` all skip here (no Docker). I wrote no Postgres-lane
test. The creation-lock transaction behaviour on Postgres runs in the same order as before, but it has
not been exercised by me.

## Left behind, and why

- **`ApiV1Controller` and the CLI paths** still have their own app reads and env pull. They were out
  of scope; they are the next natural callers.
- **`SetVolumeProtection`, `Resize`, `SetReplicas` and the other app actions** were not in the brief
  and stay in the controller.
- **`PeekEnvironmentAsync` copies `ProjectService.ResolveEnvironmentAsync`'s two-query rule** without
  its creating fallback. I did not edit `ProjectService`, because agent B is changing it in parallel.
  A small follow-up could give it a read-only `FindEnvironmentAsync` that both use.
- **No slug-to-app resolver.** The services take app ids, and `AppItemRef.ByName` covers items. The
  agent's tools address apps by slug and must resolve the slug to an id themselves, among the apps the
  user can see.
- **The services know nothing about `AgentPlanScope`.** A "create app in the environment an earlier
  step creates" plan must be handled by the Wave-B tool: the environment does not exist yet, so the
  service would fall back to the default one.

## Concerns

1. **Merge overlap with agent B is likely, in test files only:**
   - the `InlineData` lists in `WorkspaceGovernanceQuotaTests` and `ResourceCreationBillingTests`. I
     replaced the line directly above `DatabasesController.cs`.
   - the `AppScopeCensusTests.Exempt` dictionary. My entry sits right after the AppsController GET
     entry.
   - `QuotaRefusalBilingualismTests`, where B's DatabasesController construction sits about 40 lines
     below mine.

   Each is a trivial resolution.
2. **`ServiceRefusal` has no kind or code.** A caller cannot tell "not found / not allowed" from "bad
   input". I kept the controllers' own 403/404 pre-checks for that reason, and the agent's tools will
   only have the text. This is not a contract change, just an observation for whoever extends the
   contract.
3. **The behaviour changes above.** Items 1, 2 and 4 are visible to users, though only in edge cases
   or in Persian. Revert them if the owner wants strict parity over honesty.

## Fixes after review

**Rebase.** I rebased onto master (`1585e37`, with agents B and C landed). The only conflicts were the
two source-scanning census lists. In each I kept both sides: B's `DatabaseProvisioningService.cs` line
replaced `DatabasesController.cs`, and my app-service lines replaced `AppsController.cs`.
- `ResourceCreationBillingTests`: `AppProvisioningService.cs` + `DatabaseProvisioningService.cs`.
- `WorkspaceGovernanceQuotaTests`: `AppProvisioningService.cs` + `AppConfigurationService.cs` +
  `DatabaseProvisioningService.cs`.

`AppScopeCensusTests`, `QuotaRefusalBilingualismTests` and `DependencyInjection.cs` merged without
conflicts. B made `ProjectService` take an optional `IAuditLogger` and write `project.created` itself.
My provisioning service gets `ProjectService` from DI, so a default project created during an app
create is now audited by `ProjectService`. My test harness builds it without an audit logger.

**1. Every check now runs inside the creation lock (the Important fix).**
- `AppConfigurationService.AddVolumeAsync` takes `AcquireCreationLockAsync` first, then runs
  `CheckVolumeAsync(track: true)` inside it. That check covers access, app, mount-path rules and the
  duplicate test against freshly read `app.Volumes`. The quota check follows. There is no unique index
  on a volume's mount path, so this ordering is now the only thing that stops a duplicate. No
  migration was added.
- `AddDomainAsync` works the same way with `CheckDomainAsync`, which does the platform-wide host test
  and the reserved-host test.
- `AppProvisioningService.CreateAsync` takes the lock before `CheckFormAsync` and `SizeAsync`, so
  slug availability is read inside it too.
- I reworded `IAppProvisioningService.CreateAsync`'s doc to promise exactly this. It also says
  plainly what a per-workspace lock cannot do: the app slug and a typed host are unique
  platform-wide, and another workspace can claim either in the same instant. The unique indexes
  refuse that second insert, and it surfaces as an exception, as it always did. `AddDomainAsync`'s
  comment says the same for hosts.

**2. Minor fixes.**
- **Default project over the plan's ceiling** (behaviour change #6). The refusal is now added to the
  list. The workspace checks (quota, access with a `(null, null)` placement, capacity, template) still
  run, and everything collected is returned. `ValidateCreateAsync` now asks the same question when
  the workspace has no environment: `CanAddGovernedResourcesAsync(Projects: 1, Environments: 1)`,
  placed where the create resolves the environment. So validate and create return identical lists.
- **`AppActor.UserId` removed.** The record is now `AppActor(Guid WorkspaceId, string? ClientIp = null)`.
  Its doc says the person is always `ICurrentUser`, which both the access check and the audit actor
  read. I did this so Wave B is not misled into thinking a caller can choose attribution.
  `AppCreateRequest.UserId` stays: it is read, as the first deployment's `TriggeredByUserId`. I also
  corrected the signature line earlier in this report.
- **`app.created` target** is now `{app.Name}:{app.Slug}`, the same `{app}:{what}` shape as the other
  entries. The app id moved into the metadata as `appId`. The audit table above is corrected.
- **New tests**, 6 cases in total:
  - `AppProvisioningServiceTests`:
    - `A_template_whose_manifest_does_not_parse_is_refused_by_name`
    - `The_plans_scheduled_job_ceiling_is_refused_in_both_languages`
    - `A_default_project_past_the_plans_ceiling_is_refused_alongside_every_other_reason` (asserts
      the form refusal survives and validate equals create)
    - `A_slug_another_workspace_takes_while_this_create_waits_for_the_lock_is_refused_not_duplicated`
  - `AppConfigurationServiceTests`:
    - `A_volume_another_request_adds_while_this_one_waits_for_the_lock_is_seen_and_refused`
    - `A_host_another_request_claims_while_this_one_waits_for_the_lock_is_refused_as_in_use`
- **How the three lock tests work.** The harness wraps the real `QuotaService` in `LockHookQuota`.
  Right after `AcquireCreationLockAsync` returns, a one-shot `OnCreationLock` hook commits a row through
  a second context over the same store, standing in for another request that committed while this one
  waited. A check made before the lock cannot see that row; a check inside the lock must.
- **Mutation check, reverted afterwards.** I put the checks back before the lock in all three methods,
  rebuilt (`BUILD_EXIT=0`) and reran the two service test classes. The result was
  `Failed! - Failed: 3, Passed: 50, Total: 53`, and the three failures were exactly the three lock
  tests. I then restored the files, touched them and rebuilt.
- I also updated the existing `app.created` test to the new target and the `appId` metadata.

**3. Behaviour changes the first report left out.** These are in addition to the six listed above.
- **(a) A model-binding error now also runs stage-two checks.** When the form arrives with a binding or
  annotation error, the controller calls `ValidateCreateAsync`. If stage one passes, that also runs
  the address check (Reserved/Taken) and the governance quota. The old action returned after stage
  one, so those errors can now appear alongside a binding error where they did not before. Nothing is
  created either way.
- **(b) A domain or volume quota refusal with a null `Reason`** now shows "Plan quota exceeded." (or
  the Persian fallback). Before, `TempData["Error"]` was set to null and no banner appeared.
  `QuotaService` documents `Reason` as never null when refused, so this is only reachable with another
  `IQuotaService`.
- **(c) `AddRefusals` drops a service refusal for a field that model binding already flagged.** This
  avoids a doubled "name is required". The side effect: a field with a binding error never shows the
  service's own refusal for it in the same post. For example, `Name` with both `[Required]` and a
  service refusal shows only the annotation message.

**Commands and output.**

The tests covering the change were `AppProvisioningServiceTests`, `AppConfigurationServiceTests`,
`AppCreate*HttpTests`, `AppAddress*`, `AppDetailTabsHttpTests`, `BuildArgCheckboxHttpTests`,
`StatusPageDomainHttpTests`, `VolumeProtectionHttpTests`, `VolumeBackupHttpTests`,
`AppScopeCensusTests`, `NodeChannelDeploymentTests`, `WorkspaceGovernanceQuotaTests`,
`ResourceCreationBillingTests`, `AppsControllerLogSearchTests`, `QuotaRefusalBilingualismTests` and
B's `DatabaseProvisioningServiceTests`, because of the shared census files:

```
dotnet test tests/Harbora.Tests --no-build --nologo -v q --filter "FullyQualifiedName~AppProvisioningServiceTests|FullyQualifiedName~AppConfigurationServiceTests"
  Passed!  - Failed:     0, Passed:    53, Skipped:     0, Total:    53 - Harbora.Tests.dll (net10.0)
dotnet test tests/Harbora.Tests --no-build --nologo -v q --filter "<the list above>"
  Passed!  - Failed:     0, Passed:   266, Skipped:     0, Total:   266 - Harbora.Tests.dll (net10.0)
```

Full suite, run with the CONSTRAINTS recipe:

```
export MSBUILDDISABLENODEREUSE=1
LOG=/tmp/test-r6-app-services.log
dotnet build Harbora.slnx --nologo -v q -p:UseSharedCompilation=false > "$LOG.build" 2>&1; echo "BUILD_EXIT=$?"
  BUILD_EXIT=0        (warnings: NU1900 x48, NU1903 x8 only — no CS warnings)
dotnet test Harbora.slnx --nologo -v q --no-build > "$LOG" 2>&1; echo "EXIT=$?"
  EXIT=0
  Skipped! - Failed:     0, Passed:     0, Skipped:   104, Total:   104 - Harbora.Postgres.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:   500, Skipped:    17, Total:   517 - Harbora.NodeAgent.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:    15, Skipped:     0, Total:    15 - Harbora.NodeIngress.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:  6690, Skipped:     0, Total:  6690 - Harbora.Tests.dll (net10.0)
grep -c "Harbora.Tests.dll" "$LOG"   ->  2
```

**How 6690 breaks down.** The 6690 is master's count plus 54 from this branch: 53 service cases (47
before review, 6 added now) and 1 extra census InlineData case. That puts master at 6636, which matches
6429 + 62 (B's report) + 145 (C's report). I did not run master separately.

`Harbora.Postgres.Tests` still all skip here because there is no Docker, so the Postgres advisory-lock
behaviour remains unverified locally.
