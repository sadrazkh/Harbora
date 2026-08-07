# 15 — Test Strategy

## 1. Current baseline (strong, with three structural holes)
3,141 tests green. Strengths worth preserving: pure-rule extraction (`*Plan`, `*Rule` classes) making behavior unit-testable; source-reading UI convention tests (localization, navigation, secrets-on-screen, route-value collisions); contract conformance suite (schema ↔ C# ↔ examples); `DockerFactAttribute` honest-skip pattern with CI's TRX `NotExecuted` guard; 32 manual mutation scripts (with the known mtime trap documented in team memory); `MigrationConsistencyTests`.

**Holes:**
1. **No HTTP-level integration** — zero `WebApplicationFactory`/TestServer; controllers tested only as classes (6 files). Auth pipeline, filters, antiforgery, rate limits, SetupGuard, culture middleware all unexercised.
2. **No real-database tests** — EF InMemory everywhere; Npgsql behaviors (query filters + `ExecuteDeleteAsync`, indexes, concurrency tokens) unproven locally; Testcontainers was planned (`15-phase-plan.md`) and never landed. (Local constraint: audit machine has Postgres but no creds and no Docker — CI must own this.)
3. **No live-host E2E lane** — install → enroll node → deploy → domain → backup → restore chain is verified manually per progress.md, not by CI (roadmap R0).

Untested named surfaces: `SetupController`, `WebhooksController`, `MonitoringController`, `TerminalController`, `StorageController`, `PlansController`, `AdminSettingsController`, `DomainsController`, `RoutesController`, SignalR hub, `NodeAgentWorker` loop, `MetricsEndpoint`/`NodeMetrics.Render`, `TraefikProxyEngine.ApplyAsync` rollback, `deploy/install.sh`, `deploy/harbora`, entire `Harbora.Agent`.

## 2. Target pyramid & new lanes

| Lane | Tooling | Runs | Contents |
|---|---|---|---|
| L1 Unit (exists) | xunit + InMemory | every PR | keep growing per feature |
| L2 Postgres integration (new) | Testcontainers postgres:16 | every PR (CI) | query-filter semantics, migrations up-from-empty + up-from-N-1, retention sweeps, job claim under real concurrency, unique-index guards (R-02) |
| L3 HTTP integration (new) | `WebApplicationFactory` + Testcontainers | every PR | auth/capability matrix per route group, setup guard, webhook HMAC/token paths, rate limiter 429s, API v1 contract incl. error bodies, antiforgery |
| L4 Node/Docker (exists) | DockerFact + ingress harness | PR (agent workflow) | unify into main ci.yml (R-41) |
| L5 Live-host E2E (new — **R0**) | scripted VPS or nested-VM runner | nightly + release | install.sh → panel healthy → enroll node → deploy git+image+compose+template → domain+ACME(staging CA) → backup+restore drill → upgrade from previous release → uninstall |
| L6 UI smoke (new, small) | Playwright | nightly | login, create app (fake deploy), RTL toggle, mobile viewport sanity, palette, notification center once built |
| L7 Mutation (exists, manual) | scripts/mutate-*.py | on demand | leave manual; document runner order (mtime trap) |

CI consolidation (R-41): one PR workflow running L1–L4 (NodeAgent+NodeIngress suites join ci.yml), shellcheck for `deploy/*.sh` + `deploy/harbora`, CLI release checksums, fix NuGet gate.

## 3. Mission scenario matrix (Given/When/Then — the ones that must exist)

**Deployment truth (R-03)**
- Given an app with a domain and a proxy engine that fails apply · When deploy completes health checks · Then status is `Failed`, alert raised, previous route file restored.

**Queue resilience (R-01/06/07)**
- Given app A building slowly · When app B deploys · Then B completes before A (different targets).
- Given a hung build · When timeout elapses · Then job `Failed(TimedOut)`, worker advances.
- Given a job returned to Pending by shutdown · When panel restarts · Then exactly one terminal transition, no illegal-transition throw.

**Backup crash-safety (R-02)**
- Given a snapshot in `Running` and a killed panel · When panel restarts · Then snapshot `Failed (interrupted)`, staging cleaned, next `QueueAsync` accepted.
- Given two concurrent snapshot requests for one target · Then exactly one row (DB constraint), second returns "already running".

**Multi-node data truth (R-04)**
- Given a service on remote server S · When backup runs · Then engine resolved for S; if capability absent → refusal recorded, never a success with wrong bytes.

**Node lifecycle**
- Given an enrolled node and a panel restart mid-command · Then command result arrives via durable outbox replay exactly once (dedup ack).
- Given agent update to a bad binary · When service restarts · Then previous binary restored, `agent-update.rolled-back` event, node reconnects. (exists agent-side — add panel-side assertion)
- Given node offline > threshold · Then NodeOffline notification once; recovery emits resolved. (new with 09)

**Restore safety (R-11)**
- Given a database restore request · When it starts · Then a safety snapshot exists first or the restore refuses.

**Domain/SSL**
- Given DNS not pointing at the server · When domain added · Then UI check shows the exact failing record; no cert claimed. (probe logic unit-tested today; add L3 route test + L5 real ACME-staging)

**Upgrade (exists at plan level — add L5 proof)**
- Given schema changes pending and a failing dump · When panel boots · Then migration refused, exit non-zero, doctor names it.

**Notifications (09)** — routing matrix, dedup window, retry, digest, culture, quiet hours (09 §7).
**Email service (10)** — sandbox gates, rotation race, capture isolation, provider outage queueing (10 §9).
**Concurrency extras** — concurrent backup + deploy of same app's volume: mutual exclusion per target.
**Disk full** — build with a filled staging volume → clear failure naming disk, not opaque Docker error (pairs with R-24 gate).
**RTL/i18n** — extend existing source tests: no new `isFa ?` ternaries; every notification event has fa+en templates.
**ARM64** — publish-check matrix exists; add L5 on an ARM runner when available (P3).

## 4. Ownership & gates
- New feature PR = L1 + (L2/L3 where it touches DB/HTTP) in the same PR — mirrors the node-contract discipline.
- Phase exits (17) name their test lanes; Phase 1 exit requires the queue matrix + backup crash tests green in CI; node-GA exit requires L5 lane green twice consecutively.
- Flake policy: quarantine tag + issue within 24 h; the repo currently has zero flaky markers — protect that reputation.
