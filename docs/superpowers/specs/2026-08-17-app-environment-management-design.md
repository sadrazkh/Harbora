# The transition that finished everywhere except in the column

**Date:** 2026-08-17 · **Status:** decomposition proposed; eight sub-projects, none ready for a plan
until the owner answers §7

Phase 3 of `docs/product-audit/17-implementation-roadmap.md` — backlog items **0023**, **0024**,
**0025**, **0026**, **0027**, **0028**, **0040**.

---

## 1. What exploring changed

The audit was written before Phases 1, 2, 6 and 9, before PAYG, and before ten sub-projects that
touched app management heavily. Eleven times in this programme a capability the plan assumed missing
already existed, most recently volume browsing and upload — which had shipped for months under
`AppDataController`, and was missed because the search was for the name the feature *would* have had
rather than for what it does. So the search here was by verb: *provision, fail, retry, rotate,
redeploy, queue, claim, preview, teardown, reserve, pre-flight*.

Five things came back that change the shape of the phase.

**First, and most important: 0023's danger is not the data.** The backfill the roadmap wants staged
already ran, on 2026-07-30, inside the migration that created the column
(`20260730220251_ProjectsAndEnvironments.cs:113-153`). Every one of the eight production paths that
creates an `App` or a `ManagedService` sets `EnvironmentId`. The expected count of NULL rows in
production is **zero**. What makes the migration dangerous is elsewhere, and §3 is about that.

**Second: dropping the dual-attach breaks backup, restore and password rotation.** Three code paths
reach a customer's database by running a one-off container on the *workspace* network —
`BackupEngine.cs:244` (dump), `:444` (restore), `ManagedServiceEngine.cs:361` (rotation) — plus
`DatabaseTargetStager.cs:98` in the backup module. The database container is reachable there only
because of the dual attach. Remove it in the wrong order and a database backup stops working, quietly,
as a connection refused inside a one-off. That is the single most consequential finding in this
document.

**Third: build args already ship, and nothing can turn them on.** `EnvironmentVariable.AvailableAtBuild`
(`EnvironmentVariable.cs:22`) flows through `DeploymentPipeline.cs:1194-1199` into
`DockerBuildRequest.BuildArgs`, into the local daemon (`DockerEngine.cs:34`) and across the wire to
the remote agent (`RemoteDockerEngine.cs:49` → `Agent/Program.cs:109`). The app page even renders a
`build` badge for it (`Details.cshtml:617`). The add-variable form has a `secret` checkbox and **no
build checkbox** (`Details.cshtml:625-631`), so the controller parameter always binds `false`. The
badge renders for a state the panel cannot produce, and no test anywhere mentions the flag. Item
0027's "docker build args not exposed" is half wrong in the more interesting direction: the feature is
built, the door is missing.

**Fourth: preview environments handle no pull-request event of any kind.** Not "partially" — the
webhook parser has no event switch at all. `WebhookRequest.EventName` is captured at
`WebhooksController.cs:29`, declared at `IGitWebhookProcessor.cs:19`, and read by nothing. A GitHub
`pull_request` payload has no top-level `ref`, so it exits at `GitWebhookProcessor.cs:45-46` with
HTTP 200 and `"Event ignored (no ref)."` §4 sets out which of the three GA conditions is real.

**Fifth: 0024's audit line is almost exactly right, which after eleven reversals is worth saying
out loud.** A provision failure writes `Status = Failed` and an English log line, the job row settles
`Succeeded`, and nothing else records anything. The one correction is that a retry *does* exist — it
is called "Rebuild container" and is not connected to the failed state.

---

## 2. What exists today

The most valuable section of this document. Cited so the plan does not re-derive it.

### 0023 — finish the environment model

| Part | State | Evidence |
|---|---|---|
| `Environment` entity, one default per project | **Built** | `Projects/Project.cs:32-54`; `IsDefault` written in exactly one place, `ProjectService.cs:115-123` |
| Every workspace gets a project + environment | **Built** | `ProjectService.EnsureDefaultEnvironmentAsync:29-51`, called from nine sites incl. `WorkspaceAccountService.cs:108` |
| The backfill the roadmap wants staged | **Already ran, 2026-07-30** | `20260730220251_ProjectsAndEnvironments.cs:139-145` (Apps) and `:147-153` (ManagedServices), idempotent SQL |
| Every creation path sets `EnvironmentId` | **Built, all eight** | `AppsController.cs:268`, `FunctionsController.cs:122`, `TemplateDeploymentService.cs:186` and `:143`, `PreviewEnvironmentService.cs:177`, `EnvironmentCloner.cs:236` and `:165`, `DatabasesController.cs:355` |
| The columns themselves | **Still nullable** | `App.cs:18-24`, `ManagedService.cs:13-15`; **71 migrations** now, none has ever made either required |
| The FK's delete behaviour | **Built, and it is a NULL producer** | `HarboraDbContext.cs:354-359` — `DeleteBehavior.SetNull` on both, deliberately, so deleting an environment detaches rather than destroys |
| The dual-attach | **Built** | `NetworkPlan.For:20-28`; both production callers hardcode `keepWorkspaceNetwork: true` (`DeploymentPipeline.cs:293`, `ManagedServiceEngine.cs:91`). `false` appears only in `NetworkPlanTests.cs:32` |
| Second network attached after create | **Built** | `DeploymentPipeline.cs:466-469` and `ManagedServiceEngine.cs:152-153`, both `networks.Skip(1)` |
| **Compose stacks dual-attach** | **Absent** | `DeploymentPipeline.cs:400-439` returns at `:438`, before the connect loop at `:468`. A stack joins the environment network only |
| **Cron dual-attaches** | **Absent, by shape** | `CronJobRunner.cs:141-143` computes `Primary` only |
| **Backup / restore / rotation use the environment network** | **Absent** | `BackupEngine.cs:244,444,955`, `ManagedServiceEngine.cs:361`, `DatabaseTargetStager.cs:98` — all `WorkspaceNetwork(slug)` directly |
| **A dry-run report of any kind** | **Absent** | No CLI verb (`Program.cs:18-27` — ten verbs, none), no admin page, nothing in `deploy/harbora` |
| Isolation tests | **Built** | `NetworkPlanTests.cs` (incl. `:60`, `:72` asserting the dual attach through a real pipeline), `EnvironmentNetworkTests.cs`, `NetworkWiringTests.cs`, `CrossTenantIsolationTests.cs` |
| **Postgres-lane coverage of the backfill SQL** | **Absent** | `tests/Harbora.Postgres.Tests/` has zero references to `EnvironmentId` or `Environments` |

