# 13 — Test Strategy

Harbora currently has **zero automated tests**. Since the overhaul refactors core paths
(deployment, proxy, security, RBAC), tests are the prerequisite, not an afterthought. This strategy
defines what to test, at which level, and the gates that protect every change.

## 1. Objectives & principles

- **Protect before refactor:** land characterization tests around existing behavior *before*
  changing it (P1 in doc 12).
- **Test behavior, not implementation:** assert observable outcomes (rendered config, state
  transitions, HTTP results), so refactors don't churn tests.
- **Fast feedback:** the unit tier runs in seconds in CI on every PR; heavier tiers gate merges to
  `overhaul`/`master`.
- **No test is skipped or weakened to go green.** Failures are fixed or the change is reverted.
- **Real, not mocked, where it matters:** container/DB behavior is validated against ephemeral real
  services (Testcontainers), not hand-rolled fakes that can lie.

## 2. Test pyramid

**Tier 1 — Unit (most numerous, pure/fast).** No I/O. Targets:
- `TraefikProxyEngine` render + validate → **golden-file** tests (router/service/middleware YAML for
  host/path/redirect/headers/basic-auth/priority/SSL); invalid-route rejection.
- `Buildpacks.Detect` for each stack + precedence + "no match" (and the .NET version pin).
- Slug/host derivation, uniqueness, `AllocateHostPort` replacement logic.
- `AesGcmSecretProtector` round-trip + tamper/for-wrong-key failure; `SecretRedactor` redacts all
  secret shapes; master-key fail-closed check.
- Quota math, scheduler placement, metering calculations.
- **Deployment state-machine transitions:** every legal transition succeeds, every illegal one is
  rejected (exhaustive).

**Tier 2 — Integration (real Postgres + Docker via Testcontainers).** Targets:
- EF repositories/migrations against real Postgres; migration up + backfill correctness; workspace
  query scoping (no cross-tenant leakage).
- Deployment pipeline against a real Docker daemon: image + git + Dockerfile deploy → running
  container → health → route; **deploy-fail keeps old version live** (zero-downtime);
  **artifact rollback** re-points without rebuild.
- **Crash reconciler:** start a deploy, kill the process, restart → no stuck non-terminal deploy.
- **Concurrency/idempotency:** two concurrent deploys → one active; duplicate webhook delivery →
  one deploy.
- Managed DB provision + attach-env; backup → restore round-trip (state restored).
- Compose deploy (multi-service) + **malicious-compose rejection** (privileged/host-mount/socket).
- Traefik apply/rollback on forced write failure (`.bak` restored).

**Tier 3 — API/contract.** `/api/v1` endpoints: auth (token required), shapes stable (snapshot),
authorization matrix (role × action), webhook HMAC verify + de-dup, error formats. OpenAPI kept in
sync (contract test).

**Tier 4 — Security tests.** Authorization matrix (Viewer/Developer blocked from privileged POST/
API); IDOR (accessing another workspace's object id → 404/403); SSRF guards (user URL to
loopback/link-local/RFC1918 blocked); path-traversal (`../`, absolute, symlink) rejected;
secret-never-exposed (API/logs); rate-limit on auth/webhooks.

**Tier 5 — Frontend/E2E (thin but real).** CI must **build the Vue bundle** (fail on error).
Smoke E2E (Playwright) on key flows against a running panel + Docker: setup → create app → deploy
(image) → see staged progress → app Live → rollback; RTL + light/dark render without layout break;
command palette navigates. Accessibility checks (axe) on core pages.

## 3. Coverage targets (guidance, not vanity)

- Core domain/logic (Domain, Application, Infrastructure deploy/proxy/security/tenancy): **high**
  unit coverage; every state transition and every Traefik render branch covered.
- Controllers/API: covered by contract + authz tests (not line-chasing).
- Overall gate: coverage **must not decrease** PR-over-PR; new/changed core code ships with tests.
- Critical paths (deploy state machine, proxy render, secret protector, authz) are the non-negotiable
  "must be tested" set.

## 4. CI gates (doc 12 P0)

On every PR to `overhaul`/`master`:
1. `dotnet build -c Release` (0 warnings-as-errors for new code).
2. Tier 1 unit tests.
3. Frontend `npm ci && npm run build` (bundle must build).
4. Tier 2/3 integration + contract tests (Testcontainers; Docker available on the runner).
5. Security tests (Tier 4).
6. Lint/format (`dotnet format`, editorconfig) + architecture-boundary tests.
Merges are blocked unless all pass. Tier 5 E2E runs on a nightly/pre-release job (heavier).

## 5. Test data & safety

- No real secrets/tokens in fixtures — generate ephemeral values; assert the **redactor** hides
  them. Use disposable Testcontainers volumes; never touch a real host's Docker state in CI beyond
  the sandboxed daemon. Backup/restore tests use throwaway volumes.

## 6. Tooling

xUnit + FluentAssertions; Testcontainers for Postgres/Docker; `WebApplicationFactory` for API/
contract; Playwright for E2E; `axe-core` for a11y; `dotnet format` + editorconfig for lint;
coverage via `coverlet`. Golden files stored under the test project and reviewed on change.

## 7. Definition of "tested" for a phase

A roadmap phase is not Done until: its `R-*` requirements have matching tests at the right tier, the
tests run in CI, the suite is green, coverage didn't drop, and — for any phase touching runtime — a
real deploy/backup smoke test passed on a Docker host and is recorded in `progress.md`.
