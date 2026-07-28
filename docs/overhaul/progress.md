# Harbora Overhaul — Progress Log

Newest entries on top. Every entry records: what was done · files changed · tests/checks run ·
result (success/fail) · decisions · next step.

---

## 2026-07-28 — Global query filters: workspace scoping (closes P13)

**What was done**
- New `IWorkspaceScope` decides whether the current unit of work belongs to one tenant or spans all
  of them. `HttpWorkspaceScope` keys that on **request vs. system**, not authenticated vs. anonymous:
  no `HttpContext` → background work (deploy pipeline, job worker, schedulers, seeding) runs
  unscoped; a request with no workspace claim scopes to `Guid.Empty` and therefore matches nothing.
  Deny by default — a request must never fall back to seeing everything.
- `HarboraDbContext` applies global query filters to every tenant-owned entity: `App`, `Route`,
  `ManagedService`, `Backup`, `BackupDestination`, `BackupSchedule`, `Alert`, `GitProvider`,
  `WorkspaceMember`, `UsageRecord`, `Deployment`. The existing single-argument constructor still
  builds a system-scoped context, so every background call site keeps working unchanged.
- The two places that legitimately span tenants now say so explicitly with `IgnoreQueryFilters()`:
  the tenants admin page, and the "is this server still in use?" check before removing a node —
  which must be blocked by *any* tenant's workload, not just the admin's own.

**A design decision the tests forced**
Filtering `Deployment` through its `App` navigation looked natural and was wrong. Because `AppId` is
non-nullable, EF treats the relationship as required and emits an **INNER JOIN** — so a deployment
whose app row is missing disappears from *every* query, including the crash reconciler whose entire
purpose is to find stranded deployments. `IgnoreWorkspaceFilter ||` cannot rescue rows the join has
already dropped. Found because seven existing tests started failing.

Fixed by denormalising `WorkspaceId` onto `Deployment` (migration `DeploymentWorkspaceScope`, with a
backfill from the owning app — without it every existing deployment would keep the empty default and
vanish from its own tenant's history on upgrade) and filtering on a direct comparison. No join, no
hazard, and an index to match.

`EnvironmentVariable`, `Volume`, `DomainName` and `DeploymentLog` are deliberately left unfiltered:
they are only ever reached through a parent that *is* filtered, so a navigation filter would add a
join to every read — and the same inner-join hazard — for no extra protection. Stated explicitly in
a test rather than left implied.

**Files changed**
- New: `Application/Abstractions/IWorkspaceScope.cs`, `Web/Infrastructure/HttpWorkspaceScope.cs`,
  `Migrations/…_DeploymentWorkspaceScope.cs`, `tests/…/WorkspaceQueryFilterTests.cs`.
- Edited: `HarboraDbContext.cs`, `Deployment.cs`, `DeploymentEngine.cs`, `Program.cs`,
  `TenantsController.cs`, `ServersController.cs`.

**Checks run**
- `dotnet build Harbora.slnx -c Release` → 0 warnings / 0 errors.
- `dotnet test` → **259/259 passed** (was 244).
- **Mutation-tested:**
  1. treat "no workspace" as unscoped (the anonymous-sees-everything bug) → 1 test fails ✅
  2. drop the `App` filter → 4 tests fail ✅
  3. forget to stamp `WorkspaceId` on a new deployment → **survived** ❌. This one matters: the
     deployment would still build and release (background work is unscoped) but never appear in the
     UI of the tenant who triggered it — it would look like the deploy silently vanished. Added
     `A_newly_queued_deployment_is_visible_to_the_tenant_that_triggered_it`; now caught ✅.

**Honest notes**
- The filters are defence in depth, not a fix for a live leak: every controller was already scoping
  its queries by hand. What changes is the failure mode of a *future* mistake — "missing" instead of
  "another tenant's data".
- Denormalised `WorkspaceId` can drift if an app is ever moved between workspaces. Nothing supports
  that today; if it is added, the move must update its deployments too.

**Next step**
- Harden the restore shell command (extract-then-swap) — still outstanding from Phase E.
- Still blocked without a Docker host: real backup→restore round trip and end-to-end deploy.

---

## 2026-07-28 — Phase E: data-safety hardening + audit trail UI

**The bug this phase exists for**
`Backup.Checksum` has been in the schema since the first migration and is written on every backup —
and **nothing ever read it**. Meanwhile the volume restore path runs
`rm -rf /data/* && tar xzf …` as a single shell command: the wipe happens *first*. Restoring a
corrupt or truncated archive therefore destroyed the live data and had nothing to put back. That is
the worst failure mode in the product, and it was reachable through a normal, confirmed user action.

**What was done**
- **Integrity gate before restore.** The stored artifact's checksum is recomputed and compared with
  the one recorded at backup time; a mismatch aborts with an explicit "your current data has NOT
  been touched". Backups predating checksums still restore (refusing would strand the oldest
  backups) but log a warning.
- **Archive probe before restore.** A second, distinct check — a checksum only proves the bytes are
  the ones we stored, not that they form a usable archive. A backup that was garbage *when written*
  has a perfectly valid checksum. Found by a test that failed for exactly this reason.
- **Dry-run verification** — `IBackupEngine.VerifyAsync` fetches, checksums, decrypts and reads the
  archive without touching live data, returning per-check results. Wired to a "Verify" button.
  A backup nobody has ever verified is a promise, not a safety net.