### 0024 — surface managed-service provisioning failures

| Part | State | Evidence |
|---|---|---|
| A reason field on `ManagedService` | **Absent** | `ManagedService.cs` has no `Error`/`Reason`/`LastError`/`FailedAt`. The pattern exists five times elsewhere — `Deployment.ErrorMessage`, `CronRun.Error`, `Job.Error`, `Alert.LastError`, `NotificationDelivery.LastError` |
| What a failure persists | **`Failed`, and nothing else** | `ManagedServiceEngine.cs:163-173`; the billing-refusal path at `:60-72` carries a comment that *names this gap in as many words* |
| The job's own verdict | **`Succeeded`, by documented design** | `JobWorker.cs:236-247` — "a clean return is not proof the work finished"; the domain row is meant to carry the truth, and here it carries only a colour |
| The retry budget for provisioning | **Declared, unreachable** | `JobExecutionPolicy.cs:97` allows 3 attempts; `JobWorker.cs:265-276` only re-enqueues on a *thrown* retryable exception, and `ProvisionAsync` swallows every one. A transient pull timeout gets one attempt |
| An `AlertEvent` member for provisioning | **Absent** | `Enums.cs:227-247` — eight members, none of them this |
| `AlertIncident`, `IncidentService`, the timeline | **Built — Phase 6** | `Monitoring/AlertIncident.cs`, `IncidentService.cs`, migration `20260816183419_AlertIncidents`, rendered `Views/Monitoring/Index.cshtml:211-260` |
| Managed services participate in any of it | **Absent** | `ManagedServiceEngine`'s constructor (`:27-35`) holds neither `INotificationService` nor `IncidentService`. Every other raiser holds both |
| A failed database on the dashboard | **Absent** | `Dashboard/Attention.cs:63-94` has arms for deployments, crashes, backups, channels, certificates — none for services |
| A retry action | **Built, and mislabelled** | `DatabasesController.Reprovision:512-530`, button `Views/Databases/Details.cshtml:260-263` reading "Rebuild container", gated on `CanManage` and never on `Failed` |
| What the screen says | **A red pill with the raw enum** | `Views/Databases/_Shell.cshtml:59` and `Views/Databases/Index.cshtml:101` render `@Model.Status` — untranslated, in a view where every neighbouring string has an `isFa` branch |

### 0025 — rotation offers batch redeploy

| Part | State | Evidence |
|---|---|---|
| The rotation itself refuses safely | **Built and careful** | `ManagedServiceEngine.cs:350-377` — unsupported engines refused, unsafe passwords refused, and nothing is stored if the database rejects the change |
| The env-var sweep | **Built** | `:386-405`, matching by variable **key** across every app in the workspace |
| **Prefixed keys are rotated** | **Absent — a live data hazard** | `AttachKeys.For:53-75` always writes a `PREFIX_` set; a second attached database lives *only* under `MYDB_DATABASE_URL`. `attachEnv` holds unprefixed names only, so rotating a second-attached database leaves that app on a dead password. `Detach` gets this right at `DatabasesController.cs:727`; `Rotate` does not |
| **Another database's variables are left alone** | **Absent — a live data hazard** | The sweep never checks the current value. An app holding a *different* PostgreSQL under the unprefixed `DATABASE_URL` has it overwritten with this database's credentials. `DatabasesController.cs:731-738` guards exactly this for detach, and says why |
| An App↔ManagedService link entity | **Absent by design** | Attachment is inferred from decrypted values by `ServiceUsage.Mentions` (`ServiceUsageService.cs:46-47`) everywhere except rotation, which uses the weaker key-only rule |
| A redeploy after rotation | **Absent** | `DatabasesController.Rotate:613-636` does not hold `IDeploymentEngine`. `IManagedServiceEngine.cs:28-32` *promises* the behaviour in its doc comment |
| A prompt listing what will be affected | **Absent** | A browser `confirm()` before the fact (`Views/Databases/Details.cshtml:255`). `ConfirmRemove` exists precisely because that pattern was judged not good enough for destructive work |
| A batch-redeploy helper anywhere | **Absent** | All fourteen `QueueDeploymentAsync` call sites are single-app. Batch *provision* loops exist (`EnvironmentCloner.cs:382`), and `DeploymentEngine.cs:101` already coalesces per app, so a loop would be safe |
| Tests for `RotatePasswordAsync` | **Absent** | `CredentialRotationTests.cs` covers the pure plan only; the engine method has zero coverage |

### 0026 — `/activity` and long-operation toasts

| Part | State | Evidence |
|---|---|---|
| A durable job table | **Built** | `Jobs/Job.cs:60-126` — kind, target, status, attempts, started, finished, error, cancel-requested, next-attempt, claimed-by, concurrency stamp. Twelve kinds (`:6-40`), eleven enqueue sites |
| The queue-position rule | **Built, and already kind-generic** | `QueuePosition.cs` — `Noun()` at `:265-277` has bilingual labels for nine job kinds. One caller uses it |
| Cancel | **Built generically, wired for one kind** | `IJobQueue.RequestCancellationAsync(kind, targetId)`; the only caller is `DeploymentEngine.cs:123`. UI `DeploymentsController.cs:151`, API `ApiV1Controller.cs:232`, CLI `Commands.cs:449` |
| **Any page that lists jobs** | **Absent** | `db.Jobs` is read in two controllers: one deployment's position, and a `HasLiveJob` boolean on billing runs. No `.cshtml` binds `Job`, `JobKind` or `JobStatus`. There is no `/activity` route |
| A notification inbox, bell badge, preference matrix | **Built — Phase 9** | `NotificationsController.cs`, `UserNotification.cs:43-63`, `UnreadNotificationsViewComponent.cs:28-41`, preferences `:123-148` |
| **A notification that could point at a job** | **Absent** | `UserNotification` has no URL, no event type, no target reference. And `AlertEvent` is failure-only — nothing emits anything when work *starts* or *succeeds* |
| A timeline, a delivery log, an audit list | **Built** | `Views/Monitoring/Index.cshtml:211-260` and `:470-500`; `AuditController.cs:44-98`. These are the pagination and filter idiom `/activity` should copy |
| A toast component | **Absent — there is not one** | Zero matches for `toast` in `src/`. `_Layout.cshtml` renders no `TempData`; **278 assignments across 33 controllers** are rendered ad hoc in 45 views |
| A real-time channel | **Built, deployment-scoped** | SignalR at `Program.cs:89,267`; groups are `deployment:{id}` (`DeploymentHub.cs:21`). No user- or workspace-scoped group exists. A 1.5 s polling fallback is proven (`DeploymentLogs.vue:68-74`) |
| **A workspace column on `Job`** | **Absent, deliberately** | `HarboraDbContext.cs:984-992` lists jobs among the intentionally unfiltered platform tables. `DeploymentsController.cs:98-102` explains why its own read is platform-wide |
| Operations with no job row at all | **Four** | disk cleanup (`MonitoringController.cs:43-77`), environment clone (`ProjectsController.cs:208-240`), database measure (`DatabasesController.cs:603-611`), bucket measure (`StorageController.cs:225-242`) — all awaited inline in the request |
| Operations that *have* a job row and still say nothing | **Six** | `BackupCenterController.cs:170,266,304` and `BackupsController.cs:79` — "queued. It runs in the background." That is the defect exactly: the row exists, is cancellable in principle, and the message is a dead end |

