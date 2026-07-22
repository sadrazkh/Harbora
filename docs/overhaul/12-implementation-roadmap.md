# 12 — Implementation Roadmap

Phased, low-risk execution. Each phase: **Goal · Scope · Files/Modules · Dependencies · Risk ·
Acceptance Criteria (AC) · Test Strategy · Rollback · Definition of Done (DoD).** Work happens on
branch `overhaul` in small atomic PRs; after every phase Build + Test + Lint must be green and
`progress.md` updated. Phases map to requirements (doc 05 `R-*`), ADRs (doc 09), and threats
(doc 10).

Scope tiers: **Foundation** (P0–P2) → **Usable MVP** (P3–P8) → **Version 1** (P9–P14) →
**V1.x/Later** (P15+).

---

## P0 — Baseline & guardrails (Foundation)
- **Goal:** a reproducible, verified starting point + CI gate.
- **Scope:** create `overhaul` branch; add solution **build+test CI** (GitHub Actions) incl. the
  Vue bundle build; fix the 3 compiler warnings; add editorconfig/lint; add a `docs/overhaul` index.
- **Files:** `.github/workflows/ci.yml`, `.editorconfig`, minor `*.cs` (warnings).
- **Deps:** none. **Risk:** very low.
- **AC:** CI builds all projects + frontend and runs tests on every PR; warnings = 0.
- **Test:** CI green on a no-op PR. **Rollback:** revert workflow.
- **DoD:** branch + CI live; baseline recorded in `progress.md`.

## P1 — Protective test harness (Foundation) — *fixes C1*
- **Goal:** a safety net before refactors.
- **Scope:** add `Harbora.Tests` (xUnit) with **characterization tests** around pure/critical
  logic: `TraefikProxyEngine` render/validate (golden files), `Buildpacks.Detect`, slug/host
  derivation, `AesGcmSecretProtector` round-trip, `SecretRedactor`, quota/scheduler math; add an
  integration harness using Testcontainers/ephemeral Postgres for repo/DbContext tests; architecture
  tests for module boundaries.
- **Files:** new test project; no production behavior change.
- **Deps:** P0. **Risk:** low.
- **AC:** meaningful coverage on the modules P4–P8 will touch; tests run in CI.
- **Test:** the tests themselves; mutation-spot-check on the Traefik renderer.
- **Rollback:** none needed (additive). **DoD:** green suite in CI; documented in doc 13.

## P2 — Runtime baseline + security fail-closed (Foundation) — *fixes 10.§2.2*
- **Goal:** prove the real deploy path once; make secrets safe by default.
- **Scope:** on a Docker host, reproduce install + run **one real deploy** (image + git) and record
  it; implement master-key **fail-closed in Production** (ADR-009) with a precise startup message;
  add key-version scaffolding.
- **Files:** `AesGcmSecretProtector`, `DependencyInjection`, `Program.cs`, docs.
- **Deps:** P1. **Risk:** low (guarded by env the installer already sets).
- **AC:** prod boot without key → clear fatal error; dev unchanged; a real deploy succeeds end-to-end
  and is logged in `progress.md`.
- **Test:** unit (startup check), manual E2E deploy smoke. **Rollback:** revert startup check.
- **DoD:** verified deploy + fail-closed shipped; migration note written (doc 11).

## P3 — Deployment state machine + crash reconciler (MVP) — *fixes C2/H1; ADR-004/005*
- **Goal:** observable, recoverable deploys.
- **Scope:** explicit persisted state machine (Queued→Building→Pushing→Deploying→HealthChecking→
  Live/Failed/Cancelled/RolledBack) with guarded transitions; durable `Job` table + boot reconciler;
  wire cancellation to a token.
- **Files:** `Deployment` (+migration), `DeploymentEngine`, `DeploymentPipeline`, new
  `Jobs/DurableJobQueue`, `Reconciler` hosted service.
- **Deps:** P1–P2. **Risk:** medium (core path). **Mitigation:** feature-flag; keep old queue as
  in-process fast path; extensive transition tests.
- **AC:** kill mid-deploy → on restart no deploy stuck in a non-terminal state; every transition is
  logged/audited; duplicate triggers de-duped.
- **Test:** transition unit tests (all paths), reconciler integration test (kill/restart), concurrency
  test. **Rollback:** flag off → previous behavior.
- **DoD:** R-DEP-5/6/9 met; documented; `progress.md` updated.

