# 03 — Reliability & Production Problems

Build/test evidence: clean build (0/0), 3,141 tests pass, 0 fail, 17 Docker-gated skips (no daemon on the audit machine; CI runs them and fails on silent non-execution). No flaky markers exist in the tree.
Classification: **[BUG]** wrong behavior · **[DEBT]** structural/technical debt · **[GAP]** missing capability. Confidence: H/M/L (all H items were verified in source by line).

---

## P0 — Critical (full records)

### R-01 · Platform-wide serial job execution [DEBT→operational BUG at scale]
- **Severity:** P0 · **Confidence:** H
- **Component:** Job queue — `src/Harbora.Infrastructure/Jobs/JobWorker.cs:40-100`, single registration `DependencyInjection.cs:100`
- **Current:** One `JobWorker` claims and fully awaits exactly one job. Deployments, backups, provisions, cron runs, module snapshots/restores all share it.
- **Expected:** Bounded parallelism with per-app/per-target mutual exclusion (e.g. N workers + per-`(Kind,TargetId)` locks), long jobs unable to starve short ones.
- **User impact:** A tenant's 20-minute build delays every other tenant's deploy and every backup; a 6-hour Kopia snapshot (`KopiaOptions.CommandTimeout`) freezes the platform's background work.
- **Cause:** Multi-worker claim machinery (`ClaimedBy`, `ClaimStamp`) was built but a second worker was never registered; no per-key concurrency limiter exists.
- **Fix:** introduce worker pool (config `Jobs:MaxConcurrency`, default ≥ 2× cores capped) + advisory per-target serialization (`Kind+TargetId` claim predicate); keep deployment-per-app serial.
- **Tests:** concurrency test proving two different apps deploy in parallel while same-app stays serial; backup running does not delay a deploy.
- **Phase:** 1

### R-02 · Interrupted module backup permanently blocks its target [BUG]
- **Severity:** P0 (backup availability + operator dead-end) · **Confidence:** H
- **Component:** Backup module — `BackupSnapshotService.QueueAsync` guard `:60-69`; no sweeper for `BackupSnapshot`/`RestoreJob` rows; `JobReconciler.cs:40-54` settles only the Job.
- **Current:** Hard restart mid-backup leaves the snapshot `Preparing/Running` forever; every later manual or scheduled backup of that target is refused ("already running") with no UI to clear it. Restores into a destination block identically (`RestoreService.cs:138-143`). Plaintext staging directories also survive the crash (no sweeper).
- **Expected:** Startup reconciler marks orphaned snapshot/restore rows `Failed` (mirroring `JobReconciler`), cleans staging dirs, and the next schedule retakes the backup.
- **User impact:** Silent end of backups for a target after one crash — the worst possible failure mode for a backup product.
- **Fix:** module `IHostedService` reconciler + staging-dir sweep + an admin "clear stuck" action; convert the queue guard into a DB-level uniqueness (partial unique index on active statuses) to also fix the read-then-insert race.
- **Tests:** kill-during-backup harness test → after restart, target backup-able again and stale staging removed.
- **Phase:** 1

### R-03 · Proxy apply failure still reports deployment `Succeeded` [BUG — false success]
- **Severity:** P0 · **Confidence:** H
- **Component:** `DeploymentPipeline.cs:1112-1115` + `TraefikProxyEngine.ApplyAsync` (no post-apply verification; `IProxyEngine` doc promises verify)
- **Current:** Route render/validate/apply failure logs one `⚠ Proxy apply failed` line; status still `Succeeded`; traffic keeps hitting the previous upstream (or nothing).
- **Expected:** Apply failure ⇒ deployment `Failed` (with rollback of the config file, which already exists) or an explicit degraded state + alert; optionally verify by probing through Traefik.
- **User impact:** "Deploy succeeded" while the site serves the old version or 404s — direct violation of the product's own "honest software" principle.
- **Fix:** propagate apply failure into the state machine; add `DeployFailed`-class alert; add a post-cutover HTTP probe through the proxy for domained apps.
- **Tests:** unit test on pipeline with failing proxy engine → `Failed`; ApplyAsync rollback test (currently zero tests on this class).
- **Phase:** 1

