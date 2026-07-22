# Harbora Overhaul — Progress Log

Newest entries on top. Every entry records: what was done · files changed · tests/checks run ·
result (success/fail) · decisions · next step.

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