### 0027 — deploy retry and build args

| Part | State | Evidence |
|---|---|---|
| A retry action on a failed deployment | **Absent** | `DeploymentsController` has four POSTs: cancel, promote, assistant, — no retry. The only "Retry" button in the panel is `Views/BillingRuns/Index.cshtml:82-86` |
| The UI promising one | **Built** | `_DeployProgress.cshtml:53-54` — "Deploy failed … You can retry or roll back." The retry it means is the Deploy button on a different page |
| What redeploy reuses today | **Nothing** | `AppsController.cs:1170-1202` carries `app.GitRef` — a branch name. `DeploymentEngine.cs:72-86` leaves `CommitSha` and `ImageTag` null. It re-clones the branch tip as it is *now* |
| A config snapshot on the row | **Built, and not replayable** | `Deployment.ConfigJson` via `DeploymentConfig.cs:29-61` — for answering "what changed?". Secrets are stored as an HMAC fingerprint (`:57,77-83`), so a literal replay is impossible by construction |
| Automatic retry | **Built, and excludes deployments on purpose** | `JobExecutionPolicy.cs:97` — `JobKind.Deployment => 1`, with a comment explaining that a half-applied deploy must not be re-run blind. `IsRetryable` (`:123-146`) is therefore never consulted for a deploy |
| Rollback and promote | **Built** | `AppsController.cs:1221-1258`; `DeploymentsController.cs:234-237` |
| **Build args, end to end** | **Built** | `EnvironmentVariable.cs:22` → `DeploymentPipeline.cs:1194-1199` → `IDockerEngine.cs:119-123` → `DockerEngine.cs:29-35` (typed `ImageBuildParameters.BuildArgs`) and `RemoteDockerEngine.cs:49` → `Agent/Program.cs:109-112` |
| **A control that sets the flag** | **Absent** | `Views/Apps/Details.cshtml:625-631` — `key`, `value`, `isSecret`. No API or CLI surface either |
| Compose-service build args | **Absent** | `DeploymentPipeline.cs:924-925` passes `new Dictionary<string, string>()` |
| Any test of the build-arg path | **Absent** | Zero matches for `BuildArgs`, `AvailableAtBuild` or `X-Build-Args` under `tests/` |

### 0028 — capacity re-check, port burn, disk pre-flight

| Part | State | Evidence |
|---|---|---|
| Host-port allocation | **Built and careful** | `HostPortAllocator.cs:74-107` — lowest-free over `HostPortRange` 20000–29999, unique `(server, port)` index as the authority, insert-loses-and-retries. Idempotent per `(server, app, deployment)` at `:40-50` so a retried deploy takes no second port |
| **Awareness of a foreign process on the port** | **Absent** | The taken set is read from `HostPortAllocations` only. Nothing consults the OS or the node. Because lowest-free is deterministic, the *next* deploy picks the same blocked port again |
| A burn / exclusion list | **Absent** | No `IsBurned`, no excluded-ports table |
| The burn-and-advance pattern | **Built, on the other half of the pair** | `NodeIngressRegistry.cs:82-140` — try the preferred port, catch `SocketException`, advance. Exactly the shape R-23 asks for, applied panel-side |
| Node capacity at create | **Built** | `ISchedulerService` called from `AppsController.cs:255`, `DatabasesController.cs:344`, `FunctionsController.cs:114`, `EnvironmentCloner.cs:130` |
| **Capacity re-checked at queue or deploy** | **Absent** | `DeploymentPipeline.cs` never mentions the scheduler. `SchedulerService.CheckAsync` — the "does this still fit" method — has one caller, and it is a database create |
| The PAYG start gate | **Built, and it *is* re-checked every deploy** | `DeploymentPipeline.cs:197-231`, with a comment explaining that eleven queue sites is eleven chances to forget, so the check lives here. It covers money and workspace lifecycle — not capacity, not plan caps, not disk |
| Plan quota on redeploy | **Built on one path only** | `AppsController.cs:1180` re-checks; the API, webhook, preview, template, update-version and update-tag paths do not |
| **A free-disk gate** | **Absent** | `DiskCleanupService.FreeDiskAsync:218-229` can read it and uses it only to measure a cleanup's effect. `MetricsCollector.cs:448-464` warns at 85% after the fact, with no deploy consequence |
| Failure naming | **Built for health, absent for the rest** | `HealthDiagnosis.cs:4-21` is a real enum with real prose. Build, pull and bind failures reach the row as raw `ex.Message` (`DeploymentPipeline.cs:608-611`) |

### 0040 — PR preview environments

