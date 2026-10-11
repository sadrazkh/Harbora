# B — Database operations out of the controller (phase 0b)

## Why

The AI agent (see the design doc) needs to create databases, attach them to apps, detach them and
remove them. Today that logic is inline in `DatabasesController` actions, so only a form post can do it,
and creating a database writes no audit entry. Move it into services that both the controller and (next
wave) the agent's tools call. Behaviour must not change: every existing test keeps passing unchanged.

## Where the logic is today (verify; line numbers drift)

- `src/Harbora.Web/Controllers/DatabasesController.cs`
  - `Create` POST, about lines 532–664: input `CreateServiceViewModel` (EnvironmentId, Name, Type,
    Version, InstanceSizeKey, ServerId, RedisEvictionPolicy, RedisMaxMemoryMb); type/version checked
    against `ServiceCatalogReader.EffectiveAsync`; quota creation lock + `CanAddServiceAsync`; placement
    access (`ResolveEnvironmentAsync` + `access.AllowsAsync`); container name `harbora-svc-{slug}` unique
    within the workspace; scheduler; `RedisMemoryPolicy.WhyRefused`; default `ManagedServiceDatabase`;
    `ResourceCreationBilling`; `engine.QueueProvisionAsync`.
  - `Attach` (about :1172–1263) / `Detach` (about :1272–1295): `CanTouchServiceAsync(DatabasesManage)` +
    `CanTouchAppAsync(appId, AppsEnv)`, replicas refused, `NetworkWiring.CanAttach` (same environment),
    logical DB resolution, duplicate check, `AppManagedServiceAlias.Resolve`, the `AppManagedService` row
    with `HasUnpublishedChanges`; audits `database.attached` / `database.detached`.
  - `Remove` (about :921): refusals (apps still attached, a replica exists) and the typed-name check
    (`ServiceRemovalPlan.IsConfirmed`), then `IManagedServiceEngine.RemoveAsync(id, deleteData)`.
- `src/Harbora.Infrastructure/Projects/ProjectService.cs`: `CreateAsync` and `AddEnvironmentAsync`
  write no audit entry.

## Build

1. **`IDatabaseProvisioningService`** (e.g. in `src/Harbora.Infrastructure/Services/`):
   - `DatabaseCreateRequest` (no MVC types; plus `WorkspaceId`, `UserId`);
   - `Task<IReadOnlyList<ServiceRefusal>> ValidateCreateAsync(DatabaseCreateRequest, CancellationToken)` —
     **no side effects**: no rows, no billing, no provisioning, no locks held after return;
   - `Task<ServiceOutcome<DatabaseCreateResult>> CreateAsync(...)` — re-checks under the creation lock and
     creates exactly as the controller does today; `DatabaseCreateResult` carries at least the service id,
     its name/slug and the container name.
2. **`IDatabaseAttachmentService`**: `ValidateAttachAsync` / `AttachAsync(serviceId, appId, alias?,
   databaseId?)` and `ValidateDetachAsync` / `DetachAsync(serviceId, appId)` — the access checks move in
   with the logic, so a non-controller caller cannot skip them.
3. **Removal**: `ValidateRemoveAsync(serviceId)` returning the structural refusals (attached apps,
   replicas — the things that make removal impossible, not the typed-name confirmation, which stays a
   caller concern) and `RemoveAsync(serviceId, deleteData)` that re-checks and removes. Put these on
   whichever of the two services fits best; say which in the report.
4. **Refusals** are `ServiceRefusal(Message, MessageFa, Field)` from `Harbora.Application.Abstractions`;
   bring the Persian the controller shows and write Persian where it only had English.
5. **Audit** inside the services (`IAuditLogger`, `workspaceId` passed explicitly): `database.created`,
   `database.removed` (check what the controller writes today and keep that name if it exists),
   `database.attached` / `database.detached` moved in (not duplicated). Also add `project.created` and
   `environment.created` to `ProjectService.CreateAsync` / `AddEnvironmentAsync` — then make sure the
   callers that already audit those (if any) do not now write two entries.
6. **The controller becomes thin**: same redirects, same messages, same ModelState errors.
7. **Tests**: unit tests per service on EF InMemory following the existing patterns — each refusal,
   validate-has-no-side-effects (row counts unchanged), happy paths, audit entries, an access refusal.
   Existing HTTP tests must pass without edits; if one needs an edit, explain why.

## Do not touch

`AppsController` and app services (agent A), `src/Harbora.Infrastructure/Agent/*` and `Assistant/*`
(agents C and D), `_Layout.cshtml`, `_Topbar.cshtml`, `Scripts/` (agent E). In `DependencyInjection.cs`,
add your registrations as one contiguous block with a comment naming this work.

## Report

Full report to `.superpowers/sdd/r6/b-report.md` in your worktree, committed: what moved where, exact
public signatures, refusals with texts, audit actions, test files and counts, exact commands and output
lines, leftovers. Return only: status, commit list, one-line test summary, concerns.
