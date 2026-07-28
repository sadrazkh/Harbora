# 15 — Phase Plan (post-merge, reliability-first)

Written after PR #1 merged into `master` (`a18b217`). Doc 12 laid out P0–P15+ as a *forward* plan;
this doc records what actually landed versus what was claimed, and re-sequences the remaining work
around a constraint doc 12 assumed away: **there is currently no Docker host available for
verification.**

Priority chosen for this stretch: **make what exists trustworthy** before making it bigger.

---

## 1. Actual state after PR #1

| Doc-12 phase | Claimed | Actually landed |
|---|---|---|
| P0 baseline + CI | ✅ | Complete. CI activated at merge time (`.github/workflows/ci.yml`). |
| P1 protective tests | ✅ | 96 unit tests green. **No** integration harness (Testcontainers), **no** architecture tests. |
| P2 fail-closed + real deploy | ✅ | Fail-closed master key ✅. **A real deploy on a Docker host was never performed** ❌. Key-version scaffolding ❌. |
| P3 state machine + reconciler | ✅ | State machine + startup reconciler ✅. **Durable `Job` table not built** — the queue is still an in-memory `Channel`; the reconciler only compensates after the fact. Cancellation token not wired ❌. |
| P4 cutover + artifact rollback | ✅ | Versioned-container cutover ✅, artifact rollback ✅. **No image retention/prune policy** ❌. No pre-confirm rollback diff ❌. No probe fields ❌. |
| P5 design system | — | Not started. |
| P6 Create/Detail redesign | partial | Static-site source card + staged progress bar ✅. App Detail tabs ❌. |
| P7 Compose · Template · Static | partial | Static ✅, single-container Template ✅. **Compose ❌**, multi-service templates ❌. |
| P8 domains/SSL/logs | — | Not started. |
| P9–P12 | — | Not started. |
| P13 RBAC + audit | partial | Capability policies, `Operator` role, audit writes ✅. **Audit UI/CSV export ❌**, centralized workspace scoping ❌, IDOR/cross-tenant tests ❌. |
| P14 API/OpenAPI/webhooks | partial | Per-IP rate limiting landed early ✅. Rest ❌. |

## 2. Risks found in review that doc 12 did not anticipate

- **R1 — No image pruning exists anywhere in the codebase.** ✅ *Closed in Phase C.* Every deploy left
  `{prefix}/{slug}:build-{n}` on disk forever, and artifact rollback *depended* on those images
  surviving — so "instant rollback" worked only because nothing cleaned up, and broke the moment
  anyone ran `docker image prune`. Retention is now an explicit, configurable policy, and a rollback
  whose artifact is gone says so up front instead of failing mid-deploy.
- **R2 — The cutover path has zero behavioural coverage.** ✅ *Closed in Phase B.* Tests covered only
  the pure helpers in `DeploymentPlanning`; `DeploymentPipeline.ExecuteAsync`, which holds the actual
  ordering guarantee (start new → health → switch proxy → retire old), was never executed by a test
  and there was no fake `IDockerEngine`. Both now exist — and the first run of the harness surfaced a
  real `DbContext` threading bug that had been live since the overhaul landed.
- **R3 — Per-IP rate limiting and audit IPs are defeated by the shipped topology.** The panel runs
  behind Traefik (`deploy/docker-compose.yml`) but nothing configured forwarded headers, so every
  request carried the proxy's IP. Fixed in Phase A.

---

## 3. Phases

### Phase A — close the review findings
*Small · one PR · very low risk*

1. Trust forwarded headers from the proxy network only, before the rate limiter runs (R3).
2. Stop the single-active-deploy guard from silently swallowing a rollback request.
3. Actually apply the `Succeeded → RolledBack` transition the state machine already allows.
4. Route `DeploymentEngine.CancelAsync` through the state machine.

**AC:** each fix has a test; a spoofed `X-Forwarded-For` from an untrusted source is ignored.

### Phase B — pipeline integration harness with a fake Docker engine ⭐ ✅ done
*Medium · low risk (test-only) · highest confidence per unit of effort*

> **Outcome:** 20 tests over the real `ExecuteAsync`; every mutation tried was caught. Found and
> fixed a genuine `DbContext` race — build-log lines were written from a thread-pool thread while
> the pipeline thread was mid-`SaveChangesAsync`. Health-gate timings became configurable as a
> side effect (closes P4's owed "probe fields"). See `progress.md`, 2026-07-28.

The honest substitute for the E2E run we cannot perform. A recording `FakeDockerEngine` plus
end-to-end execution of `DeploymentPipeline.ExecuteAsync`, asserting on **call order**, not just
return values:

- the new container is started **before** any old container is removed;
- on failure, `RemoveContainer` is called **only** for the new container and the previous one is
  left untouched;
- `WireProxy` happens **before** `RetireOldContainers`, never the reverse;
- a rollback never calls `BuildImage` and releases exactly the target image;
- a failed health check ends in `Failed` and does not switch traffic;
- on a remote node, old and new published host ports do not collide.

**Why it matters:** every headline claim of PR #1 gets verified as *behaviour* for the first time,
and the assertions become the precise specification for the real E2E run once a host exists.

### Phase C — image retention + resilient rollback ✅ done
*Medium · medium risk (deletes data)*

- ✅ Keep the last *k* images per app (plus the active deployment's image); prune the rest after a
  successful cutover. `Runtime:ImageRetentionCount`, default 5.
- ✅ Verify the image still exists and fail with a clear message up front, rather than part-way
  through a deploy — `IRollbackPlanner`, plus a re-check in the pipeline.
- ✅ Show which commit/image a rollback targets before the user confirms (owed from P4).

> **Outcome:** `IDockerEngine` had no image operations at all, so this phase began by adding
> list/exists/remove across all four engines (local, remote, agent, fake). Retention deliberately
> never touches user-supplied images, ignores failed deployments, and dedupes by tag so a rollback
> can't shrink the window. Mutation testing caught one weak test, which was rewritten. See
> `progress.md`, 2026-07-28.

### Phase D — durable job queue (completes P3)
*Medium · medium risk (core path)*

Replace the in-memory `Channel` with a persisted `Job` table so a `Queued` deployment genuinely
survives a restart instead of being re-queued into another volatile channel. Wire real
`CancellationToken` support so `CancelAsync` stops work in progress.

### Phase E — data-safety hardening
*Large · high risk (data)*

Backup→restore round-trip verification, archive encryption, dry-run restore (P10). Complete P13:
audit log UI + CSV export (entries are written today but nothing can read them), centralized
workspace scoping, cross-tenant/IDOR tests.

---

## 4. Blocked — requires a Docker host

Recorded, not dropped. Revisit as soon as a host is available:

- Real install + deploy E2E verification (P2).
- Docker Compose deploys (P7) — cannot be honestly verified without a host.
- Testcontainers-based Postgres integration tests (P1) — Testcontainers itself needs Docker.
- ACME/SSL path against a real domain (P8).