| Part | State | Evidence |
|---|---|---|
| What a preview is | **Built** | A new `App` **and** a new `Environment` in the parent's project — a hand copy, not `EnvironmentCloner` (`PreviewEnvironmentService.cs:177-200`) |
| Create / refresh on push | **Built** | `EnsureAsync:39-99`, reached only from `GitWebhookProcessor`. There is no button, no CLI verb, no API route |
| Naming | **Built** | `PreviewNaming` — `{app}-{branch}-{sha256(branch)[..6]}.{root}`, ≤ 63 chars, hash never trimmed (`:30-57`) |
| **`pr-{n}` naming, a PR number, any PR field** | **Absent** | The four preview fields are `PreviewsEnabled`, `PreviewOfAppId`, `PreviewBranch`, `PreviewLastPushedAt` (`App.cs:39-50`). The domain model cannot represent a pull request |
| **Webhook PR events** | **Absent entirely** | No event switch exists. `EventName` is written at `WebhooksController.cs:29` and read nowhere. `TryParse:166-194` requires a top-level string `ref`; a PR payload has none and exits at `:45-46` with HTTP 200 |
| Signature verification per provider | **Built** | `GitWebhookProcessor.Verify:144-159` — GitLab token, GitHub and Gitea HMAC. Bitbucket is not supported at all |
| A preview gets its own address | **Built — sub-project B1** | `PreviewEnvironmentService.cs:206-212` now goes through the shared `AppAddressAssigner` (`3b57fc2`), with a `Derived` origin so a collision is discriminated rather than refused (`55fc6d8`) |
| The URL on the preview's own page | **Built** | `Views/Apps/Details.cshtml:155-173`, plus a "this is a preview" banner at `:46-59` |
| **The URL on the parent's preview list** | **Absent** | `Details.cshtml:595-600` renders branch and last-push date. The one thing worth clicking is not there |
| **Commenting the URL back on the PR** | **Absent** | `IGitProviderClient` has exactly one method, `ListRepositoriesAsync`. No status-check, no comment client |
| Teardown by idle sweep | **Built** | `PreviewSweeper.cs` — 6-hour tick, 10-minute startup delay, per-item catch; `PreviewPolicy.IdleLifetime = 7 days` |
| Teardown by branch delete | **Built, one provider shape** | `GitWebhookProcessor.cs:188` reads a top-level `deleted: true` with no provider branching |
| **Teardown on merge or PR close** | **Absent** | Nothing anywhere reacts to a merge |
| The delete itself | **Built, and it refuses to lie** | `RemoveAsync:105-125` re-reads and throws if the row survives; `AppOperationsService.DeleteAsync` also drops the now-empty preview environment (`:139-150`) |
| **Any test of the lifecycle** | **Absent** | `PreviewEnvironmentTests.cs` covers naming and policy — pure functions only. `EnsureAsync`, `RemoveAsync`, `ExpiredAsync`, `SweepAsync` and `HandlePreviewAsync` have **zero** coverage. `GitWebhookScopeTests.cs:75,125` construct the processor with `previews: null!` |
| Quota refusals are visible | **Absent** | `PreviewEnvironmentService.cs:67,88` log at Information and return null; the webhook answers "Queued 0 deployment(s)" |

---

## 3. Item 0023, which the roadmap calls the biggest of the roadmap

This is the section a reader will most need to trust, because it runs against real customer data.

### How many apps have a NULL `EnvironmentId`?

**Almost certainly zero — and "almost certainly" is the entire problem.**

The derivation, which is solid: the migration that introduced the column also backfilled it, in four
idempotent SQL blocks (`20260730220251_ProjectsAndEnvironments.cs:113-153`) that create a default
project per workspace, a `production` environment per project, and then

```sql
UPDATE "Apps" a SET "EnvironmentId" = e."Id" FROM "Environments" e
WHERE e."WorkspaceId" = a."WorkspaceId" AND e."Slug" = 'production' AND a."EnvironmentId" IS NULL;
```

with the `ManagedServices` twin beneath it. Every workload that existed on 2026-07-30 was placed.
Since then, all eight creation paths set the column (§2). So the only way a NULL exists today is a
row that was detached *after* the backfill, by `DeleteBehavior.SetNull`
(`HarboraDbContext.cs:354-359`) firing on one of two delete paths:
`ProjectsController.cs:266` (guarded at `:254-264`, but the guard is application-level) or
`AppOperationsService.cs:148` (only removes an environment that is already empty).

The derivation is not a measurement, and this machine cannot make one: it holds no credential for the
live server. The only recorded figure for production scale is three apps, in
`2026-08-14-app-address-guarantee-design.md:166`, three days old.

**So the number is not the risk, and pretending otherwise would be the wrong reassurance.** A
zero-row backfill is a migration that succeeds instantly and proves nothing. The reason the roadmap's
dry-run report still has to exist is not to size the work — it is to convert "almost certainly zero"
into a number an operator has read on the real database before an enforcing migration is written. If
it comes back non-zero, that is a bug report about a delete path, not a backfill task.

### What the dual-attach actually is

`NetworkPlan.For` (`NetworkPlan.cs:20-28`) is nine lines and has three outcomes:

| Input | Result |
|---|---|
| no environment network | `[workspace]` |
| environment network, `keepWorkspaceNetwork: true` | `[environment, workspace]` ← **the dual attach** |
| environment network, `keepWorkspaceNetwork: false` | `[environment]` |

The third row is never produced in production. Both call sites hardcode `true`
(`DeploymentPipeline.cs:293`, `ManagedServiceEngine.cs:91`); `false` appears once, in a unit test. The
first element is the network the container is *created* on and the proxy is pointed at; the rest are
attached afterwards with `ConnectNetworkAsync` (`DeploymentPipeline.cs:466-469`).

So the workspace network is not a legacy leftover that nothing uses. It is load-bearing for four
things, and this is the part the roadmap line "drop dual-attach" does not say:

- **a database dump** — `BackupEngine.cs:244` runs the dump one-off with
  `NetworkMode: _runtime.WorkspaceNetwork(wsSlug)`
- **a database restore** — `:444`, and the restore rehearsal at `:955`
- **a password rotation** — `ManagedServiceEngine.cs:361`, inconsistently with its own neighbour
  `TestConnectionAsync:304`, which correctly uses `NetworkPlan.Primary`
- **the backup module's database stager** — `DatabaseTargetStager.cs:98`

Each of those reaches a customer's database *only* because the database container is also on the
workspace network. Remove the dual attach without moving them and every managed-database backup,
every restore and every rotation fails — as a connection refused inside a short-lived container, which
is the quietest failure this platform can produce.

Two smaller asymmetries, recorded so the plan does not discover them late: **compose stacks are
already single-attached** to the environment network (`DeploymentPipeline.cs:400-439` returns before
the connect loop), and **cron jobs never dual-attach** (`CronJobRunner.cs:141-143` uses `Primary`
only). Both are already living in the world the migration is trying to reach, which is mild evidence
that the destination works.

### What would break if enforcement landed before backfill

Nothing, because the backfill already ran. That question, asked of this codebase, has a more useful
form: **what breaks if enforcement lands at all?**

1. **`DeleteBehavior.SetNull` becomes illegal.** A required column cannot be set to null by a cascade.
   Today, deleting an environment quietly detaches its apps — a deliberate choice with a comment
   saying so. The same migration must change that behaviour, and choosing what it becomes is §7 Q2.
   Leaving it is not an option: it converts a documented silent detach into an unhandled FK violation
   on a delete path the panel still offers.