### R-04 · Backups/cleanup/gateway bypass the engine seam — wrong host on multi-node [BUG]
- **Severity:** P0 for multi-server correctness · **Confidence:** H
- **Component:** `BackupEngine.cs:25`, `DiskCleanupService.cs:43`, `DockerTcpGateway.cs:27` inject local `IDockerEngine` instead of `IServerEngineFactory`
- **Current:** Volume/DB backup of a resource scheduled on a remote server tars **the panel host's** volume of the same name (or fails); disk cleanup iterates all apps but only ever prunes the panel's daemon; DB gateway only works for local databases (undeclared).
- **Expected:** resolve engine per resource's server; refuse clearly when the capability is absent on that engine (v1 nodes: dispatch `SnapshotVolume`/`RestoreVolume` verbs, which already exist and are tested agent-side but never dispatched panel-side).
- **User impact:** Backups that "succeed" with wrong/empty content for node-hosted services — data-loss class.
- **Fix:** thread `IServerEngineFactory` through; wire node snapshot verbs; add per-server cleanup loop.
- **Tests:** fake-engine test asserting backup of remote-server service resolves the remote engine; refusal path for legacy agent.
- **Phase:** 1

### R-05 · Node Agent v1 unusable out of the box (activation chain broken) [BUG]
- **Severity:** P0 for the flagship node feature · **Confidence:** H
- **Component:** `deploy/install.sh` (never writes `NodeAgent__PublicUrl` / `TrustForwardedClientCertificate`), `deploy/traefik/dynamic/node-agent.yml` (hardcoded `panel.example.com` hosts :46,:55; `caFiles: /etc/traefik/dynamic/node-ca.pem` never generated; comment references non-existent `harbora node-ca` verb — `AdminCommands.cs:31-35` has no such verb)
- **Current:** A fresh install cannot enroll a v1 node without undocumented manual work: templating the Traefik file, exporting the CA (`nodeagent.ca.certificate` setting by hand), and setting env vars.
- **Expected:** installer templates the mTLS file with the real panel domain, exports the CA on first boot, and writes the env vars; `harbora node-ca` verb added (or the comment corrected).
- **User impact:** README's headline capability fails for every new operator; the older inbound agent quietly remains the only working path.
- **Fix:** installer + AdminCommands verb + docs alignment; add an install-verify step that hits `/api/node-agent/v1/enroll` preflight.
- **Tests:** `DeploymentArtifactTests`-style assertions on install.sh writing the keys; doc drift test on the referenced verb.
- **Phase:** 2

### R-06 · No job retry and no job-level timeout [DEBT]
- **Severity:** P0 combined with R-01 · **Confidence:** H
- **Component:** `JobWorker`/`JobDispatcher` (Attempts incremented never read; only per-op bounds: release task 30 m, cron 1 h, remote HTTP 30 m)
- **Current:** A hung `docker build` against a live daemon runs forever and (because of R-01) halts the platform; transient failures are terminal.
- **Expected:** per-kind default timeout (deploy 30–60 m) with cancellation; bounded retry with backoff for retry-safe kinds (provision, backup upload), never for deploys mid-flight.
- **Fix:** wrap dispatch in linked CTS from a `JobKind→timeout` table; honor `Attempts` with `MaxAttempts` per kind; classify retryable errors.
- **Tests:** hung-handler fake → job times out, next job proceeds; retryable failure re-enqueues with backoff.
- **Phase:** 1

---

## P1 — Serious (full records)

### R-07 · Shutdown-vs-reconciler race double-fails a deployment [BUG]
- **Confidence:** H · **Component:** `JobWorker.cs:84-90` (returns job to `Pending`) vs `DeploymentReconciler` (marks its deployment `Failed` at boot), `BackgroundService.StartAsync` ordering race.
- **Current:** Restart during graceful shutdown → job re-dispatched for a `Failed` deployment → `SetStatus(Building)` throws illegal transition → recorded failed twice; worker loop can even claim while reconciler is writing.
- **Fix:** reconciler settles Pending jobs whose deployment is terminal; make JobWorker start after reconcilers via explicit ordering/gate; pipeline treats "already terminal" as no-op.
- **Tests:** restart harness reproducing the interleave (none exists today).
- **Phase:** 1

### R-08 · External DB credential rotation always fails (Fake client in production DI) [BUG]
- **Confidence:** H · **Component:** `DatabaseAccessService.RotateAsync:233-253` ignores `CanOpenLocally`; `FakeNodeAgentClient` is the only `INodeAgentClient` (`DependencyInjection.cs:210`).
- **Current:** Every rotation returns "No such login to rotate", even on single-server installs where the local path works for create/revoke.
- **Fix:** branch on `CanOpenLocally` like its sibling methods; longer-term, implement the real node client (grants over the tunnel) or hide rotate for node-hosted DBs.
- **Tests:** rotation test through the local executor.
- **Phase:** 2