## P4 — Zero-downtime cutover + artifact rollback (MVP) — *fixes C4; ADR-006/007*
- **Goal:** safe deploys + instant rollback.
- **Scope:** start new container → startup+readiness probes → **switch Traefik target** → retire old;
  retain last *k* build images; rollback = re-point to a prior artifact (no rebuild) with a
  pre-confirm diff.
- **Files:** `DeploymentPipeline`, `TraefikProxyEngine` (service target swap), `AppOperationsService`,
  image-prune policy, app probe fields (+migration), rollback UI.
- **Deps:** P3. **Risk:** medium. **Mitigation:** golden-file proxy tests; probe defaults; canary on
  local first.
- **AC:** a failing new container never interrupts the live version; rollback completes in seconds
  without a build; diff shown before confirm.
- **Test:** integration deploy-fail scenario (old stays live), rollback timing test, probe unit tests.
- **Rollback:** flag off → replace-in-place path. **DoD:** R-DEP-7/8, R-HLT-1 met.

## P5 — Design system + app shell (MVP) — *ADR-010; doc 08*
- **Goal:** the UI foundation for redesigned flows.
- **Scope:** tokenize palette (dark+light parity), formalize components (button/badge/tabs/empty/
  loading/error/**staged progress**/confirm-with-diff), build a **command palette** island + real
  global search backend, replace the dead search box, ship a live **design-system reference page**.
- **Files:** `Scripts/*`, `app.css`, `tailwind.config.js`, new island, `_Layout.cshtml`, a
  `SearchController`, resource strings (fa/en).
- **Deps:** P0 (CI builds bundle). **Risk:** low-medium (visual). **Mitigation:** keep fallback CSS;
  snapshot key pages.
- **AC:** palette + search work; light/dark/system + RTL/LTR verified; states available as reusable
  partials.
- **Test:** frontend build in CI; a11y checks; manual RTL/theme pass. **Rollback:** revert island;
  shell still renders. **DoD:** R-UX-1/2/3 foundations shipped.

## P6 — Create App + App Detail redesign (MVP) — *doc 06/08*
- **Goal:** expose all real sources; make the app page operable.
- **Scope:** source **card grid** (Git/Dockerfile/Compose/Image/Static/Template — gated on
  implemented sources), progressive advanced; tabbed **App Detail** (Overview/Deployments/Logs/Env/
  Domains/Metrics/Settings); staged deploy-progress on the deployment page.
- **Files:** `Apps/Create.cshtml`, `Apps/Details.cshtml`, `Deployments/Details.cshtml`,
  `AppsController`, view models.
- **Deps:** P3–P5. **Risk:** low-medium. **AC:** all six sources selectable when enabled; app detail
  tabs functional; deploy shows staged progress + recovery actions.
- **Test:** controller tests; UI walk. **Rollback:** revert views. **DoD:** flows 4–6 (doc 06) met.

## P7 — Compose · Template · Static deploys (MVP) — *fixes C3; ADR-014; R-DEP-2/3/4*
- **Goal:** ship what the README claims.
- **Scope:** implement Compose (supported-directive allowlist, multi-service on tenant net, route web
  service), Template manifest → deploy synthesis (incl. required services, e.g. WordPress+DB), Static
  via Nginx buildpack path; flip the P2 feature flag on.
- **Files:** `DeploymentPipeline`/new `ComposeDeployer`, `TemplateResolver`, `Buildpacks`,
  `ManagedServiceEngine` (template deps), UI enablement.
- **Deps:** P3–P6. **Risk:** medium-high (input variety). **Mitigation:** strict allowlist + clear
  unsupported-directive errors; per-source tests; start with a curated template set.
- **AC:** each listed template deploys to a working app; a sample compose stack deploys + routes;
  static repo serves over HTTPS.
- **Test:** per-source integration deploys; malicious-compose rejection tests (doc 10 §2.6).
- **Rollback:** re-gate the flag. **DoD:** R-DEP-2/3/4, R-TPL-1 met; README honest.

## P8 — Domains/SSL polish + Logs/History (MVP) — *R-NET-3, R-LOG-2*
- **Goal:** finish the MVP safety/clarity essentials.
- **Scope:** DNS-guidance component (exact records + live resolve check), SSL status/reason per
  domain; unified build+runtime log view with filtering + download; deployment history polish.
- **Files:** `Domains/*`, `DomainsController`, log view/island, `DeploymentsController`.
- **Deps:** P4–P6. **Risk:** low. **AC:** domain screen shows records + resolves-state; ACME failures
  show the reason; one log view covers both phases.