2. **Nine null branches become dead code**, and one of them is a live behaviour: `NetworkPlan.cs:24-25`
   and `:38`, `DeploymentPipeline.cs:670` and `:708`, `CronJobRunner.cs:196`,
   `ManagedServiceEngine.cs:194`, `NetworkWiring.cs:32-33`, `PreviewEnvironmentService.cs:41`,
   `AppsController.cs:525-534`. `PreviewEnvironmentService.cs:41` is the interesting one — it silently
   disables previews for an unplaced app, which after enforcement is a condition that cannot arise.
3. **Five tests assert that the null case is legal** and must be deleted or inverted:
   `ProjectModelTests.cs:70-83` (`An_app_without_an_environment_is_still_valid`),
   `NetworkPlanTests.cs:38`, `NetworkWiringTests.cs:38`, `CronRunnerTests.cs:328-357`
   (whose fixture says `// app.EnvironmentId is deliberately left null.`),
   `ProjectAccessServiceTests.cs:239`.
4. **Roughly 98 `new App { … }` literals across 49 test files** omit the column. In-memory EF enforces
   a required scalar FK, so this is a mechanical but wide edit — and it is the part most likely to be
   mistaken for the work itself.
5. **`SetupController.cs:51-58` creates the first workspace with no project and no environment**,
   relying on a later lazy `EnsureDefaultEnvironmentAsync`. That stays legal after enforcement — a
   workspace may have no environment; only a *workload* may not — but it is worth a test, because it
   is the one place the invariant is established late.

**Then, separately, the network half.** Making the column required changes no networking at all:
every workload already has an environment, so `NetworkPlan.For` already returns two names. Dropping
the second name is a distinct change with a distinct blast radius, which is why §6 makes it a distinct
sub-project with a forced predecessor.

---

## 4. Do preview environments work today?

GA means three things, per the roadmap. One is real, one is half real, one does not exist.

**(a) Webhook PR events — no, and not partially.** There is no event dispatch of any kind. The only
discriminator is payload shape: a top-level string `ref`. A GitHub `pull_request`, a GitLab
`Merge Request Hook` and a Gitea `pull_request` all lack it, so all three are answered
`{"message":"Event ignored (no ref).","deploymentsQueued":0}` — HTTP 200, nothing logged. The field a
PR implementation needs is already captured and already threaded through the DTO
(`WebhooksController.cs:29` → `IGitWebhookProcessor.cs:19`) and read by nothing, which makes the
wiring look finished from the controller's side. Open question Q5 in
`docs/product-audit/18-open-questions.md:17-18` is still open and this confirms why.

**(b) URL surfacing — half.** A preview does get a real, unique, collision-checked `DomainName`
through the shared assigner that sub-project B1 built, and the preview's own page shows it as a
clickable, copyable link with a banner explaining what it is. What is missing is both ends of the
journey: the **parent's** preview list shows branch and date but not the URL
(`Details.cshtml:595-600`), and there is no way to put the URL where a reviewer would see it, because
`IGitProviderClient` can only list repositories. The competitor research this feature came from names
the PR comment explicitly (`docs/overhaul/02-competitor-research.md:266`); nothing implements it.

**(c) Teardown — the code is real, the proof is absent.** Two triggers work: a 7-day idle sweep and a
GitHub-shaped `deleted: true` push. The delete path is the most rigorous code in the feature — it
re-reads afterwards and throws rather than report a removal that did not happen
(`PreviewEnvironmentService.cs:120-122`). But **not one test executes any of it.** `EnsureAsync`,
`RemoveAsync`, `ExpiredAsync`, `PreviewSweeper.SweepAsync` and `HandlePreviewAsync` have zero
coverage, and the two webhook tests that come closest pass `previews: null!` — they would
`NullReferenceException` if they ever entered the preview branch. There is no test that a push creates
a preview, that a preview gets an address, or that anything is ever removed.

**And merge is not a teardown trigger at all.** The acceptance criterion "PR open → URL comment ready
→ merge → gone" has, today, no first step, no third step, and no proof of the fourth. The one working
teardown signal — a deleted branch — depends on providers that delete the branch on merge, which is a
repository setting, not a guarantee.

One inference, flagged as such because it was not verified against a live payload: because
`GitWebhookProcessor.cs:188` reads `deleted` with no provider branching, a provider whose
branch-deletion payload still parses a top-level string `ref` without that flag would take the
`EnsureAsync` path instead — **re-creating and deploying a preview for a branch that no longer
exists.** That is the concrete shape of the risk the roadmap files under "provider PR payload
differences".

---

## 5. What is genuinely missing

Stated plainly, before it is decomposed.

1. **Two columns, and the seven behaviours that assume they are nullable.** The data is ready; the
   schema, the delete behaviour, nine branches and a hundred test literals are not.
2. **A workspace network that four platform operations still depend on.** Until backup, restore,
   rotation and the module's stager reach a database on its own network, "one network per workload"
   cannot ship.
3. **A failed database that says only `Failed`** — no reason, no timestamp, no alert, no incident, no
   dashboard row, and a retry button whose label is about something else.
4. **A rotation that misses prefixed variables and overwrites unrelated ones**, and then tells the
   user to go and redeploy by hand.
5. **A jobs table with no face.** Twelve kinds, eleven enqueue sites, a queue-position rule already
   written bilingually for nine of them — and nothing lists a single row. Six operations queue
   durably and still end in "it runs in the background"; four more do not queue at all.
6. **No toast component at all**, and 278 `TempData` assignments rendered by hand in 45 views. "Link
   every started-toast to /activity" is not a small edit today; there is nothing to edit.
7. **No retry for the one operation that fails most**, on a page whose own copy says you can retry.
8. **A build-arg feature with no switch.** Everything downstream of a checkbox exists; the checkbox
   does not.
9. **A port allocator that cannot see the operating system**, so a foreign process on port 20000 fails
   every deploy in a loop, deterministically choosing the same port each time.
10. **No pre-flight of any kind before a build** — not capacity, not free disk, not a daemon ping.
11. **A preview feature that cannot represent a pull request**, cannot tell anybody its URL where they
    are, and has never had its lifecycle executed by a test.

---

## 6. Decomposition

Eight sub-projects, each independently mergeable, each worth shipping alone. The letters are fresh for
this phase.