### R-09 · Backup UI: SFTP fields rendered in the wrong loop kill the destination form [BUG]
- **Confidence:** H · **Component:** `Views/Backups/Index.cshtml` — `data-when-sftp` block (L131-145) sits inside the Schedules `@foreach` instead of the destination form; toggle script (L321) then throws on page load.
- **Current:** Local/S3 toggle dead; SFTP destinations impossible to create from UI (backend supports them; tests exist).
- **Fix:** move the block; add a UI-convention test (the repo already has source-reading UI tests that could assert this structure).
- **Phase:** 1 (small)

### R-10 · Module snapshots can never become verified [BUG]
- **Confidence:** H · **Component:** `JobKind.BackupVerify` handler registered, **never enqueued**; `BackupSnapshotService.RunAsync` skips `Verifying`.
- **Fix:** schedule verify after completion + a "verify now" action; surface result in the existing `VerificationStatus` column.
- **Phase:** 2 (with module GA)

### R-11 · Module restore has no safety copy [GAP]
- **Confidence:** H · **Component:** `RestoreJob.SafetySnapshotRef` column exists, never assigned; legacy engine *does* take pre-restore snapshots and refuses when it fails (`BackupEngine.cs:326-341`).
- **Fix:** port the legacy pre-restore behavior into `RestoreService` before destructive filesystem/DB restores.
- **Phase:** 2

### R-12 · Cron in a non-default environment joins the wrong network [BUG]
- **Confidence:** H · **Component:** `CronJobRunner.cs:98` builds only the workspace network while pipeline/services use `NetworkPlan` (environment-first).
- **Current:** A cron app in `staging` cannot resolve its own environment's database hostname.
- **Fix:** use `NetworkPlan.For` identically to the pipeline; regression test with non-default env.
- **Phase:** 1 (small)

### R-13 · Notifications are single-attempt, best-effort, no dedup for most events [DEBT]
- **Confidence:** H · **Component:** `NotificationService.DispatchSafe` (no retry; 10 s timeout; `LastError` only); dedup exists only for disk (in-memory) and thresholds; deploy-failed/SSL/backup-failed have none.
- **Impact:** transient 502 from Discord permanently loses a critical alert; restart double-fires disk alerts.
- **Fix:** covered by the Notification System plan (09): durable outbox + retry + dedup keys.
- **Phase:** 9 (interim: add one retry + jittered backoff in place — S effort)

### R-14 · Unbounded growth: `DeploymentLogs`, `AuditLogs`, `CronRun`, `NodeCommandRecord`, `NodeEventRecord`, `IdempotencyRecord`, `PasswordResetToken` [DEBT]
- **Confidence:** H · **Component:** no retention job targets any of these; `IdempotencyRecord.ExpiresAt` indexed but never purged (contradicting its own comment).
- **Fix:** one nightly retention sweeper with per-table knobs (defaults: deploy logs 90 d or last N deployments per app; audit 365 d configurable; idempotency 7 d; command/event records 90 d).
- **Phase:** 2

### R-15 · Silent managed-service provisioning failure [BUG]
- **Confidence:** H · **Component:** `ManagedServiceEngine.ProvisionAsync:133-138` catch → `Status=Failed`, no alert, job "succeeds".
- **Fix:** raise alert (new event) + failure reason on the row + UI surfacing (page already shows status).
- **Phase:** 3

### R-16 · RUNBOOK/README drift misleads operators [DEBT-docs]
- **Confidence:** H · **Examples:** RUNBOOK omits required `S3_DOMAIN`/`MINIO_*` env keys and teaches the legacy agent; README claims "Jobs: … + Redis" (unused) and lists 5 DB engines vs 7; `platform-expansion-v1.md` claims "not merged" though code is on master; three node docs say "21 verbs" (actual 24); merge-notes still lists shipped `GetWorkloadStats` as a gap; contract CHANGELOG missing entries past v1.2.0.
- **Fix:** single doc-truth pass + the repo's own delivery rule ("documentation must describe shipped behavior only"); add drift tests where cheap (verb count vs catalog).
- **Phase:** 2

---

## P2 — Important (compact records)

