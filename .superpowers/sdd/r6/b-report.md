# B report — database operations out of the controller (phase 0b)

Branch `r6/db-services`, worktree `../Harbora-r6-b`. Status: **DONE_WITH_CONCERNS** (the concerns are
small and listed at the end; nothing is left unfinished).

## Commits

| sha | what |
|---|---|
| `9855a41` | the extraction: two services, refusals, audits, thin controller, ProjectService audits, tests |
| `e948173` | a test that both services are registered in DI (the controller's fallback would hide a missing one) |
| (last) | this report |

All authored as `sadra zadeh khameneh <1986.aandrii@gmail.com>`, no trailer.

## What moved where

| Was in | Now in |
|---|---|
| `DatabasesController.Create` POST (catalog/version check, creation lock, quota, environment resolve + placement access, container-name uniqueness, tier, scheduler, Redis policy, default logical database, first-hour billing, provision queue) | `DatabaseProvisioningService.CreateAsync` (+ `ValidateCreateAsync`) |
| `DatabasesController.Remove` structural refusals (apps attached, replicas) + `engine.RemoveAsync` | `DatabaseProvisioningService.ValidateRemoveAsync` / `RemoveAsync` — **removal lives on the provisioning service** (it is the database's lifecycle: create and remove; attach/detach is about the app relationship) |
| `DatabasesController.Attach` / `Detach` (access checks, replica refusal, `NetworkWiring.CanAttach`, logical-database resolution, duplicate check, alias, join row, `database.attached`/`database.detached` audits) | `DatabaseAttachmentService` |
| controller's private `NamedList` (used only by Remove) | `DatabaseProvisioningService.NamedList(names, isFa)` (both languages) |
| controller's private `Slugify` + regex | `DatabaseProvisioningService.Slugify`; the controller's `Slugify` (still used by `DatabasesController.Replicas.cs`) now delegates to it |

Files: `src/Harbora.Infrastructure/Services/DatabaseProvisioningService.cs`,
`DatabaseAttachmentService.cs`, `DatabaseRefusals.cs`; edits to `DatabasesController.cs`,
`Projects/ProjectService.cs`, `DependencyInjection.cs` (one contiguous block after
`LogicalDatabaseService`, commented "Round six, phase 0b").

The typed-name confirmation (`ServiceRemovalPlan.IsConfirmed`) stays in the controller, as the brief says.

## Public signatures (exact)

```csharp
namespace Harbora.Infrastructure.Services;

public sealed record DatabaseCreateRequest(
    Guid WorkspaceId, Guid UserId, string Name, ManagedServiceType Type,
    string? Version = null, Guid? EnvironmentId = null, string? InstanceSizeKey = null, Guid? ServerId = null,
    string? RedisEvictionPolicy = null, int? RedisMaxMemoryMb = null, string? IpAddress = null);

public sealed record DatabaseCreateResult(
    Guid ServiceId, string Name, string Slug, string ContainerName, ManagedServiceType Type,
    string Version, Guid EnvironmentId, Guid ServerId);

public sealed record DatabaseRemoveResult(Guid ServiceId, string Name, string VolumeName, bool DataDeleted);

public interface IDatabaseProvisioningService
{
    Task<IReadOnlyList<ServiceRefusal>> ValidateCreateAsync(DatabaseCreateRequest request, CancellationToken ct);
    Task<ServiceOutcome<DatabaseCreateResult>> CreateAsync(DatabaseCreateRequest request, CancellationToken ct);
    Task<IReadOnlyList<ServiceRefusal>> ValidateRemoveAsync(Guid serviceId, CancellationToken ct);
    Task<ServiceOutcome<DatabaseRemoveResult>> RemoveAsync(Guid serviceId, bool deleteData, string? ipAddress, CancellationToken ct);
}
// DatabaseProvisioningService also exposes: public const string ContainerPrefix = "harbora-svc-";
//   public static string Slugify(string name); public static string NamedList(IReadOnlyList<string> names, bool isFa);

public sealed record DatabaseAttachResult(
    Guid ServiceId, string ServiceName, Guid AppId, string AppName, Guid? DatabaseId, string Alias);
public sealed record DatabaseDetachResult(Guid ServiceId, Guid AppId, Guid? DatabaseId, string? Alias);

public interface IDatabaseAttachmentService
{
    Task<IReadOnlyList<ServiceRefusal>> ValidateAttachAsync(Guid serviceId, Guid appId, string? alias, Guid? databaseId, CancellationToken ct);
    Task<ServiceOutcome<DatabaseAttachResult>> AttachAsync(Guid serviceId, Guid appId, string? alias, Guid? databaseId, string? ipAddress, CancellationToken ct);
    Task<IReadOnlyList<ServiceRefusal>> ValidateDetachAsync(Guid serviceId, Guid appId, Guid? databaseId, CancellationToken ct);
    Task<ServiceOutcome<DatabaseDetachResult>> DetachAsync(Guid serviceId, Guid appId, Guid? databaseId, string? ipAddress, CancellationToken ct);
}

public static class DatabaseRefusalFields  // ServiceRefusal.Field values that are not form fields
{ Service = "ServiceId"; App = "AppId"; Database = "DatabaseId"; Environment = "EnvironmentId"; }

public static class DatabaseRefusals
{ public static readonly ServiceRefusal ServiceNotFound; public static readonly ServiceRefusal AppNotFound; }

// Harbora.Infrastructure.Projects
public sealed partial class ProjectService(
    HarboraDbContext db, ISystemClock clock, IQuotaService? quota = null, IAuditLogger? audit = null)
```

Both services are registered scoped (`AddScoped<IX, X>`).

`DetachAsync` takes `databaseId` although the brief listed `(serviceId, appId)`: the controller already
accepts it to disambiguate an app attached to two logical databases on one instance, so dropping it
would have changed behaviour. `ipAddress` is a parameter (or `DatabaseCreateRequest.IpAddress`) because
the controller's audit rows carried the client IP and the services have no HTTP context; the agent can
pass null.

## Refusals (Message / MessageFa / Field)

Create — the first group is collected together (as the form showed it), each later check stops at its own:

| When | English | Persian | Field |
|---|---|---|---|
| `WorkspaceId`/`UserId` not the signed-in caller's | This request names a workspace or user other than the one signed in. | این درخواست به فضای کاری یا کاربری غیر از کاربرِ واردشده اشاره می‌کند. | null |
| empty name (new; the form's `[Required]` used to be the only check) | A name is required. | نام لازم است. | `Name` |
| engine not in the effective catalogue | Unknown service type. | نوع سرویس ناشناخته است. | `Type` |
| version not offered | The selected database version is not supported. | نسخهٔ انتخاب‌شدهٔ دیتابیس پشتیبانی نمی‌شود. | `Version` |
| quota | `QuotaCheck.Reason` ?? "Plan quota exceeded." | `QuotaCheck.ReasonFa` ?? Reason ?? «سهمیهٔ پلن پر شده است.» | null |
| placement access | You do not have permission to create a database in this environment. | اجازهٔ ساختن دیتابیس در این محیط را ندارید. | `EnvironmentId` |
| container name taken | A service with this name already exists. | سرویسی با این نام از قبل وجود دارد. | `Name` |
| scheduler | `PlacementResult.Reason` ?? "No server has capacity for this database." | `ReasonFa` ?? Reason ?? «هیچ سروری برای این دیتابیس ظرفیت ندارد.» | null |
| Redis policy | `RedisMemoryPolicy.WhyRefused(…, isFa: false)` | the same with `isFa: true` | `RedisEvictionPolicy` |
| billing (create only; no read-only form exists) | `CreationPaymentRequiredException.Message` | `.ReasonFa` | null |

Remove (both structural refusals are returned when both apply; the controller shows the first, as before):

| When | English | Persian | Field |
|---|---|---|---|
| not there / not the caller's (`CanTouchServiceAsync(DatabasesManage)`) | That database does not exist, or you are not allowed to manage it. | این دیتابیس وجود ندارد یا اجازهٔ مدیریت آن را ندارید. | `ServiceId` |
| apps attached | This database is still attached to {NamedList}. Detach it from every app first, then delete it. | the controller's existing Persian | `ServiceId` |
| replicas | This database still has {NamedList} replicating from it. Delete the replica(s) first, then delete the primary. | the controller's existing Persian | `ServiceId` |
| row still present after `engine.RemoveAsync` (new) | {name} is still there after the removal; nothing was removed. | «{name}» پس از حذف هنوز وجود دارد؛ چیزی حذف نشد. | `ServiceId` |

Attach / detach (order is the controller's: database access, app access, app, database, replica, network, logical database, duplicate):

| When | English | Persian | Field |
|---|---|---|---|
| database access | `DatabaseRefusals.ServiceNotFound` (above) | | `ServiceId` |
| app access (`CanTouchAppAsync(AppsEnv)`) or app not in the workspace | That app does not exist, or you are not allowed to change its environment. | این اپ وجود ندارد یا اجازهٔ تغییر متغیرهای محیطی آن را ندارید. | `AppId` |
| replica | the controller's existing text, both languages | | `ServiceId` |
| different environments | `NetworkWiring.CanAttach` reason (English, unchanged) | این دو در محیط‌های متفاوتی هستند و هر محیط شبکهٔ خصوصی جداگانه‌ای است؛ اپ نام میزبانی می‌گیرد که نمی‌تواند آن را پیدا کند. (new) | `EnvironmentId` |
| logical database not this instance's (was a bare 404) | That logical database does not belong to this database instance. | این پایگاه‌دادهٔ منطقی متعلق به این نمونهٔ دیتابیس نیست. | `DatabaseId` |
| already attached | This database is already attached. | این دیتابیس از قبل به این اپ متصل است. | null |
| detach: not attached | This app is not attached to this database. | این اپ به این دیتابیس وصل نیست. | null |

## Audit actions

| Action | Where | target | workspaceId | metadata |
|---|---|---|---|---|
| `database.created` (new) | `CreateAsync`, after the quota commit, before the provision is queued | `service` / service id | request's | name, type, version, environment, server, size |
| `database.removed` (new; nothing was written before, so no existing name to keep) | `RemoveAsync`, only after the row is confirmed gone | `service` / service id | the row's | name, type, deleteData, volume when kept |
| `database.attached` | moved from the controller (not duplicated) | `service` / `{serviceId}:{appId}` | the service row's | — |
| `database.detached` | moved from the controller (not duplicated) | `service` / `{serviceId}:{appId}` | caller's | — |
| `project.created` (new) | `ProjectService.CreateAsync` (the saving path only; `PrepareAsync` writes nothing) | `project` / project id | passed | name, slug, environment |
| `environment.created` (new) | `ProjectService.AddEnvironmentAsync` | `environment` / environment id | passed | name, slug, project |

No caller audited project/environment creation before (`ProjectsController.Create`/`AddEnvironment`
wrote nothing), so there are no double entries. `project.created` is also written for the default
project `EnsureDefaultEnvironmentAsync` makes on demand (first visit, signup) — once, not per ask.
ProjectService has no request IP, so those two rows carry none.

## The controller

Same redirects, same messages for English readers, same ModelState keys. Notes on how:

- **Attach/Detach/Remove keep `Guard(id)` and `CanTouchAppAsync` up front**, and the services ask again.
  Two reasons: each has its own HTTP answer the service cannot give (the `UnauthorizedAccessException`
  path, a 404), and `AppScopeCensusTests` reads each gated action's body for a scope check.
- Refusals are told apart by `Field` (constants in `DatabaseRefusalFields`), never by text: in Attach,
  `DatabaseId` → `NotFound()` (as before), `EnvironmentId` → TempData + the database's own page (as
  before), anything else → `BackTo(returnUrl)`.
- Create: `ModelState` invalid at entry (the `[Required]` name) → `ValidateCreateAsync`'s refusals are
  added beside it, skipping the service's own copy of the name refusal so the summary does not list it
  twice; otherwise `CreateAsync`.
- The two services are **optional constructor parameters**, with a lazy fallback that builds them from
  the dependencies the controller was handed. Reason: four existing tests build `DatabasesController` by
  hand (one passes its first arguments by position), so adding required parameters or removing
  `scheduler`/`protector` (now unread except by the fallback) would have meant editing them. DI always
  supplies the registered services; `Both_database_services_are_registered…` asserts the registration,
  because the fallback would otherwise hide a missing one.

## Deliberate behaviour differences

1. **Persian readers now get Persian** for refusals the controller only ever showed in English: unknown
   type, version, quota (`ReasonFa`), environment permission, duplicate name, placement (`ReasonFa`),
   and the cross-environment attach refusal. English readers see identical text. This follows from the
   brief's "write Persian where it only had English"; say if the form should stay English.
2. A create post whose binding failed on something other than the name now also lists later-stage
   refusals (duplicate name, placement, Redis), because validate runs every stage; the old code stopped
   after the first group. Only reachable with a hand-made malformed post.
3. A removal the engine quietly did not carry out (its tenant-filtered read found nothing) now shows an
   error on the confirmation page instead of "{name} was removed." and is not audited.

## Tests

New (62 test cases, all in `Harbora.Tests`):

| File | Cases | Covers |
|---|---|---|
| `DatabaseProvisioningServiceTests.cs` | 39 | create happy path (row, default logical db, provision queued, lock committed, audit with IP and no secret), version casing, tier ceiling, Redis policy kept, no-environment workspace; validate changes nothing and takes no lock (acceptable, refused, and no-environment cases); every create refusal as a theory (8: nothing saved/billed/queued/audited, Persian ≠ English) plus one fact per refusal with exact texts; first-group collection; scoped member refused / allowed in their project; viewer; stranger request; billing refusal leaves nothing; remove happy path + audit; attached/replica refusals with exact texts and no side effects; viewer cannot remove (and "missing" reads the same); engine no-op not audited; DI registration; NamedList; Slugify |
| `DatabaseAttachmentServiceTests.cs` | 15 | attach row/alias/order/unpublished + audit; alias suffix; validate no side effects; viewer; scoped member refused on the app; other workspace's app; replica; different environments; foreign logical db; duplicate; engine with no logical db; detach + audit; detach by logical db; not attached + validate no side effects; viewer cannot detach |
| `ProjectServiceAuditTests.cs` | 4 | `project.created`, `environment.created`, `PrepareAsync` writes nothing, on-demand default audited once |
| `Http/DatabaseCreateHttpTests.cs` | 4 | nothing posted to `/databases/create` before: success redirects to the page that renders + `database.created` row with IP; duplicate re-renders in English; a version refusal reaches a Persian reader in Persian; empty name listed once |

Mutation check (applied together, then reverted with `git checkout`, then rebuilt for the full run):
skipping the attachment service's own database access check, letting `ValidateCreateAsync` resolve
(create) the default environment, and removing the controller's duplicate-name skip produced exactly
these failures: `Validating_in_a_workspace_with_no_environment_does_not_create_one`,
`A_viewer_cannot_detach`, `A_viewer_cannot_attach_and_is_not_told_whether_the_database_exists`,
`An_empty_name_is_said_once_not_twice` (`Failed! - Failed: 4, Passed: 54`).

Existing tests edited (no HTTP test was edited):

- `Billing/ResourceCreationBillingTests.cs` and `WorkspaceGovernanceQuotaTests.cs`: the source-scan
  `[InlineData]` naming `src/Harbora.Web/Controllers/DatabasesController.cs` now names
  `src/Harbora.Infrastructure/Services/DatabaseProvisioningService.cs` — they assert the file that
  creates databases contains `creationBilling.SaveAsync` / `AcquireCreationLockAsync`, and that code moved.
- `AppScopeCensusTests.cs`: an `Exempt` entry for `DatabasesController.cs:Create(CreateServiceViewModel model`
  with the reason: the placement check (`AllowsAsync`) moved into the service, where a body scan cannot
  see it; `DatabaseProvisioningServiceTests` proves the scoped-member refusal.

## Commands and output

Per CONSTRAINTS.md (`MSBUILDDISABLENODEREUSE=1`, `-p:UseSharedCompilation=false`, log named for the branch):

```
dotnet build Harbora.slnx --nologo -v q -p:UseSharedCompilation=false > /tmp/test-r6-db-services.log.build
BUILD_EXIT=0        (28 Warning(s), 0 Error(s) — all NU1900/NU1903, the same 28 as the baseline build before any change)
dotnet test Harbora.slnx --nologo -v q --no-build > /tmp/test-r6-db-services.log
EXIT=0
Passed!  - Failed:     0, Passed:   500, Skipped:    17, Total:   517 - Harbora.NodeAgent.Tests.dll
Passed!  - Failed:     0, Passed:    15, Skipped:     0, Total:    15 - Harbora.NodeIngress.Tests.dll
Passed!  - Failed:     0, Passed:  6491, Skipped:     0, Total:  6491 - Harbora.Tests.dll
Skipped! - Failed:     0, Passed:     0, Skipped:   104, Total:   104 - Harbora.Postgres.Tests.dll
grep -c "Harbora.Tests.dll" → 2
```

6491 = baseline 6429 + the 62 new cases. While iterating: the new classes (`Passed! 62`) and the related
existing ones — census, the two source scans, `AppManagedServicesHttpTests`, `LogicalDatabasesHttpTests`,
`ReplicasHttpTests`, `MeilisearchHttpTests`, `RedisMemoryHttpTests`, `ServiceRetryHttpTests`,
`ProjectServiceTests`, `QuotaRefusalBilingualismTests`, `ManagedServiceEngineTenancyTests`,
`DatabaseAccessPageTests`, `DependencyInjectionTests`, `ProjectDeleteHttpTests`,
`EnvironmentProtectionHttpTests` (`Passed! 147`).

**Unverified locally:** the Postgres-only behaviour — the advisory lock and the transaction rollback when
`CreateAsync` refuses after `ResolveEnvironmentAsync` made a default environment. The service holds and
disposes the reservation exactly as the controller did, but EF InMemory's reservation is a no-op, and no
Postgres-lane test was added.

## Leftovers and notes for wave B

- `ValidateCreateAsync` cannot ask billing (the first-hour prepayment has no read-only form), so a
  validated create can still be refused at execution with the billing refusal. The plan engine's
  re-validation at approval will not catch it either.
- The services judge access through `ICurrentUser`/`ProjectAccessService`, so they must run inside the
  user's own request (as the design says the agent does). `DatabaseCreateRequest.WorkspaceId`/`UserId`
  must equal the signed-in caller's or the request is refused.
- Validate methods take ids; a step naming a database an earlier plan step will create
  (`AgentPlanScope`) has to be handled by the tool itself.
- Not audited, out of this brief: stack deployment's `PrepareAsync` project (its caller owns the commit),
  `EnvironmentCloner` (the controller writes `environment.cloned`), and `PreviewEnvironmentService`'s
  preview environments.
- Agent A's app creation also calls `ResolveEnvironmentAsync`; a workspace with no environment now gets a
  `project.created` row from that path too.
- **Merge:** agent A probably edits the adjacent `AppsController.cs` `[InlineData]` line in
  `ResourceCreationBillingTests.cs` and `WorkspaceGovernanceQuotaTests.cs`; git will report a trivial
  conflict there (keep both lines' new values). My `DependencyInjection.cs` block sits right after the
  `LogicalDatabaseService` registration.

## Concerns

1. Deliberate difference 1 (Persian now shown where the form was English-only) is a user-visible change;
   it is what the brief asked for, but it is a change.
2. The optional-constructor fallback in `DatabasesController` keeps `scheduler` and `protector` in its
   constructor only to build that fallback. It saved editing four existing tests; if the owner prefers
   those tests edited instead, the parameters and the fallback can go.
3. Three existing tests were edited (two source-scan paths, one census exemption), as explained above.