| | Sub-project | What it delivers | Schema |
|---|---|---|---|
| **P1** | **The report nobody has run** | A read-only report naming every workload with no environment, every environment with no workloads, and every row the enforcing migration would touch | none |
| **P2** | **The environment becomes required** | `IsRequired` on both columns, a new delete behaviour, nine dead branches removed, the test corpus corrected | one migration |
| **P3** | **One network per workload** | Backup, restore, rotation and the module stager move onto the workload's own network; then the dual attach goes | none |
| **P4** | **A database that fails, or rotates, tells you** | A failure reason and an incident; a retry that knows what it is for; a rotation that hits the right variables and offers the redeploys | one column |
| **P5** | **`/activity`** | Every durable job on one page, with status, position and cancel; started-messages that link to it | one column |
| **P6** | **Retry, and a build arg somebody can set** | A retry action on a failed deployment; the build checkbox the pipeline has been waiting for | none |
| **P7** | **Pre-flight** | Capacity re-checked at queue time, a burned port that advances, a low-disk refusal that names the threshold | none |
| **P8** | **Previews reach GA** | PR events, the URL where a reviewer is, teardown on merge, and the first lifecycle test | depends on §7 Q6 |

### The order, and why

There are **two independent tracks**, and only one of them has a forced order.

**The 0023 track is P1 → P2 → P3, and the order is not negotiable.**

**P1 first** because it is the only sub-project that produces evidence instead of change. It cannot
break anything, it can be run against production the day it merges, and both of the sub-projects
after it are matters of faith until it has. It is also the roadmap's own instruction, and the
precedent for where it lives already exists: `deploy/harbora`'s `cmd_doctor` (`deploy/harbora:61`) is
the operator-facing read-only check this codebase already ships. §7 Q1.

**P2 second** because making the column required changes no networking whatsoever — every workload
already has an environment, so `NetworkPlan.For` returns the same two names before and after. Bundling
it with P3 would put a schema change and a container-networking change in one revert.

**P3 third, and strictly after the four one-off paths are moved.** This is the sub-project that can
break a customer's backups, and the failure mode is silent. Its internal order matters more than its
position: move `BackupEngine.cs:244,444,955`, `ManagedServiceEngine.cs:361` and
`DatabaseTargetStager.cs:98` onto `NetworkPlan.Primary` **and prove each one still reaches a
database**, and only then flip `keepWorkspaceNetwork` to `false` and delete the parameter.

**Everything else is independent of that track and of each other**, so the order below is about value
and risk rather than dependency:

**P4 next, and arguably first of all**, because it is the only sub-project containing defects that can
damage customer data today. A rotation that leaves a second-attached database on a dead password, and
one that overwrites a *different* database's connection string, are both live. Neither needs anything
from the 0023 track. If the owner wants one thing merged this week, this is it — and the correctness
half should be its own commit, ahead of the surfacing half.

**P5 (`/activity`) is the largest and the most visible**, and the audit calls it the biggest systemic
UX gap (`12-ui-ux-audit.md:56`). It is placed after P4 only because P4 is smaller and more urgent, not
because anything blocks it. Its shape is decided by §7 Q3, which has to be answered before a plan.

**P6 and P7 can go in either order.** P6 is the smaller and contains the single highest
value-to-effort item in the phase — a checkbox that switches on a fully-built feature. P7 is the one
that stops a fleet getting stuck.

**P8 last**, for three reasons that are all real. The backlog makes it depend on 0023. It is the only
one whose scope is genuinely undecided (§7 Q6 and Q7 change what gets built, not how). And it is the
only sub-project where the existing code has *no test coverage at all*, which means its first task is
writing tests for behaviour that already ships — work that is much easier once the environment model
underneath it has stopped moving.

### P1 — the report nobody has run

One read-only report, answering four questions against the live database: how many `App` and
`ManagedService` rows have a null `EnvironmentId` and which they are; how many environments exist with
no workloads; how many workloads would attach to more than one network today; and whether any
workspace has workloads but no project.

**It writes nothing.** Not a fix-up, not a backfill, not a "would you like me to". The value is a
number an operator has read.

**Not in P1:** the migration. If the report returns non-zero, the answer is a bug report against a
delete path — not a second backfill bolted onto this sub-project.

### P2 — the environment becomes required

`IsRequired()` on both columns, in one migration with its own idempotent backfill retained (belt and
braces; it will update zero rows and that is the expected outcome). The delete behaviour changes in
the same migration — §7 Q2. The nine null branches listed in §3 are deleted rather than left as
unreachable code, because an unreachable fallback is how a future reader concludes the column is
still optional.

**The test corpus is the bulk of it,** and it should be treated as the work rather than as cleanup:
five tests assert the null case is legal and must be inverted, and ~98 `new App { … }` literals need a
placed environment. The fixture in `PipelineHarness.cs:75-98` already builds the full
Workspace→Project→Environment→App chain and is the model.

**Not in P2:** anything about networks.

### P3 — one network per workload

Four one-off container paths move from `WorkspaceNetwork(slug)` to the workload's own network, each
with a test that asserts **the network the one-off was asked to run on**, not that the operation
returned success — the existing `DatabaseAccessLifecycleTests.cs:293` is the shape, and note that it
currently pins `"harbora-ws-acme"`, so it is one of the assertions that has to change meaning
deliberately rather than by accident.

Then `keepWorkspaceNetwork` goes, `NetworkPlan.For` returns one name, and `NetworkPlanTests.cs:72`
(`A_deploy_also_keeps_it_on_the_workspace_network_for_now`) inverts — the name of that test is the
receipt that this was always the plan.

**One consequence to size before starting, because it gets better rather than worse.** The panel and
the proxy are joined to *every* network in the list (`DeploymentPipeline.cs:369-378`), so today
Traefik is a member of two networks per workload on a local server. Halving that is a benefit of P3,
not a cost — but it means the change touches proxy membership, and a proxy that has been
disconnected from a network it still routes on is the failure worth writing the test for.

**Not in P3:** garbage-collecting an environment's Docker network when the environment is deleted.
Nothing does that today, it is not a regression this introduces, and it deserves its own decision
about what happens to a network with containers still on it.

### P4 — a database that fails, or rotates, tells you

Two halves, in this order.

**The correctness half.** Rotation matches prefixed keys the way `Detach` already does
(`DatabasesController.cs:727`), and refuses to overwrite a variable whose current value belongs to a
different service — `ServiceUsage.Mentions` is the rule the rest of the codebase already uses for
"is this app attached to this database", and rotation should use it too rather than a weaker one of
its own. It returns ids, not display names, so a caller can act on the answer.

**The surfacing half.** A reason column on `ManagedService` (the pattern exists five times over), an
`AlertEvent` member and an incident opened through the Phase 6 machinery, an arm in
`Dashboard/Attention.cs`, and the existing `Reprovision` action re-presented as the retry it already
is when the status is `Failed`. Then rotation ends on a confirmation page that lists the apps it
rewrote and queues their redeploys — `DeploymentEngine.cs:101` already coalesces per app, so the loop
is safe, and `ConfirmRemove` is the page pattern.