| ID | Class | Problem | Component / evidence | Fix sketch | Phase |
|---|---|---|---|---|---|
| R-17 | BUG | Topbar bell links `/alerts` → 404 (no GET route) | `_Topbar.cshtml:79`, `AlertsController` POST-only | Point at `/monitoring#alerts` or add GET page | 1 |
| R-18 | BUG | Broken links: Networks→`/databases/details/{id}`; Terminal breadcrumb→`/apps/{guid}` | `Networks/Index.cshtml:126`, `Terminal/Index.cshtml:11` | Fix hrefs; add `RouteValueCollision`-style link test | 1 |
| R-19 | BUG | 13 lucide icons referenced but not imported (incl. Nodes & Sync sidebar icons, dashboard health check icon) | `Scripts/main.ts:73-94` vs views | Import; add source-reading icon test | 1 |
| R-20 | BUG | PWA icons declared but files absent → install prompt rejected | `manifest.webmanifest` vs missing `wwwroot/icons/` | Ship icons | 2 |
| R-21 | BUG | Command palette uses un-augmented Advanced-only map; renders raw keys like `ai-admin`; never lists Backup/Sync centers | `_Topbar.cshtml:927`, `NavigationMap.cs:98-100` | Use the same augmented map as sidebar; unify label table | 2 |
| R-22 | DEBT | Scheduler/quota not re-checked on redeploy; capacity drift possible | pipeline never calls `ISchedulerService`/`IQuotaService` | Re-validate at queue time (cheap read) | 3 |
| R-23 | DEBT | OS-level port collision on nodes → deploy fails, no re-allocation | `HostPortAllocator` (DB-level only) | On bind failure, mark port burned + retry next port | 3 |
| R-24 | DEBT | No pre-flight disk check before builds; cleanup is manual and local-only | `DiskCleanupService` manual; no free-space gate | Pre-build free-space gate + scheduled cleanup incl. buildkit cache | 3 |
| R-25 | BUG | Heartbeat `ActiveDatabaseGrants`/`ActiveTunnels` never populated (panel shows 0) | `NodeAgentWorker.cs:404-417` | Populate from managers; assert in a heartbeat test | 2 |
| R-26 | DEBT | 7 node event kinds declared, never published (pressure/cert/tunnel/container-state) | `Frames.cs:245-264` | Publish from evaluator transitions | 4 |
| R-27 | BUG | Kopia restore ignores `Skip`/`Rename` strategies (only up-front `Fail` check) | `KopiaBackupEngine.cs:131-137` | Honor or refuse unsupported strategies explicitly | 2 |
| R-28 | DEBT | Module backup concurrency guard is read-then-insert race | `BackupSnapshotService.cs:60-69` | Partial unique index on active statuses | 2 |
| R-29 | BUG | Managed-DB password rotation rewrites env vars but doesn't redeploy apps (stale creds until next deploy) | `ManagedServiceEngine.RotatePasswordAsync:304-321` | Offer/trigger rolling redeploy of attached apps | 3 |
| R-30 | BUG | Templates cannot require brokers (`rabbitmq`/`nats` rejected) despite first-class engine support | `TemplateDeploymentService.ParseServiceType:315` | Extend parser + manifest schema | 3 |
| R-31 | DEBT | Postgres TLS bootstrap failure silently degrades to unencrypted (logged only) | `ManagedServiceEngine.cs:80-110` | Surface on service row + alert; make policy explicit | 3 |
| R-32 | BUG | `AlertsController.Create` partial-threshold input silently stores an inert rule | `AlertsController.cs:62-67` | Validate as a unit; UI error | 2 |
| R-33 | GAP | No alert edit/disable — create/test/delete only; `IsEnabled` hardcoded true | `AlertsController` | Add edit/toggle routes + UI | 3 |
| R-34 | DEBT | `MetricRollups` has no index though queried by 5-column predicate | migration `20260731150727`; `MonitoringController.cs:197-200` | Add composite index migration | 2 |
| R-35 | DEBT | Backup module: native engine has no timeout; module browse decrypts entire archive per directory level | `HarboraNativeBackupEngine` | Bound with CTS; cache listing | 3 |
| R-36 | DEBT | CLI: Ctrl+C not honored while following logs; `apps/logs` network errors exit -1 not 1; no upload progress | `Commands.cs:387-409`, `ApiClient` | Thread CT; normalize exit codes | 3 |
| R-37 | DEBT | `examples/harbora.yml` advertises unsupported `env:`/`domains:` keys (silently dropped) | parser `ProjectConfig.cs:112-122` | Fix example or implement keys | 2 |
| R-38 | DEBT | `redis` runs in compose but nothing uses it; `StackExchange.Redis` dead ref | `docker-compose.yml`, csproj:20 | Remove both (or wire cache) | 2 |
| R-39 | DEBT | In-memory throttles/limits are per-process (AlertThrottle, durable-queue cancel registry, AI rate limits) — documented single-instance constraint | various | Acceptable now; blockers for HA listed in 13 | 4+ |
| R-40 | BUG | `VerifyTimeoutSeconds` stored in update marker, never used (no timeout-driven rollback path) | `AgentUpdater.cs:154` | Implement or drop from contract | 4 |
| R-41 | DEBT | CI: main workflow runs only `Harbora.Tests`; `install.sh`/`deploy/harbora` never linted; CLI release lacks checksums; NuGet push gate can never fire | `.github/workflows/*` | Consolidate; add checksums | 2 |
| R-42 | DEBT | `harbora backups/restore-db` hardcode `/var/lib/docker/volumes/harbora_backups/_data` | `deploy/harbora:191` | Resolve via `docker volume inspect` | 2 |
| R-43 | GAP | No orphan-volume enumeration; `RemoveVolumeAsync` no-op on v1 nodes leaves invisible data | engine seam | Orphan report page + reconcile | 5 |
| R-44 | DEBT | Backup staleness (48 h), disk warn (0.85) hardcoded and duplicated | `MonitoringController.cs:122`, `MetricsCollector.cs:23` | Options-bind | 3 |
| R-45 | BUG | `Setting` "Bitbucket" enum value has no working provider path | `GitProviderType` | Implement or hide option | 3 |