- **Test:** controller + island tests; manual SSL path on a host. **Rollback:** revert views.
- **DoD:** MVP definition met — **reliable Deploy, Domain, SSL, Logs, Database, Backup, Monitoring**.

> **MVP gate:** after P8, Harbora reliably delivers deploy→domain→SSL→logs→DB→backup→monitoring with
> zero-downtime cutover, rollback, and crash recovery. Ship a milestone build.

## P9 — Database provisioning verify + attach-env (V1) — *R-DB-1, R-ENV-2 (basic)*
- Verify managed DBs on a host; auto-inject connection env on attach; service detail page. Risk med;
  AC: attach wires env with no copy-paste. Tests: provision+attach integration. Rollback: revert
  attach injection.

## P10 — Backups & Restore hardening (V1) — *R-BAK-1/2/3; doc 10 §2.14*
- Live-verify tar/restore; encrypt archives; dry-run restore; auto pre-upgrade/pre-restore backups;
  scheduled S3 at every tier. Risk med (data). AC: restore returns exact state; archives encrypted;
  dry-run reports diffs. Tests: backup→restore round-trip integration. Rollback: keep prior engine
  behind flag.

## P11 — Monitoring & Alerting depth (V1) — *R-MON-2/4; ADR-013*
- Per-app + per-route metrics (Traefik), threshold alerts (CPU/mem/disk/consecutive-fails),
  Prometheus-friendly export. Risk low-med. AC: per-app charts + threshold alerts fire. Tests:
  collector unit + alert-rule tests. Rollback: revert collectors.

## P12 — Multi-server reliability (V1) — *R-SRV-2; ADR-008*
- Tracked `HostPortAllocation` (replace hash); agent version negotiation; node health UX. Risk med.
  AC: no cross-node port collisions; stale agent prompts update. Tests: allocation + multi-node
  integration. Rollback: fall back to hash behind flag.

## P13 — RBAC + Audit (V1) — *R-RBAC-1/2, R-AUD-1; doc 10 §2.11/2.12/2.13*
- Authorization policies per action on UI **and** API; Operator role; centralized workspace scoping;
  audit all privileged actions + UI/CSV export. Risk med (access changes). AC: role matrix enforced
  (Viewer/Developer blocked from privileged POST/API); every privileged action audited. Tests: full
  authz matrix tests; IDOR/cross-tenant tests. Rollback: policies are additive; can loosen if a
  legitimate action is over-blocked (with a test).

## P14 — CLI/API + OpenAPI + webhooks (V1) — *R-API-1, R-HOOK-1, R-BLD-2*
- Documented `/api/v1` + OpenAPI + versioning policy; per-app deploy webhook (HMAC + de-dup);
  `harbora.yaml` as source of truth. Risk low-med. AC: OpenAPI published; webhook triggers deploy;
  `harbora.yaml` drives a repeatable deploy. Tests: API contract tests; webhook forgery/dup tests.
  Rollback: additive.

## P15+ — Later (post-V1, prioritized)
- **PR/preview environments** (Traefik weighted routes) — R-DEP-12.
- **In-browser DB client** — R-DB-2 (top differentiator).
- **DNS-provider automation** — R-NET-5.
- **Cross-service env refs + env groups** — R-ENV-2/3.
- **Named routing slots / traffic splitting / canary** — ADR-006 extension.
- **Horizontal replicas → autoscaling (RPS/concurrency)** — R-SCL-2.
- **Topology/canvas view** — UX delight for multi-service.
- **Community template marketplace** — R-TPL-2.
- **AI Gateway (gated by demand validation)** — R-AI-1.
- **Billing/invoicing on metering; optional 2FA/SSO; optional K8s target** — validate demand first.

---

## Cross-phase rules
- Small atomic PRs; each keeps Build+Test+Lint green and updates `progress.md`.
- Touch runtime behavior only behind a flag until verified on a Docker host.
- Never delete a working, healthy capability without cause; preserve API/data compatibility (doc 11).
- Stop and ask only for the brief's escalation cases (mass deletion, unsafe breaking change, stack
  change, external cost, product-direction shift, irreversible/prod-credential ops). Otherwise
  decide, record, proceed.

## Indicative sequencing (not calendar-bound)
Foundation P0–P2 → MVP P3–P8 (the bulk of user value + safety) → V1 P9–P14 → Later P15+. Security
threats (doc 10) are addressed inside the phase that owns the surface (fail-closed in P2, authz/audit
in P13, build sandbox in P7, SSRF/path in P7–P8, webhook/rate-limit in P14).