**One trap this sub-project owns.** Appending to `AlertEvent` obliges a line in
`NotificationService.Matches` on the same day, and that file says so at `:212-217`. Two census tests
(`NotificationTemplateCensusTests`) will fail loudly if the templates are missed, which is the
protection working.

**Also worth fixing here, cheaply:** `ProvisionAsync` swallows every exception, which makes the
declared 3-attempt retry budget (`JobExecutionPolicy.cs:97`) unreachable. Once the reason is
persisted, the exception can be rethrown and the budget becomes real.

**Not in P4:** an App↔ManagedService link table. Inferred attachment is the established model and
changing it is a separate argument.

### P5 — `/activity`

Every durable job on one page, with the status chip, the queue position `QueuePosition.Describe`
already writes in both languages for nine kinds, and the cancel that `IJobQueue` already exposes
generically. `AuditController` is the filter/paging idiom to copy.

**The whole design turns on one question — §7 Q3 — because `Job` has no tenant column and this is
deliberate** (`HarboraDbContext.cs:984-992`). Every option has a cost and none is obviously right.

**The started-message half is bigger than it sounds.** There is no toast component; there are 278
`TempData` assignments rendered by hand in 45 views. Introducing one shared partial that every view
uses is most of this sub-project's diff, and it is worth doing once rather than threading a link
through 45 files.

**Not in P5:** live updates. SignalR exists but its only group is `deployment:{id}`; the proven
1.5-second poll (`DeploymentLogs.vue:68-74`) is enough for a list page, and a workspace-scoped hub
group is a separate piece of work.

**Also not in P5:** enqueueing the four operations that run inline today. Making disk cleanup and
environment clone into durable jobs changes their failure semantics and their transactional shape;
`/activity` can ship listing the eleven kinds that already queue, and say honestly that these four are
not jobs yet.

### P6 — retry, and a build arg somebody can set

**The checkbox first**, because it is three lines of Razor plus a test, and it switches on a path that
already reaches the local daemon and the remote agent. Compose-service builds get their args too
(`DeploymentPipeline.cs:924-925`).

**Then the retry.** What it re-uses is §7 Q4. Whatever the answer, it mints a new `Deployment` row —
`Deployment`'s own contract is immutable history, and the incident code already anticipates this at
`DeploymentPipeline.cs:616-619`: "a retry that fails again opens a second, independently-closeable
incident rather than reopening one someone already dismissed."

**Not in P6:** changing `JobExecutionPolicy`'s refusal to auto-retry deployments. That is a considered
decision with a comment explaining it, and a user-initiated retry is the deliberate act it points to.

### P7 — pre-flight

Three checks, each of which must **name its reason** rather than surface a Docker string.

`SchedulerService.CheckAsync` already exists and is called once; calling it at queue time is the whole
of the capacity item. The port burn has a working precedent in the same codebase —
`NodeIngressRegistry.cs:82-140` catches `SocketException` and advances — applied to the panel-side
listener rather than the node-side publish. The disk gate reads a figure the platform already collects
(`HostInfo.FreeDiskBytes`), and its threshold belongs in the `Monitoring` options section Phase 6
created rather than as a fourth constant. Whether that gate refuses or merely warns is §7 Q5, and it
is the difference between delivering the item and restating what `MetricsCollector` already does.

**Not in P7:** a deployment-wide failure-reason enum. `HealthDiagnosis` shows what that looks like
done well and it is a bigger project than this; P7 adds three named refusals, it does not classify
everything.

### P8 — previews reach GA

PR events parsed per provider off the header that is already captured; the URL surfaced where a
reviewer will see it; teardown on merge or close; and — first, before any of it — tests for the
lifecycle that already ships. §7 Q6 decides whether the domain gains a pull request at all, and Q7
decides whether "URL surfacing" means the parent's list or a comment on the PR.

**The first task is coverage of existing behaviour.** `EnsureAsync`, `RemoveAsync`, `ExpiredAsync` and
`SweepAsync` have none, and `GitWebhookScopeTests.cs:75,125` pass `previews: null!`. Adding PR events
to an untested lifecycle is how the teardown proof ends up being a screenshot.

**Not in P8:** Bitbucket. It is supported by nothing today — no signature scheme, no enum member — and
adding a provider is a different piece of work from adding an event.

---

## 7. The seven decisions this spec cannot make

Each changes what gets built, not merely how.

**Q1 — Where does the dry-run report live?**
*(a) A verb in `deploy/harbora`*, beside `doctor` (`deploy/harbora:61`). It is where an operator
already stands, it needs no auth model, and it can run before the panel is healthy. Cost: it is a bash
script talking to Postgres, so the query lives outside the code that owns the schema and will drift.
*(b) A platform admin page*, under `PlatformManage`. Cost: it requires a working panel and a session
to read a number about a migration you are about to run — and this is exactly the report you want when
you are nervous.
*(c) A `harbora` CLI verb.* Cost: wrong audience — that CLI is the customer's deploy tool, ten verbs,
none administrative.
*(d) A test in the Postgres lane* that asserts the invariant, run in CI rather than by a person. Cost:
it proves it about a fixture, not about production, which is not what the roadmap asked for.

**Q2 — What happens to a workload when its environment is deleted, once the column is required?**
Today `SetNull` detaches it, deliberately, so that deleting an environment never takes a customer's
running apps with it (`HarboraDbContext.cs:354-359`). That behaviour becomes impossible.
*(a) `Restrict`* — the database refuses the delete. Matches the application guard that already exists
at `ProjectsController.cs:254-264` and makes it real. Cost: `AppOperationsService.cs:148` and any
future cleanup must be certain the environment is empty, and a partially-deleted preview would leave
an environment nobody can remove.
*(b) `Cascade`* — deleting an environment deletes its workloads. Never; it is the outcome the current
comment exists to prevent.
*(c) Reassign to the project's default environment* — a trigger or an application step that moves
workloads home. Preserves the "never lose an app" intent. Cost: a workload silently changes network
and therefore what it can reach, which is a bigger surprise than a refused delete.

