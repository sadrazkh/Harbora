# 06 — Node & Deployment Plan

Goal of this plan: make the deploy engine and the node fleet trustworthy under failure, then extend capability. It sequences the P0s from 03 plus the node items from 05 §B4. Nothing here proposes a rewrite — the pipeline, state machine, cutover and the v1 protocol are keepers (see 19).

## 1. Deployment engine hardening (Phase 1)

### 1.1 Concurrency (R-01)
Work: `JobWorker` pool (`Jobs:MaxConcurrency`, default `min(4, cores)`), claim query excludes kinds/targets already running via `(Kind, TargetId)` in-flight set; deployments stay serial **per app**, backups serial **per target**, cron serial per app. Keep the single-row claim + `ClaimStamp` (already race-safe).
Acceptance: `DeploymentEngineConcurrencyTests` extended — app A build (slow fake) does not delay app B deploy; module backup does not delay deploys. Rollback plan: config `Jobs:MaxConcurrency=1` restores today's behavior.

### 1.2 Timeouts & retry (R-06)
Work: `JobKind → TimeSpan` table (Deployment 45 m, ServiceProvision 15 m, Backup* 6 h aligned with Kopia, CronRun 1 h existing); linked CTS around `JobDispatcher.ExecuteAsync`; `MaxAttempts` per kind (Deployment 1, ServiceProvision 3, BackupSnapshot 2) honoring the existing `Attempts` column; retry only on classified-retryable errors (`NodeError.Retryable`, HTTP 5xx, socket).
Acceptance: hung fake handler → `Failed(TimedOut)` at deadline, worker continues; provision transient failure retries with backoff and audit line.

### 1.3 Truthful terminal states (R-03, R-07)
Work: proxy apply failure ⇒ pipeline throws ⇒ `Failed` + existing `DeployFailed` alert; add post-cutover probe (GET via Traefik on the primary domain, 3 attempts) behind option `Runtime:VerifyThroughProxy` (default on when a domain exists). Fix the boot race: `DeploymentReconciler` also settles `Pending` jobs whose deployment is terminal; gate `JobWorker` start on reconcilers (an `IStartupGate` the worker awaits).
Acceptance: failing proxy fake ⇒ `Failed`; boot-interleave test (new) shows single terminal transition.

### 1.4 Queue transparency (UX, 12 §6)
Work: Queued deployments show position + what's ahead ("behind 2 jobs: backup of X…"); cancel button on Queued; CLI `harbora cancel <deploymentId>` (needs one new API endpoint).
Acceptance: CLI/UI can cancel a queued deploy; position visible.

## 2. Node fleet plan

### 2.1 Out-of-box enrollment (R-05, Phase 2)
Work in `deploy/install.sh` + panel:
1. Template `node-agent.yml` host rules from `PANEL_DOMAIN` at install/update time (simple sed into the watched dir).
2. First-boot CA export: panel writes `node-ca.pem` beside its Traefik dynamic file when `nodeagent.ca.certificate` exists; installer references that path.
3. Backfill `NodeAgent__PublicUrl=https://$PANEL_DOMAIN` and `NodeAgent__TrustForwardedClientCertificate=true` into `.env` (repair_env).
4. Add `harbora node-ca` verb to `AdminCommands` (prints the CA PEM) — the file already references it.
5. `verify_install` gains an enroll-endpoint preflight (expect 401-with-JSON, not 404).
Acceptance: fresh VPS + one command → panel; `install.sh` on a second VPS with a minted token → node Online, deploy of `nginx:alpine` onto the node succeeds through Traefik. This is the **R0 "release proof"** from `docs/overhaul/17-next-roadmap.md` — automate it as a CI-lane script on a real Docker host.

### 2.2 Observability completion (Phase 2)
- Populate `ActiveDatabaseGrants`/`ActiveTunnels` in heartbeat (R-25) + panel test asserting non-zero after a grant.
- Publish the 7 declared node events on state transitions (R-26): pressure enter/exit, cert expiring/rotated, tunnel state, container state changes (from runtime events poll). Panel: surface in node Events feed (UI exists).
- Panel-side scrape (optional, Phase 4): pull `:9701/metrics` over the tunnel into `MonitoringMetrics` for node self-view parity.

### 2.3 Capability honesty & growth
- Keep v1 nodes build-free. Document per-source compatibility matrix on the New App form when a node is targeted (today discovered at deploy time).
- **Volume snapshot/restore over verbs** (with R-04): panel `BackupEngine` resolves engine per server; for `NodeWorkloadEngine`, dispatch `SnapshotVolume`/`RestoreVolume` (agent side shipped + tested; panel never calls them). Snapshot artifact lands in node staging; ship via existing tunnel or HTTPS presigned upload — decide in 18 (open question Q7).
- **App move between servers** (Phase 5, L): stop→snapshot→restore→start→rewire routes/ports; refuse legacy-agent sources.
- Terminal on nodes: requires a bidirectional exec channel — defer; contract change would be additive (`ExecWorkload` verb) but is explicitly v2-scoped unless demanded.

### 2.4 Legacy agent sunset (Phase 2–3)
Policy decision (18 Q3): mark `Harbora.Agent` deprecated in docs + UI badge ("legacy — plan migration"), stop documenting it in RUNBOOK, keep runtime support ≥ 2 minor versions. It has zero tests — freeze changes.

### 2.5 Scheduler & capacity (Phase 3)
- Re-check quota/capacity at deploy-queue time (R-22) — cheap read, refuse with the same friendly reasons used at create.
- Node pools management UI (list/tag/assign) — model exists (`Server.Pool`, `Plan.NodePool`).
- OS-level port burn list (R-23).

## 3. Deployment capability growth (Phases 3–5, from 05)
Order: retry action (S) → build args (S) → queue transparency (above) → PR previews (L — finish `PreviewEnvironmentService` lifecycle; webhook events for PR open/close exist for GitHub/Gitea payloads? verify — open question Q5) → repo IaC apply (L) → multi-arch builds (XL, defer; needs buildx or per-arch builders).

## 4. Required tests (roll into 15)
- Concurrency matrix (1.1), timeout/retry (1.2), truth (1.3), boot-race (1.3).
- Live-host node E2E lane (2.1) — the single most important missing CI lane; also closes the "verified by tests only" caveat on v1 nodes/tunnel/module (02 §15).
- Contract additions get schema+conformance updates in the same PR (existing discipline — keep).

## 5. Risks
- Parallel workers expose hidden shared-state assumptions (mitigate: per-target serialization default-strict; feature-flag the pool).
- Proxy-verify probe can false-negative behind Cloudflare (make probe target `127.0.0.1` with Host header, as install.sh already does).
- Installer templating must stay idempotent (follow `backfill_env` pattern).
