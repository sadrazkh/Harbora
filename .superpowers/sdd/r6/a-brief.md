# A — App operations out of the controller (phase 0a)

## Why

The AI agent (see the design doc) needs to create apps and change their env vars, domains and volumes.
Today all of that is written inline in `AppsController` actions, mixed with ModelState, TempData and
view models, so nothing but a form post can do it — and several of these mutations write no audit entry.
Your job is to move that logic into services that both the controller and (next wave) the agent's tools
call. Behaviour must not change: every existing test, HTTP tests included, keeps passing unchanged.

## Where the logic is today (verify; line numbers drift)

- `src/Harbora.Web/Controllers/AppsController.cs`
  - `Create` POST, about lines 231–502: name/slug rules (platform-wide unique slug), source-type rules
    (clone URL or image required), `AppRootDirectory.Validate`, cron expression/command, quota creation
    lock + `CanAddAppAsync` (+ `CanAddWorkloadsAsync(CronJobs:1)`), `projects.ResolveEnvironmentAsync` +
    `access.AllowsAsync(placement, Capabilities.AppsCreate)`, `scheduler.CheckAsync/PlaceAsync`, the
    "Custom" Git provider + repository row, `AppAddressAssigner.AssignAsync`, `ApplyTemplateAsync`
    (about :508), `CanAddGovernedResourcesAsync`, `ResourceCreationBilling.SaveAsync`
    (`CreationPaymentRequiredException`), then `QueueDeploymentAsync` when DeployNow.
    Input: `CreateAppViewModel` (`ViewModels/ViewModels.cs` about 85–138).
  - `AddEnv` (about :2128) / `DeleteEnv` (about :2153): upsert, secrets via `protector.Protect`.
  - `AddDomain` (about :2166) / `DeleteDomain` (about :2201): global uniqueness,
    `IsReservedHostAsync`, quota (Domains:1), primary re-pick on delete.
  - `AddVolume` (about :1083) / `RemoveVolume` (about :1144): `MountPath.Check`, quota (Volumes:1),
    `VolumeProtection`.

## Build

1. **`IAppProvisioningService`** (Infrastructure, beside the related app services; pick the folder that
   fits — e.g. `src/Harbora.Infrastructure/Apps/`):
   - an `AppCreateRequest` record carrying everything `CreateAppViewModel` carries that the logic needs
     (no MVC types), plus `WorkspaceId` and `UserId`;
   - `Task<IReadOnlyList<ServiceRefusal>> ValidateCreateAsync(AppCreateRequest, CancellationToken)` —
     **no side effects at all**: no rows, no locks held after return, no billing, no scheduling writes.
     It answers "would this be refused, and why" (slug format and availability, source rules, root
     directory, cron, environment exists and is accessible, quota, capacity check, template exists);
   - `Task<ServiceOutcome<AppCreateResult>> CreateAsync(AppCreateRequest, CancellationToken)` — re-runs
     the checks under the creation lock and performs the creation exactly as the controller does today.
     `AppCreateResult` carries at least the app id, slug, and the queued deployment id (or null).
2. **`IAppConfigurationService`** with a validate method beside each mutation (same rule: validate
   changes nothing):
   - set env var(s) (key, value, isSecret, availableAtBuild), delete env var;
   - add domain (host, ssl), remove domain;
   - add volume (mountPath), remove volume (with the protection rule).
   Each mutation checks the per-resource access itself (`ProjectAccessService.CanTouchAppAsync` with the
   capability the controller's policy uses — `AppsEnv` for env/domains, the one volume actions use
   today), so a caller that is not a controller cannot skip it. Workspace scoping follows the existing
   pattern (the request's workspace filter applies; do not use `IgnoreQueryFilters` for user-facing reads).
3. **Refusals** are `ServiceRefusal(Message, MessageFa, Field)` from `Harbora.Application.Abstractions`.
   Bring the Persian text the controller already shows; where the controller only had English, write
   the Persian too. Map `QuotaCheck.ReasonFa` through.
4. **Audit** inside the services, through `IAuditLogger` (pass `workspaceId` explicitly — it is never
   defaulted): `app.created`, `app.env_set`, `app.env_deleted` (never log a value — keys only),
   `app.domain_added`, `app.domain_removed`; keep the existing `app.volume_*` action names (move those
   calls into the service, do not duplicate them).
5. **The controller becomes thin**: map the view model to the request, call the service, map refusals to
   `ModelState`/TempData exactly as the user saw them before. Same redirects, same messages.
6. **Tests** (`tests/Harbora.Tests`): unit tests for each service on EF InMemory following the existing
   patterns (look at tests that construct services with an InMemory `HarboraDbContext`, a fixed clock and
   `PassthroughProtector`), covering: each refusal, validate-has-no-side-effects (row counts unchanged),
   create/set/add happy paths, the audit entries written (and that secret values never appear in audit
   metadata), access refusal for a user without the capability/project grant. Existing HTTP tests are
   the parity net — they must pass without edits; if one needs an edit, explain why in the report.

## Do not touch

`DatabasesController` and database services (agent B), `src/Harbora.Infrastructure/Agent/*` and
`Assistant/*` (agents C and D), `_Layout.cshtml`, `_Topbar.cshtml`, `Scripts/` (agent E). In
`DependencyInjection.cs`, add your registrations as one contiguous block with a comment line naming this
work, so parallel edits merge cleanly.

## Report

Write your full report to `.superpowers/sdd/r6/a-report.md` in your worktree and commit it: what moved
where, the public signatures (exact), refusals and their texts, audit actions, test files and counts,
the exact test commands and their output lines, anything left behind and why. Return only: status
(DONE / DONE_WITH_CONCERNS / BLOCKED), commit list, one-line test summary, concerns.
