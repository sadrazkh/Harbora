# 13 — Target Architecture (evolutionary)

**Headline: keep the modular monolith.** One ASP.NET control plane + Postgres + Traefik + out-of-process agents is the right shape for this product and team size. No microservices. Every proposal below is an in-place evolution with a migration path and protective tests. The Backup/Sync modules prove the codebase already knows how to grow modularly (own projects, contracts, feature flags, `PathGuard` moved to Shared rather than coupling).

## 1. Assessment per architectural concern

| Concern | Today | Verdict / target |
|---|---|---|
| Modular monolith | Core in `Infrastructure` (29k LOC) + 2 real modules | ✅ Keep. Grow new subsystems (Notification, Email) as `src/Modules/*` with Contracts/Domain/Infrastructure triplets like Backup. Do **not** retro-modularize the core now. |
| Control plane | Single Web app; 22 hosted services; break-glass admin verb | ✅ Keep. Document the single-instance constraint explicitly (see §4). |
| Node agent | v1 protocol: versioned contract, conformance tests, durable outbox/ledger, capability advertisement | ✅ Keep as-is — this is the best-engineered part of the repo. Additive-only within v1 (existing policy). |
| Deployment orchestrator | `DeploymentPipeline` 1,123 LOC / 14 deps | 🟡 Works and is test-protected; **do not rewrite**. Extract only along existing seams when touched: `ImageAcquisition` (build/pull/upload), `Cutover` (proxy+retire+ports), `HealthGate` — three internal classes, same behavior, cutover tests (`DeploymentPipelineCutoverTests`, 519 lines) must pass untouched. Trigger: Phase 3+, only when a feature forces edits there. |
| Build module | `Buildpacks` + per-source branches in pipeline | 🟡 Extract with ImageAcquisition; future builder-node concept stays out until multi-arch demanded. |
| Job queue | Durable Postgres queue, serial worker, no retry/timeout | ❗ The one structural change **needed now**: worker pool + per-target mutual exclusion + timeout/retry classes (06 §1). Protect with existing `DurableJobQueueTests` + new concurrency matrix. |
| Event system | None (direct service calls; alerts fire inline) | Target: minimal in-process domain events only where fan-out already hurts (deployment finished → notifications, metering, webhooks). Implement as the `NotificationEvent` table (09) — an **outbox, not a bus**. No MediatR-style rewiring of everything. |
| State machines | `DeploymentStateMachine`, `SnapshotLifecycle` — single write paths, illegal-transition throws | ✅ Keep pattern; add the missing one: `RestoreJob` transitions + node-command statuses already have one server-side. |
| Distributed lock | None (in-proc registries; DB optimistic concurrency) | Not needed while single-instance. When HA comes (P3): Postgres advisory locks — never Redis-for-locks (Redis is already unused; remove the dead dependency, R-38). |
| Idempotent jobs | `Idempotency-Key` on backup APIs; node-side full ledger | Extend the API pattern to deploy/restore endpoints (S). Job handlers should tolerate re-execution (deployment already does via state machine refusals). |
| Versioned agent protocol | `contracts/node-agent/v1` + schema/conformance/example tests | ✅ Keep; fix changelog drift (R-16); v2 only for breaking needs (exec channel). |
| Provider abstraction | `IDockerEngine` seam (3 impls); backup engines; AI providers | ✅ Sound. Fix the 3 seam violations (R-04). New: `IEmailProvider` (10), notification channel senders behind delivery jobs (09). |
| Integration architecture | Git providers (token+OAuth), Telegram/Discord/webhook senders, S3, registries | 🟡 Ad-hoc per integration but consistent (encrypted creds + test buttons). Formalize only via the two new subsystems; no generic "integration framework". |
| Plugin architecture | None | ❌ Defer. Templates + webhooks + (future) OpenAPI are the extension surface. Revisit only with a concrete third-party demand. |

## 2. Changes proposed, each with the mission's five questions

### 2.1 Job execution layer (now — Phase 1)
- **Problem:** serial platform (R-01), no timeout/retry (R-06), shutdown race (R-07).
- **Why now:** it's the multi-tenant credibility blocker and every later phase queues more job kinds (notifications, email).
- **Migration:** additive — worker pool behind `Jobs:MaxConcurrency` (default >1 after a soak), per-target claim filter, `JobKind` timeout table, startup gate ordering. Rollback = config to 1.
- **Don't change:** `Job` schema (add nothing but `MaxAttempts` default column if needed), claim/ClaimStamp mechanics, `JobSignal`.
- **Protective tests:** existing DurableJobQueue/Reconciler suites + new concurrency matrix, timeout, boot-race.

### 2.2 Backup convergence (Phases 1–2–8)
As 08 §A1: harden both, achieve parity in the module, only then default-flip; legacy becomes read-only "maintenance" after one release of overlap. Don't change: artifact formats mid-flight, `UpgradeSafetyService` (sacred — see 19).

### 2.3 Notification outbox + delivery jobs (Phase 9)
As 09 §4. Don't change: channel sender implementations (wrap, don't rewrite). Protective: `NotificationDeliveryTests` semantics preserved for send behavior.

### 2.4 Email service module (Phases 10/14)
New `src/Modules/Email/*` triplet + one new container (SMTP ingest) in compose behind a flag — exactly the MinIO precedent. Don't change: platform SMTP settings path (`PlatformMailer`) — 09 uses it; the module is for tenant apps.

### 2.5 Environment-model completion (Phase 3)
Finish `EnvironmentId` non-nullability + drop dual network attach (07 §2.1). This closes the repo's longest-open architectural transition. Protective: isolation + wiring tests exist.

### 2.6 API layer growth (Phases 3–11)
`/api/v1` grows to cover apps CRUD, env vars, domains, rollback, deployments list, cancel — driven by CLI needs (05 §C). Add OpenAPI generation. Contract rule: additive, versioned path, error contract already documented in `docs/cli-deploy.md` — keep it the source of truth. Idempotency keys on mutating deploy endpoints.

## 3. What must NOT change (summary — full list in 19)
State machines and their single-write-path rule; node contract discipline (schema+tests in same PR); engine-factory resolution order (local → v1 → legacy → throw, never silent fallback); upgrade safety chain (restore point → migrate → seed, fail-closed master key); "unmeasured ≠ zero" display layer; secret redaction path for logs; `LogText` NUL stripping; CLI deploy-mode precedence (14 pinned tests); PBKDF2/AES-GCM/DP-keyring choices (out of audit scope but flagged as do-not-touch).

## 4. Single-instance constraints register (make explicit, don't fix yet)
`JobCancellationRegistry`, `AlertThrottle` (dies with 09 dedup), AI rate-limit windows, `NodeIngressRegistry`/`NodeChannelRegistry` (per-instance sockets), SignalR groups, DP keyring on a volume (shared-ready ✅). HA/multi-panel is P3; when it comes: sticky node channels or a channel-owner table + Postgres advisory locks + distributed rate limits. Record this in README's limitations section (R-16 pass).

## 5. Dependency-direction rules to enforce (arch tests, Phase 2, S)
Add NetArchTest-style assertions: Domain references nothing; Application references Domain only; Modules reference Shared+Application, never Infrastructure internals; Web never touches `Harbora.Data.Migrations`; nothing outside Notifications sends via SmtpClient directly (post-09). These lock in the boundaries the codebase already mostly respects (the overhaul plan's P1 promised architecture tests that were never written).
