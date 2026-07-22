# Harbora Overhaul — Progress Log

Newest entries on top. Every entry records: what was done · files changed · tests/checks run ·
result (success/fail) · decisions · next step.

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