**Q3 — How does `/activity` scope a job to a workspace?**
`Job` has no tenant column and is listed among the deliberately unfiltered platform tables.
*(a) Add a denormalised `WorkspaceId`.* There is precedent: `Deployment` and the backup-module rows
carry one for a stated reason (`HarboraDbContext.cs:1005-1008`). Cheapest to query, and it makes the
page trivially correct. Cost: a migration, eleven enqueue sites to update, and a nullable column for
`BillingHour` and other platform-level jobs that belong to nobody.
*(b) Resolve the tenant by joining `TargetId` to nine different aggregate tables.* No schema change.
Cost: nine joins per page load, a switch that a tenth job kind will silently fall out of, and a page
that is wrong rather than empty when it does.
*(c) Platform-admin only, unscoped* — the `/audit` model. Smallest and honest. Cost: it does not
deliver the acceptance criterion, which says workspace-scoped visibility, and the customer whose
backup is queued still cannot watch it.

**Q4 — What does a deploy retry re-use?**
*(a) Nothing — it is today's redeploy with a button in the right place.* Honest and one line. Cost:
it does not satisfy the acceptance criterion "re-enqueues with the same config snapshot", and
retrying a failure caused by a bad commit re-clones the branch tip, which may now be different.
*(b) The recorded `CommitSha`.* Re-builds the same source. Cost: env vars, size and volumes still come
from the app as it is now, so it is a partial replay and has to say so.
*(c) A full config replay from `ConfigJson`.* **Impossible as designed** — secrets are stored as HMAC
fingerprints (`DeploymentConfig.cs:57,77-83`) precisely so the snapshot cannot leak them. Making it
replayable means storing secrets a second time, which is a decision about a different subsystem.

**Q5 — Does the disk gate refuse, or warn?**
*(a) Refuse below a threshold*, naming the figure. Delivers the acceptance criterion. Cost: a number
that is wrong for somebody, and a platform that stops deploying at 3am because a log file grew.
*(b) Warn and proceed.* Never blocks legitimate work. Cost: it is the state today — `MetricsCollector`
already warns at 85% — so it delivers nothing.
*(c) Refuse, with an operator override.* Both. Cost: an override is a second code path that only
appears in an emergency, which is the code path least likely to have been tested.

**Q6 — Does the domain gain a pull request?**
The acceptance criterion says `pr-{n}`; the code is branch-keyed throughout, and `PreviewNaming`'s
branch hash exists so two previews cannot collide by construction.
*(a) Keep branch keying; treat PR events purely as triggers.* Smallest, and everything already built
keeps working. Cost: the panel cannot say "this preview is for PR 41", the acceptance criterion is met
in spirit rather than in letter, and reopening a PR on the same branch is indistinguishable from a
push.
*(b) Add a PR number and state to `App`, and name previews `pr-{n}`.* Matches the criterion and the
competitor flow. Cost: two naming schemes coexisting (a preview created by a push has no PR number), a
migration, and every existing preview's hostname either changes or does not — and changing it breaks
a URL somebody has bookmarked.
*(c) A separate `PullRequestPreview` row pointing at the preview app.* Cleanest model. Cost: a table,
and a second lifecycle to keep in step with the first.

**Q7 — Does GA include commenting the URL back on the PR?**
`IGitProviderClient` has one method and it lists repositories. Every stored provider credential is
used read-only today.
*(a) No — surface the URL on the parent app's preview list and stop there.* Delivers most of the value
for a fraction of the work and needs no new credential scope. Cost: a reviewer never sees the URL
without opening the panel, and that is the flow the feature was sold on.
*(b) Yes — a write-scoped provider client posting a PR comment.* The competitive flow, in full. Cost:
three provider APIs, a token scope the panel does not ask for today, a failure mode where the deploy
worked and the comment did not, and comment spam on every push unless it edits in place.
*(c) A commit status / check run instead of a comment.* Quieter and idempotent by nature. Cost: same
credential problem, and it is less visible than the thing customers have seen elsewhere.

---

## 8. Testing

Each sub-project states its own, but five rules apply across the phase.

- **Assert on the request handed to the engine, not on what came back.** For P3 this is the whole
  proof: the test must pin *which network* the dump one-off was asked to run on. A backup test that
  asserts "returned success" against a fake engine will pass on both sides of the change.
- **A test that a migration is safe must first assert there was something to migrate.** The
  equivalent trap here is a backfill test over an empty fixture — it passes, instantly, and proves
  nothing. The Postgres lane currently has zero coverage of the 2026-07-30 backfill SQL; P1 and P2
  should leave it with some.
- **A background evaluator's test must run without a session and still see rows.** `Job` is unfiltered
  on purpose and `/activity` will be read *with* a session; the failure mode is a page that is empty
  rather than broken. Two workspaces in the fixture, and an assertion about which rows each sees.
- **Assert on `data-` attributes and route fragments, not on visible text.** The panel renders
  **Persian by default**; `Details.cshtml:718`'s `data-spec-restarts` is the established model. Note
  that `@Model.Status` on the database views is currently untranslated — P4 fixing that will break any
  assertion written against the English word.
- **Cover what already ships before extending it.** P8's first commit is tests for `EnsureAsync`,
  `RemoveAsync`, `ExpiredAsync` and `SweepAsync`, none of which has ever been executed by a test, and
  fixing `GitWebhookScopeTests.cs:75,125` so `previews` is a real object rather than `null!`.

Specific behaviours worth naming now: a workload with no environment cannot be saved · deleting an
environment that still holds an app does whatever Q2 decided, provably · a database dump reaches its
database on the environment network · a rotation rewrites `MYDB_DATABASE_URL` and leaves another
service's `DATABASE_URL` untouched · a provision failure leaves a reason on the row and a job row that
does not claim success · a queued backup appears on `/activity` and can be cancelled from it · another
workspace's job never appears · a build arg marked at build time reaches `ImageBuildParameters` · a
second deploy after a bind failure chooses a different port · a `pull_request` payload does something
other than return 200 and nothing · a merged PR's preview is gone, asserted by reading the database
rather than by the sweeper reporting success.

---

## 9. What Phase 3 is not

Per-app RBAC and ownership transfer — Phase 4, and the roadmap says so · app move between servers —
Phase 5 · IaC apply · a deployment-wide failure taxonomy, which `HealthDiagnosis` shows the shape of
and which is larger than 0028 · garbage collection of environment networks, which nothing does today
and which needs its own decision about a network with containers still attached · an
App↔ManagedService link table, replacing inference that works · Bitbucket · live updates on
`/activity`, which want a workspace-scoped SignalR group that does not exist · scale-to-zero or
per-request billing for previews · **security review**, which is out of scope by the owner's standing
instruction: what is being settled here is whether a workload is where the panel says it is, and
whether an operation that failed admits it.