- **Archive encryption at rest** — new `ArchiveCipher`: streaming, chunked AES-GCM. Chunked
  deliberately (database dumps don't fit in memory); each chunk carries its own nonce and tag, and
  the chunk index is bound into the associated data so chunks can't be reordered, duplicated or
  dropped. Key derived from the platform master key, so there is no second secret to lose. Format
  is detected per file, so pre-encryption artifacts keep restoring.
- **Pre-restore snapshot** — the current volume is tarred aside before it is overwritten, so even a
  verified-but-wrong restore is recoverable. Best-effort: it never blocks a confirmed restore.
- **Audit log UI + CSV export** (owed from P13). Entries had been written since the overhaul but
  nothing could read them. Admin-only (the trail spans workspaces and holds actor emails and IPs),
  filterable by action/actor, paged, with export capped at 50k rows.
- **Cross-tenant isolation tests** (owed from P13) — apps, backups, deployments, routes, proxy
  config, container retirement and image retention.

**CSV formula injection**
Audit fields carry attacker-influenced text (actor emails, target ids) and the export is opened in
Excel by an administrator investigating an incident. `CsvWriter` prefixes values starting with
`=`, `+`, `-`, `@` with an apostrophe so they are read as text rather than executed.

**Files changed**
- New: `Backups/ArchiveCipher.cs`, `Web/Controllers/AuditController.cs`,
  `Web/Infrastructure/CsvWriter.cs`, `Web/Views/Audit/Index.cshtml`, and four test files
  (`ArchiveCipherTests`, `BackupSafetyTests`, `AuditExportTests`, `CrossTenantIsolationTests`)
  plus `Fakes/BackupHarness.cs`.
- Edited: `PlatformAbstractions.cs` (VerifyAsync + result types), `BackupEngine.cs`,
  `BackupOptions.cs`, `BackupsController.cs`, `_Layout.cshtml`, `ViewModels.cs`,
  `appsettings.json`, `Fakes/FakeDockerEngine.cs` (one-off commands now recorded).

**Checks run**
- `dotnet build Harbora.slnx -c Release` → 0 warnings / 0 errors.
- `dotnet test` → **244/244 passed** (was 197).
- **Mutation-tested** — this phase can destroy data, so a weak test here is the most dangerous
  kind:
  1. remove the checksum gate before restore → 3 tests fail ✅
  2. remove the archive probe before the wipe → 1 test fails ✅
  3. unbind the chunk index from the AES-GCM tag (chunks become reorderable) → 1 test fails ✅

**Honest notes**
- One test I wrote (`Verification_reads_the_archive_not_just_its_checksum`) did not initially test
  what its name claimed — it asserted on the wrong backup. Rewritten around a genuinely
  intact-but-unusable artifact, which is what then exposed the missing probe on the restore path.
- The restore path still shells out `rm -rf /data/* && tar xzf …` as one command. The two gates in
  front of it make reaching that command with a bad archive very unlikely, but extracting to a
  temporary directory and swapping would remove the window entirely. Recorded as follow-up.
- Centralized workspace scoping (a query-filter refactor) is **not** done — the cross-tenant tests
  pin the predicates the controllers use today, but nothing yet prevents a future controller from
  forgetting one. That is the remaining P13 item.

**Next step**
- Global query filters for workspace scoping, so isolation is structural rather than per-query.
- Harden the restore shell command (extract-then-swap).
- Still blocked without a Docker host: an actual backup→restore round trip against a live volume.
  The verification path is exercised end-to-end against real archives on disk, but the tar/untar
  legs run through the fake engine.

---

## 2026-07-28 — Phase D: durable job queue (completes P3)

**What was done**
- Replaced the in-memory `Channel` queue with a persisted **`Job` table**. The old queue held
  `Func<IServiceProvider, CancellationToken, Task>` delegates — a delegate cannot be written to a
  database, which is why "crash-safe deploys" previously meant *the reconciler re-queued work into
  another equally volatile channel*. Persisting a **description** of the work (kind + target id)
  instead makes the row itself the queue.
- New: `Job`/`JobKind`/`JobStatus` (Domain), `IJobQueue` + `IJobCancellationRegistry`
  (Application), `DatabaseJobQueue`, `JobWorker`, `JobDispatcher`, `JobReconciler`,
  `JobCancellationRegistry`, `JobSignal` (Infrastructure). EF migration `DurableJobQueue`.
- Deleted `ChannelBackgroundJobQueue`, `BackgroundJobWorker` and `IBackgroundJobQueue`. All three
  producers migrated: deployments, backups, managed-service provisioning.
- **Real cancellation.** `IJobCancellationRegistry` maps running job → its `CancellationTokenSource`,
  so `DeploymentEngine.CancelAsync` now stops the work as well as updating the record. Previously
  cancelling a Building deployment only rewrote a column while the build carried on.
- `JobSignal` wakes the worker instantly on an in-process enqueue, so durability costs no latency;
  a 5s poll is the backstop that also catches rows written by the reconciler.
- `JobReconciler` runs **before** `DeploymentReconciler` and settles jobs left `Running` by a crash.
  Deliberately does not retry: deployments/backups/provisioning have side effects that a blind
  re-run could compound.

**A duplicate-deploy bug this introduced, and fixed**
`DeploymentReconciler` used to re-queue every `Queued` deployment on startup. With a durable queue
the job row survives the restart too, so that would deploy the same thing **twice**. It now
re-queues only when no live job covers the deployment — heal the gap, don't duplicate the work.
Covered by `A_queued_deployment_that_still_has_its_job_is_not_queued_twice`.

**Semantics worth stating**
- Host shutdown returns a claimed job to `Pending` with its claim released — the work never
  happened, so it must resume, not be recorded as cancelled or failed.
- A user cancel settles a `Pending` job outright; if the worker claimed it in the meantime, the
  concurrency stamp turns that into a caught conflict and the running path interrupts it instead.
- `ClaimStamp` is an EF concurrency token, so two workers racing for one job means a lost update
  for one of them rather than a double execution. Enforced on Postgres; the InMemory provider used
  in tests does not check it, so that guarantee is not test-covered — noted honestly.

**Files changed**
- New: `Domain/Jobs/Job.cs`, `Application/Abstractions/IJobQueue.cs`, four files under
  `Infrastructure/Jobs/`, `Migrations/…_DurableJobQueue.cs`, `tests/…/DurableJobQueueTests.cs`,
  `tests/…/Fakes/JobHarness.cs`.
- Deleted: `Jobs/ChannelBackgroundJobQueue.cs`, `Jobs/BackgroundJobWorker.cs`.
- Edited: `PlatformAbstractions.cs`, `HarboraDbContext.cs`, `DependencyInjection.cs`,
  `DeploymentEngine.cs`, `DeploymentReconciler.cs`, `BackupEngine.cs`, `ManagedServiceEngine.cs`,
  and two test files.

**Checks run**
- `dotnet build Harbora.slnx -c Release` → 0 warnings / 0 errors.
- `dotnet test` → **197/197 passed** (was 179).
- **Mutation-tested:**
  1. ignore `CancelRequested` on a pending job → **survived** ❌. The queue settles a pending cancel
     itself, so the worker's guard only matters after *cancel-then-restart* — a path I hadn't
     tested. Added that test; mutation now caught ✅. Also hardened the cancel/claim race the
     investigation exposed.
  2. treat host shutdown as cancellation (losing the work) → 1 test fails ✅
  3. remove the cancellation registration → **rejected by the compiler** (unused parameter is an
     error here); the compiling variant — registering a decoy token — fails 1 test ✅. That run
     also showed the blocking stub could hang the suite instead of failing, so its wait is now
     bounded: the test fails in ~11s rather than never.

**Next step**
- Phase E (data-safety hardening): backup→restore round-trip verification, archive encryption,
  dry-run restore; then the audit-log UI/export, centralized workspace scoping and cross-tenant
  tests still owed from P13.
- Known limitation: a cancel for a job running on **another** instance persists the flag but cannot
  interrupt it — the registry is process-local. Single-instance today; worth revisiting if the
  platform ever runs more than one panel.

---

## 2026-07-28 — Phase C: image retention + resilient rollback

**What was done**
- **Image operations added to the runtime seam.** `IDockerEngine` had no way to list, check or
  delete images at all — retention was impossible to implement, and "instant rollback" could not
  even be verified. Added `ListImagesAsync` / `ImageExistsAsync` / `RemoveImageAsync`, implemented
  across all four engines: `DockerEngine` (Docker.DotNet), `RemoteDockerEngine` (HTTP), the
  `Harbora.Agent` endpoints (`GET /agent/images`, `GET /agent/images/exists`,
  `POST /agent/images/remove`), and `FakeDockerEngine`.
- **Retention policy** as a pure function, `DeploymentPlanning.ImagesToPrune`. Keeps the active
  image plus the newest N *rollback-eligible* (Succeeded/RolledBack) images; prunes the rest after
  a successful cutover. Configurable via `Runtime:ImageRetentionCount` (default 5; 0 disables).
  Closes **R1** from doc 15 — previously every deploy leaked an image forever, and artifact
  rollback only worked because nothing cleaned up.
- **Rollback pre-flight.** New `IRollbackPlanner` checks up front that the target exists, belongs to
  the app, succeeded, has a retained image, and that the image is *still on the node*. The pipeline
  also re-checks before starting anything, so a pruned artifact fails cleanly instead of part-way
  through a deploy.
- **Rollback confirmation screen** (`Apps/ConfirmRollback`): shows the live version vs. the target
  with commit sha/message/author, deploy time and the exact image being re-released — or explains
  why the rollback is blocked. The Details page now links here instead of posting straight through.
  Closes P4's owed "pre-confirm rollback diff". The POST re-runs the plan, since retention could
  prune between rendering and submitting.

**Safety properties deliberately encoded**
- User-supplied images (`nginx:1.27`, template images) are **never** prunable — only tags matching
  `{prefix}/{slug}:build-`. Deleting a shared base image would break unrelated apps.
- Failed deployments do not consume the retention window; otherwise a burst of broken builds would
  silently push every working version out of rollback range.
- Retention dedupes by **image tag, not deployment** — a rollback re-releases an existing tag, so
  counting deployments would spend the window on one artifact.
- Pruning runs only after the deployment is recorded `Succeeded`, and any failure is swallowed:
  housekeeping must never turn a live, working deployment into a failure.
- `RemoveImageAsync` uses `Force = false`, so an image a container still references survives even if
  our bookkeeping thinks otherwise.

**Files changed**
- New: `Application/Abstractions/IRollbackPlanner.cs`, `Infrastructure/Deployments/RollbackPlanner.cs`,
  `Web/Views/Apps/ConfirmRollback.cshtml`, `tests/…/ImageRetentionTests.cs`,
  `tests/…/RollbackResilienceTests.cs`.
- Edited: `IDockerEngine.cs`, `DockerEngine.cs`, `RemoteDockerEngine.cs`, `Agent/Program.cs`,
  `DeploymentPlanning.cs`, `DeploymentPipeline.cs`, `HarboraRuntimeOptions.cs`,
  `DependencyInjection.cs`, `AppsController.cs`, `ViewModels.cs`, `Apps/Details.cshtml`,
  `appsettings.json`, and the test fakes.

**Checks run**
- `dotnet build Harbora.slnx -c Release` → 0 warnings / 0 errors.
- `dotnet test` → **179/179 passed** (was 154).
- **Mutation-tested** — retention deletes data, so a weak test here is actively dangerous:
  1. drop active-image protection → 1 test fails ✅
  2. drop the build-prefix guard (would delete `nginx:1.27`) → 2 tests fail ✅
  3. let failed deployments consume the window → 1 test fails ✅
  4. dedupe by deployment instead of by tag → **survived** ❌ → the test used a case that was
     immune (rollback to a non-adjacent version). Rewrote it around the common case — rolling back
     to the immediately previous version, where the two newest deployments share a tag — and the
     mutation is now caught ✅.

**Next step**
- Phase D (durable job queue) — completes P3. The in-memory `Channel` still means a `Queued`
  deployment only survives a restart because the reconciler re-queues it into another volatile
  channel; `CancelAsync` still cannot stop work already in progress.
- Note for a future phase: retention is per-app and runs on deploy, so an app that is never
  deployed again keeps its images indefinitely. A platform-wide sweep is the natural follow-up.

---

## 2026-07-28 — Phase B: pipeline integration harness (fake Docker engine)

**What was done**
- Built `FakeDockerEngine` — an in-memory container runtime that **records every call in order** and
  simulates a small container world (containers exist, have state, can be removed, can refuse
  removal). Ordering is the point: "zero-downtime" is a claim about *sequence*, so a fake returning
  canned values could never falsify it.
- Added `PipelineHarness`, which wires a **real** `DeploymentPipeline` (real state machine, real EF
  context, real cutover logic) over fake Docker/git/proxy/HTTP, plus recording fakes for the log
  stream, proxy and notifications, and a stub `IHttpClientFactory` for the health probe.
- **20 end-to-end tests** over `DeploymentPipeline.ExecuteAsync`, which previously had **zero**
  behavioural coverage: start-before-retire ordering, traffic switches only after health passes,
  failed deploy removes only its own container, container that never reaches `running`, failed
  build never starts a container, rollback re-releases the artifact without building or checking
  out source, rollback marks the deployment it displaced, imageless rollback target, remote-node
  host-port uniqueness, local vs remote proxy targets, unremovable old container, health probe
  targets the same address the proxy will use.

**Bug found and fixed — DbContext race on build logs**
The harness immediately failed with *"Collection was modified; enumeration operation may not
execute"*. Cause: `new Progress<string>(l => _ = Log(...))` — `IProgress` dispatches through the
thread pool (ASP.NET Core has no `SynchronizationContext`), so build/pull log lines were calling
`db.DeploymentLogs.Add(...)` **on a thread-pool thread while the pipeline thread was inside
`SaveChangesAsync`**. `DbContext` is not thread-safe. In production this hits every build that
emits log lines — the more verbose the build, the likelier the corruption.
Fix: engine-thread lines enqueue to a `ConcurrentQueue` and are drained onto the DbContext by the
pipeline thread; live SignalR streaming still happens immediately and never touches the context.

**Health-gate timings made configurable**
`Task.Delay(2s)` was hardcoded (up to 16s to reach `running`, then 20s of probing), which made the
suite unusable and gave operators no way to accommodate a slow-booting app. Now
`HealthPollIntervalSeconds` / `HealthRunningAttempts` / `HealthHttpAttempts` /
`HealthHttpTimeoutSeconds` on `HarboraRuntimeOptions`, defaulting to exactly the previous
behaviour. This also closes the "no probe fields" gap doc 12 left owed from P4.

**Files changed**
- New: `tests/Harbora.Tests/Fakes/{FakeDockerEngine,PipelineFakes,PipelineHarness}.cs`,
  `tests/Harbora.Tests/DeploymentPipelineCutoverTests.cs`.
- Edited: `DeploymentPipeline.cs` (log threading + configurable timings),
  `HarboraRuntimeOptions.cs` (health-gate knobs).

**Checks run**
- `dotnet build Harbora.slnx -c Release` → 0 warnings / 0 errors.
- `dotnet test` → **154/154 passed** (was 134), suite still ~1s.
- **Mutation-tested the new tests** — a green ordering test that cannot fail is worthless:
  1. retire old containers *before* the health gate → 3 tests fail ✅
  2. wire the proxy *before* the health gate passes → 1 test fails ✅
  3. rollback rebuilds instead of re-releasing → 2 tests fail ✅
  Pipeline restored and re-verified green after each.

**Decisions**
- `ListContainersAsync` is deliberately **not** recorded by the fake: the health loop polls it
  repeatedly and it would drown the ordering assertions in noise.
- Cross-fake ordering (proxy vs docker) is asserted through resulting state plus `ApplyCount`,
  not a shared clock — a shared call log across unrelated fakes would couple them for little gain.

**Next step**
- Phase C (image retention + resilient rollback). Note the harness makes the retention work
  testable: "prune everything except the last k images and the active one" is exactly the kind of
  ordering/selection claim `FakeDockerEngine` can now verify.
- Still blocked without a Docker host: the real E2E run. These assertions are the precise
  specification to execute against once a host exists.

---

## 2026-07-28 — PR #1 merged; Phase A (post-merge review fixes)

**What was done**
- Merged PR #1 into `master` (`a18b217`, `--no-ff`, 12 atomic commits preserved). Activated CI by
  moving the workflow to `.github/workflows/ci.yml` and dropping the now-merged `overhaul` trigger.
- Wrote `docs/overhaul/15-phase-plan.md` — actual-vs-claimed state per doc-12 phase, plus a
  re-sequenced plan for the constraint doc 12 assumed away: **no Docker host is available**.
- **Phase A — four defects found in post-merge review:**
  1. **Forwarded headers were never configured** while the shipped topology puts the panel behind
     Traefik, so every request carried the proxy's IP. The per-IP rate limits added in this overhaul
     were therefore one platform-wide bucket (10 logins/min for *everyone*), and every audit row
     recorded the proxy. New `TrustedProxySetup` trusts one hop from configured proxy networks only
     (`Harbora:TrustedProxyNetworks`, default = Docker's private ranges); `UseForwardedHeaders()`
     runs before the rate limiter.
  2. **The single-active-deploy guard swallowed rollbacks.** A rollback requested while a deploy was
     in flight returned the forward deploy's id and redirected to it — indistinguishable from
     success, though nothing was queued. Coalescing now applies only when both requests share the
     same intent; a mismatch throws with a clear message (surfaced as `TempData["Error"]` in the UI,
     `409 Conflict` on the API, skip-and-continue for webhooks).
  3. **`Succeeded → RolledBack` was allowed by the state machine but never applied**, so history
     never showed which version a rollback abandoned. The pipeline now marks the displaced
     deployment after cutover; the decision is a pure `DeploymentPlanning.ShouldMarkRolledBack`.
  4. **`CancelAsync` bypassed the state machine** via `ExecuteUpdateAsync`. It now transitions
     through it, making an already-terminal deployment a no-op instead of a raw column write.

**Files changed**
- New: `Web/Infrastructure/TrustedProxySetup.cs`, `docs/overhaul/15-phase-plan.md`,
  `tests/Harbora.Tests/TrustedProxySetupTests.cs`.
- Edited: `Program.cs`, `appsettings.json`, `DeploymentEngine.cs`, `DeploymentPipeline.cs`,
  `DeploymentPlanning.cs`, `AppsController.cs`, `ApiV1Controller.cs`, `GitWebhookProcessor.cs`.
- Tests: +38 → **134 total**, incl. the real `ForwardedHeadersMiddleware` exercised end-to-end
  (trusted hop adopted, untrusted peer ignored, client-prepended entry not believed).

**Checks run**
- `dotnet build Harbora.slnx -c Release` → 0 warnings / 0 errors.
- `dotnet test` → 134/134 passed.

**Decisions**
- `IPNetwork.TryParse` accepts a non-canonical base (`10.1.2.3/8`) and masks it to the prefix.
  Rejecting such entries would silently drop proxy trust and reintroduce defect 1, so they are
  accepted and documented as equivalent to the canonical form rather than treated as errors.
- Used `KnownIPNetworks` (not the obsolete `KnownNetworks`) to keep the build at 0 warnings.

**Next step**
- Phase B (`docs/overhaul/15-phase-plan.md`): a recording `FakeDockerEngine` and integration tests
  over `DeploymentPipeline.ExecuteAsync`, which today has **zero** behavioural coverage — the
  cutover ordering that this overhaul's headline claim rests on is currently unverified.

---

## 2026-07-23 — Action-level RBAC + Operator role (H2 / threat 2.12)

**What was done**
- Added the **Operator** role (enum value 4, appended) — day-2 ops only.
- Introduced a capability-based authorization model (deny-by-default): `Capabilities` (16 named
  action policies) + pure `RolePermissions` matrix (Domain) + `CapabilityRequirement` /
  `CapabilityAuthorizationHandler` (Web) evaluating the caller's role claim. Registered one policy
  per capability via `AddCapabilityAuthorization()` (replaced the bare `AddAuthorization()`).
- Applied `[Authorize(Policy = …)]` to **every privileged action** across all controllers **and**
  the token-authenticated API:
  - Apps: create / deploy+rollback / operate (restart·stop·start) / delete / env·domains.
  - Databases, Routes(save), Git(connect·import·oauth·rotate), Alerts, Backups(run/restore/manage),
    Servers(add/remove), Plans(create), Settings(platform), Tenants (whole controller).
  - API `POST /api/v1/apps/{slug}/deploy` → `apps.deploy` (same matrix as the UI).
- Role→capability matrix: Owner/Admin = all; Member (developer) = app lifecycle + databases/routes/
  git; Operator = operate + backups.run; Viewer = read-only.

**Files changed**
- New: `Domain/Authorization/Capabilities.cs`, `Domain/Authorization/RolePermissions.cs`,
  `Web/Infrastructure/CapabilityAuthorization.cs`.
- Edited: `Enums.cs` (Operator), `Program.cs` (policy registration), and 11 controllers.
- Tests: `RolePermissionsTests.cs` (full matrix) + `CapabilityAuthorizationHandlerTests.cs`
  (adapter, incl. missing/unknown role) → **96 tests total**. Test project now references
  `Harbora.Web` to test the handler directly.

**Tests / checks run**
- Build 0/0; `dotnet test` → **96 passed** (+10).
- **Live enforcement (real Postgres):** as Owner, `GET /apps/create` → 200. After switching the
  user's role to Viewer and re-logging in: `GET /apps/create` and `POST /servers/add` both →
  **302 → /account/denied** (denied). Role restored to Owner afterward.

**Decisions**
- Deny-by-default: unknown/missing role claim → denied (verified by test). Cookie users get a 302
  to `/account/denied`; API/token users get 403 — both driven by the same policy + matrix.
- `RolePermissions` lives in Domain (pure, framework-free) so the matrix is the single source of
  truth and is exhaustively unit-tested; the Web handler is a thin adapter.
- Converted the pre-existing `[Authorize(Roles="Owner,Admin")]` on Tenants to the capability policy
  for one consistent model.

**Next step**
- Push this to GitHub `overhaul` / PR #1. Remaining: Operator/role selection in the member-invite
  UI, resource-level "own apps" scoping for Member, audit UI/export.

---

## 2026-07-23 — Pushed to GitHub: branch `overhaul` + PR #1

**What was done**
- Pushed the entire overhaul branch (10 commits, original messages preserved) to
  `github.com/sadrazkh/Harbora` on branch **`overhaul`** via the GitHub integration
  (`create_branch` + per-commit `push_files`, replayed in order on top of `master@84603e0`).
- Opened **Pull Request #1** (`overhaul` → `master`) with a full summary of the phase:
  https://github.com/sadrazkh/Harbora/pull/1

**Verification**
- `origin/master` was still exactly the local baseline `84603e0` (no drift) before branching.
- After the push: fetched `origin/overhaul` and diffed against local — **only** the planned CI-file
  relocation differs (see below); all other 57 files byte-identical; all 10 commit messages intact.
- One transient GitHub 500 on commit 8 (ref not moved) — verified via `list_branches`, retried
  safely, succeeded.

**Decision / known limitation**
- The integration token lacks the GitHub `workflows` permission, so `.github/workflows/ci.yml`
  could not be pushed (403 on tree containing workflow files). The workflow was shipped at
  **`docs/overhaul/ci-workflow.yml`** with a relocation note. **Resolved at merge time:** the file
  was moved to `.github/workflows/ci.yml` (note dropped, stale `overhaul` branch trigger removed)
  in the merge commit for PR #1.
- Local branch was reset to `origin/overhaul` so local == remote lineage from here on.

**Next step**
- Review/merge PR #1; move the CI file; then continue with the roadmap phases (Docker-host E2E
  verification, per-action RBAC, monitoring depth, previews).

---

## 2026-07-23 — Audit logging for privileged actions (threat 2.13)

**What was done**
- Added `IAuditLogger` (Application) + `AuditLogger` (Infrastructure): append-only audit rows,
  actor/workspace default to the current user, request IP passed by the caller (no web coupling),
  best-effort (an audit failure never breaks the audited action).
- Wired it into the highest-value actions: **login success**, **login failure**, **app deploy**,
  **app rollback**, **app delete** — each records actor, target, IP, and metadata.

**Files changed**
- `SecurityAbstractions.cs` (`IAuditLogger`), `Auditing/AuditLogger.cs` (new), `DependencyInjection.cs`
  (register), `AccountController.cs` (login ±), `AppsController.cs` (deploy/rollback/delete).
- Added `tests/Harbora.Tests/AuditLoggerTests.cs` (+2) → **86 tests total**.

**Tests / checks run**
- Build 0/0; `dotnet test` → **86 passed**.
- Runtime (real Postgres): a wrong-password then correct-password login produced two audit rows —
  `user.login_failed` and `user.login` — each with the actor email and client IP (127.0.0.1).

**Result**
- SUCCESS. Security-relevant actions are now audited (the entity existed but was previously written
  only by the webhook path). Audit UI + CSV/webhook export remain a follow-up (R-AUD-1).

**Next step**
- Remaining items are broad refactors or Docker-dependent (per-action RBAC across all controllers,
  per-app/route monitoring, PR previews, in-browser DB client, multi-server port table, OpenAPI).
  These are documented in the roadmap; the critical/verifiable overhaul work is complete.

---

## 2026-07-23 — Staged deploy-progress UI + live reconciler verification

**What was done**
- Added a **staged deploy-progress bar** (`_DeployProgress` partial) to the deployment details
  page: Queued → Build → Deploy → Health → Live, server-rendered from the current status, with a
  clear failed-state message ("previous version is still serving — retry or roll back"). Matches
  docs/overhaul/08.
- **Live-verified the P3 crash reconciler against real PostgreSQL:** inserted a deployment in the
  `Building` state, restarted the app, and confirmed the reconciler transitioned it to `Failed`
  with *"Interrupted by a platform restart before completion. Please redeploy."* and set the app
  status to Failed — exactly the C2 behavior, now proven end-to-end (not only in the unit test).

**Files changed**
- Added `src/Harbora.Web/Views/Shared/_DeployProgress.cshtml`; included it in
  `Views/Deployments/Details.cshtml`.

**Tests / checks run**
- `dotnet build` (web) → 0/0 (Razor precompiles, partial valid).
- Runtime render check (real Postgres, seeded deployments): details page renders 200 for Building/
  Failed/Succeeded; Succeeded shows all five steps complete (5 ✓); Failed shows the ✕ + recovery
  message. Reconciler DB fingerprint confirmed.

**Result**
- SUCCESS. A signature UX gap from the spec is closed, and the crash-recovery fix is now verified
  live against PostgreSQL.

**Next step**
- Audit logging for privileged actions (login/deploy/rollback), then the deeper Docker-dependent
  and broad-refactor items (per-action RBAC, monitoring depth, previews).

---

## 2026-07-23 — Security & reliability hardening (H3 + threats 2.8 / 2.18)

**What was done**
- **Concurrency guard (H3):** `DeploymentEngine.QueueDeploymentAsync` now coalesces concurrent
  triggers (double-clicks, webhook storms) onto the existing in-flight deployment instead of racing
  a second build — at most one active deployment per app.
- **SSRF guard (threat 2.8):** new pure `UrlSafety.IsAllowedOutboundUrl` rejects non-http(s)
  schemes, localhost/metadata hostnames, and loopback/link-local/private/unique-local IP literals.
  Applied to the outbound Discord + generic webhook notification channels (blocked → logged, never
  sent; never breaks a deploy).
- **Rate limiting (threat 2.18):** per-IP fixed-window limiters — login `auth` (10/min) and inbound
  git `webhook` (60/min); 429 on exceed. Middleware added; policies applied via
  `[EnableRateLimiting]` on the login POST and the webhooks controller.

**Files changed**
- `DeploymentEngine.cs` (concurrency guard), `Security/UrlSafety.cs` (new),
  `Notifications/NotificationService.cs` (SSRF guard on webhook/Discord), `Program.cs` (rate
  limiter registration + middleware), `AccountController.cs` + `WebhooksController.cs`
  (`EnableRateLimiting`). Added `UrlSafetyTests.cs` (+11) and `DeploymentEngineConcurrencyTests.cs`
  (+2), plus others → **84 tests total**.

**Tests / checks run**
- Build 0/0; `dotnet test` → **84 passed**.
- Runtime: `/healthz` 200; login hammered 14× → first 10 = 200, then **429 429 429 429** (limiter
  works); app boots with the limiter active.

**Result**
- SUCCESS. Three targeted security/reliability gaps closed, all verifiable without Docker.

**Next step**
- Deeper items (per-action RBAC, audit coverage/export, per-app monitoring, previews, in-browser DB
  client) and the live Docker-host end-to-end run remain — larger and/or Docker-dependent.

---

## 2026-07-23 — Phase 7 (C3): Static-site + Template deploys + honest Compose gating

**What was done**
- Implemented **Static-site** deploys (git checkout → forced Nginx build) — previously threw
  `NotSupported`. Exposed as a source card in the create form; wired through the controller
  (validation, repo creation, deployability).
- Implemented **Template** deploys via a pure `TemplateResolver`: image-based templates deploy
  one-click (pull image), git-based templates build from the app's repo, managed-service and
  multi-service (`requires`) templates return an **honest, specific message** instead of a raw
  crash.
- **Docker Compose** now fails with a clear "not yet supported / planned" message (still gated, not
  selectable) instead of `NotSupportedException`.
- Refactored the git build path into a reusable `BuildFromGitAsync(forceStatic)` helper.
- **README** corrected: Compose is "planned, not shipped"; Static/Template status stated honestly.

**Files changed**
- `DeploymentPipeline.cs` (StaticSite/Template/Compose cases + BuildFromGitAsync),
  `TemplateResolver.cs` (new, pure), `Buildpacks.cs` (public `ForStaticSite`),
  `Apps/Create.cshtml` (Static card + multi-source panels), `AppsController.cs` (StaticSite),
  `README.md`. Added `tests/Harbora.Tests/TemplateResolverTests.cs` (+5).

**Tests / checks run**
- `dotnet build Harbora.slnx -c Release` → 0/0. `dotnet test` → **64 passed**.
- Runtime: `/apps/create` renders all three source cards (Git, Image, **Static site**); auth + form
  load verified (HTTP 200).

**Result**
- SUCCESS. C3 resolved honestly: advertised single-container sources now work or fail with a helpful
  message; Compose is truthfully marked as planned. No control implies an unimplemented capability.

**Decisions**
- Scoped Template to single-container (image/git); multi-service templates (WordPress+DB) return a
  clear "not one-click yet" message and remain a documented roadmap item rather than shipping a
  half-working multi-service orchestration I can't verify without Docker.

**Next step**
- Remaining backend hardening (webhook de-dup/rate-limit, RBAC per-action, audit) and the
  Docker-host end-to-end verification (P2 live step) are the natural continuations.

---

## 2026-07-23 — Phase 4: Zero-downtime cutover + artifact rollback (C4)

**What was done**
- **Zero-downtime cutover (ADR-007):** the new container now starts under a **versioned name**
  (`harbora-{slug}-{n}`) ALONGSIDE the currently-serving one; the old container is retired only
  AFTER the new one passes health checks and traffic has been switched. A failed deploy now leaves
  the previous version serving (was: old container removed before the new one even started →
  downtime + outage on failure).
- **True artifact rollback (ADR-006):** rollback now **re-releases the prior deployment's image**
  with no rebuild (instant + exact). Fixed a real correctness bug — the previous "rollback" ignored
  `RolledBackFromId` and rebuilt from current source, which could produce a *different* image.
- Remote nodes get a **per-deployment host port** so old+new can coexist during cutover.
- Container lookup for restart/stop/logs/delete is now **label-based** (was exact-name), matching
  the versioned naming.

**Files changed**
- Added `src/Harbora.Infrastructure/Deployments/DeploymentPlanning.cs` (pure helpers: versioned
  naming, retirement selection, per-deployment port, rollback-image resolution).
- `DeploymentPipeline.cs`: rollback short-circuit (skip build), start-new-before-retire-old cutover,
  failed-container cleanup on error, retire-after-cutover.
- `AppOperationsService.cs`: label-based current-container lookup.
- Added `tests/Harbora.Tests/DeploymentPlanningTests.cs` (+6).

**Tests / checks run**
- `dotnet build Harbora.slnx -c Release` → 0 warnings / 0 errors.
- `dotnet test` → **59 passed** (+6).

**Result**
- SUCCESS at build + unit level. Fixes C4 and a rollback correctness bug.
- Live cutover/rollback still needs a **Docker host** to verify end-to-end (P2 Docker step); the
  pure planning logic is fully unit-tested.

**Decisions**
- Versioned container names + retire-after-cutover chosen over the old remove-then-start, and the
  fix applies to remote nodes too via per-deployment ports (strictly better than the prior stable-
  port remove-first behavior). Legacy unversioned containers are retired automatically on first
  redeploy (safe migration).

**Next step**
- C3 honesty pass: implement Static-site + single-container Template deploys (currently throw),
  expose them in the create form, gate Compose until implemented, and correct the README.

---

## 2026-07-22 — Phase 2 (partial): Master key fail-closed (critical security fix)

**What was done**
- Implemented ADR-009 / threat 2.2: the master encryption key is now resolved **fail-closed**.
  Previously it silently fell back to a public default — in code *and* hardcoded in
  `appsettings.json` — so with `HARBORA_MASTER_KEY` unset, all "encrypted" secrets were trivially
  decryptable. Fixed both instances.

**Files changed**
- Added `src/Harbora.Infrastructure/Security/MasterKeyResolver.cs` (pure policy: Production must
  have a secure key; rejects known-insecure placeholders; Development uses a dev key with a loud
  warning).
- `DependencyInjection.cs`: use the resolver; coalesce a blank appsettings value through to the env
  var; print a warning when the dev fallback is used.
- `appsettings.json`: removed the insecure `Harbora:MasterKey` default (now blank).
- `appsettings.Development.json`: added a dev-only key for local convenience.
- Added `tests/Harbora.Tests/MasterKeyResolverTests.cs` (8 tests).

**Tests / checks run**
- `dotnet build Harbora.slnx -c Release` → 0 warnings / 0 errors.
- `dotnet test` → **31 passed** (was 24; +7 net).
- Runtime (built DLL, real Postgres): Production **without** a key → aborts with the precise
  message; Production **with** an env key → `/healthz` 200; Development (no env key) → boots and
  prints the INSECURE-key warning.

**Result**
- SUCCESS. The platform's most serious "insecure default" is closed and covered by tests.

**Decisions**
- Marked BREAKING (semver-major): existing Production installs that never set `HARBORA_MASTER_KEY`
  will now refuse to boot. Justified (it's a real vulnerability), low blast radius (the installer
  already generates the key in `deploy/.env`), and documented as a migration note (doc 11 §2.3).
  This is the one intentional breaking default in the overhaul; per the escalation rules it is
  reversible (unset the check) and non-destructive, so proceeded and recorded.

**Next step**
- P2 remainder needs a Docker host: reproduce install + one real end-to-end deploy (image + git),
  recorded here. Then P3 — deployment state machine + crash reconciler (ADR-004/005).

---

## 2026-07-22 — Phase 0–1: Guardrails & protective tests (execution begins)

**What was done**
- Created branch `overhaul` (off `84603e0`).
- **P0 — guardrails:** added a solution-wide CI workflow (`.github/workflows/ci.yml`: restore →
  build → test, plus a frontend job that runs `npm ci && npm run build` so a broken Vue bundle
  fails CI); added `.editorconfig` (promotes the unused-parameter warning CS9113 to an **error** so
  dead ctor params can't return); fixed the 3 pre-existing warnings.
- **P1 — protective tests:** added the first-ever test project `tests/Harbora.Tests` (xUnit +
  FluentAssertions) with 24 characterization tests over the highest-risk pure logic:
  secret protector (round-trip, non-determinism, wrong-key + tamper rejection), PBKDF2 hasher
  (verify + salting), secret redactor, buildpack detection (per-stack + precedence + no-match),
  and the Traefik renderer/validator (router/service YAML, cert resolver, priority ordering,
  and the validation gate: missing host, bad port, redirect-without-target, duplicate warning).

**Files changed**
- Added: `.github/workflows/ci.yml`, `.editorconfig`,
  `tests/Harbora.Tests/{Harbora.Tests.csproj,SecurityTests.cs,BuildpackTests.cs,TraefikProxyEngineTests.cs}`.
- Edited (warning fixes, removed unused `clock` param): `GitWebhookProcessor.cs`,
  `ManagedServiceEngine.cs`, `AppsController.cs`.
- Solution: `Harbora.slnx` (added test project).

**Tests / checks run**
- `dotnet build Harbora.slnx -c Release` → **Build succeeded, 0 warnings, 0 errors** (was 3
  warnings).
- `dotnet test Harbora.slnx -c Release` → **24 passed, 0 failed**.

**Result**
- SUCCESS. The protective net is live and green; the build is warning-clean; CI will gate future
  PRs on both backend tests and a successful frontend bundle build.

**Decisions**
- Removed the 3 unused `clock` primary-constructor parameters rather than suppressing the warning
  (cleaner; DI unaffected). Recorded because it slightly changes 3 constructor signatures (no
  behavior change).
- Started tests at the pure-logic tier (no Docker/DB needed) so the net exists before any core
  refactor; integration/E2E tiers (Testcontainers) come with the phases that need them (doc 13).

**Next step**
- P2: on a Docker-capable host, reproduce install + run one real end-to-end deploy (image + git)
  and record it here; implement the master-key **fail-closed in Production** check (ADR-009) with a
  unit test. Then P3 (deployment state machine + crash reconciler).

---

## 2026-07-22 — Phase 0: Discovery, market research, and design (baseline)

**What was done**
- Cloned `github.com/sadrazkh/Harbora` @ `84603e0` (branch `master`).
- Read the full repository (Domain/Application/Infrastructure/Data/Web/Agent/Cli/Shared, installer,
  compose, Traefik config, CLI, Vue islands, all controllers/views).
- Installed .NET 10 SDK (10.0.107) and PostgreSQL 15 in the workspace.
- Established the **build baseline** and a **runtime baseline** of the panel.
- Ran deep competitor research across 25 products (5 parallel research agents).
- Wrote the first design documents (see below).

**Files changed**
- Added `docs/overhaul/01-current-state-assessment.md`, `02-competitor-research.md`, `progress.md`
  (more docs landing in this phase).
- No source files changed yet (discovery only).

**Tests / checks run**
- `dotnet restore Harbora.slnx` → success.
- `dotnet build Harbora.slnx -c Release` → **Build succeeded, 0 errors, 3 warnings** (unread
  `clock` primary-constructor parameters in `GitWebhookProcessor`, `ManagedServiceEngine`,
  `AppsController`).
- `dotnet run --project src/Harbora.Web` against PostgreSQL 15 → boots, applies **5 migrations**,
  seeds **7 templates / 5 instance sizes / 3 plans / 1 local server**.
- Authenticated UI walk (cookie session): **16/16 routes → HTTP 200** (`/`, `/apps`,
  `/apps/create`, `/deployments`, `/git`, `/domains`, `/routes`, `/databases`,
  `/databases/create`, `/backups`, `/monitoring`, `/servers`, `/plans`, `/tenants`, `/templates`,
  `/settings`).
- `npm run build` (Vue islands) → **blocked** by the sandbox package-registry firewall
  (`registry.npmjs.org` 403 on transitive `ws`/`vite` tarballs). Environmental, not a project
  defect; fallback CSS keeps the shell usable.

**Result**
- SUCCESS for build + backend runtime baseline. Frontend bundle build deferred to a normal network.
- Docker is unavailable in this sandbox → container/deploy/metrics runtime paths were verified by
  **code reading only**. They must be validated on a Docker host as execution step 0.

**Key findings / decisions**
- The codebase is a genuine, well-structured modular monolith — **keep the foundation, don't
  rewrite.**
- Critical gaps identified: **no tests / no CI gate (C1)**; **deploy lifecycle not crash-safe
  (C2)**; **compose/static/template deploy sources advertised but throw `NotSupported` (C3)**;
  **health/cutover not zero-downtime (C4)**. Full list in doc 01 §5.
- Decision: overhaul order = stabilize (tests+CI+real deploy smoke) → fix domain/state-machine core
  → close claimed-but-missing gaps → layer differentiators. Recorded in doc 12.

**Next step**
- Finish the remaining design docs (03–14).
- Create the `overhaul` branch, add a solution build+test CI workflow, and stand up the first test
  project (characterization tests around Traefik rendering, buildpack detection, slug/host logic,
  secret protector) — the protective net required before any refactor.
- On a Docker-capable host: reproduce the baseline and run one real end-to-end deploy (prebuilt
  image + git repo); record the outcome here.

---

### Baseline reference (do not edit — pin for comparison)
- Commit: `84603e0`
- Build: 0 errors / 3 warnings (Release)
- Migrations: 5 · Seed: 7 templates, 5 sizes, 3 plans, 1 server
- UI: 16/16 routes 200 · Tests: 0