## P3 — Minor (list)

Dead surface to remove or wire (each S-effort): `DeploymentStatus.Pushing`; `AlertEvent.ThresholdBreached` (route rule-notifications through `Matches` instead); `CertificateStatus.Revoked` writer; module dead fields (`CompressionAlgorithm`, `Include/ExcludePatterns`, `Pre/PostBackupHook`, `EncryptionEnabled`, `AlertAfterHoursWithoutSuccess` — or implement the no-recent-backup evaluator, which is genuinely valuable, see 08); `BackupTrigger.Safety`; `RestoreType.{File,Volume,Application,FullServer}`; `BackupRepositoryType.{WebDav,HarboraNode,Custom}` should return refusals not exceptions; `KopiaCommands.Maintenance` never called (repos never GC — becomes P1 if Kopia GA); CLI `YamlDotNet` unused dep; `Context:` parsed unused; 3 orphan partials (`_Placeholder`, `_ValidationScriptsPartial`, `_Sparkline`); `DatabaseAccessService.AuditAsync` empty; `AiModel.SupportsResponses`, `AiPlan.TrialAvailable`, `AiPlan.MaxContext` (unenforced), `AppTemplateAsset.WorksOnBothThemes` unread; `DeploymentHub.Unsubscribe` unused; Vue `text-success` non-token class; stale Tenants phase note; `Routes/Index` dev copy ("run npm run build") shown to users.

---

## Reliability review answers (mission checklist not already covered)

- **Half-done deploy:** safe — failure removes only the new container; previous release untouched; reconciler converges statuses at boot.
- **Concurrent deploys of one app:** coalesced by intent; conflicting intents refused; serial worker is the real gate (benign TOCTOU noted).
- **Node offline mid-deploy:** command fails → deploy `Failed` + alert; agent-side durable outbox reconciles result delivery on reconnect; `ListContainersAsync` deliberately throws (never "cut over onto nothing").
- **Docker daemon down:** reactive handling everywhere; server flips Offline via collector; **no pre-flight ping** before deploys (R-24 adjacent).
- **RAM exhaustion:** limits enforced per instance size; scheduler avoids overcommit at placement; no OOM-specific diagnosis in failure taxonomy (falls under `Exited`).
- **App state after server reboot:** containers `unless-stopped` + agent `StateReconciler` (restarts stopped workloads, reports missing ones, never invents deploys); panel `NodeIngressRebinder` rebinds tunnel ports.
- **Half-done jobs at boot:** settled `Failed` deliberately (no retry) — combined with R-02's missing module-row reconciliation this is the main gap.
- **Migration compatibility:** guarded by `MigrationConsistencyTests` + pre-migration dump with refusal + `--no-build` trap documented in team memory.
- **Platform upgrade:** strong path (see 01 §9); recovery CLI works while panel is down; the one hazard is `fix-key` replacing a working master key (guarded by typed `REPLACE`).
- **ARM64:** installer/agent/images all multi-arch; builds inherit panel-host arch (multi-arch build gap noted in 02 §4).
