# 05 — Feature Gap Analysis

Consolidates 02 (inventory) into actionable gaps. Every item reappears in `backlog.json` with an ID. Competitor grounding from 11 §A (researched 2026-08-06/07). Scope: S ≤ 2 dev-days · M ≤ 2 weeks · L ≤ 6 weeks · XL > 6 weeks.

## A. Gaps that block trustworthy production use (pair with 03's P0s)

| Gap | Why it matters | Scope | MVP | Full | Acceptance (MVP) |
|---|---|---|---|---|---|
| Parallel job execution w/ per-target locks (R-01) | Multi-tenant credibility | M | N workers + per-app serialization | priority lanes, per-kind pools | two apps deploy concurrently; same app serial; test proves backup ∥ deploy |
| Backup crash-recovery + DB-level mutual exclusion (R-02/28) | Backup trust | M | boot reconciler + stuck-clear + unique index | staging sweeper, resumable snapshots | kill-mid-backup → next schedule succeeds |
| Deployment truthfulness on proxy failure (R-03) | "Honest software" principle | S | apply failure → `Failed` + alert | post-cutover probe through Traefik | failing proxy ⇒ deployment Failed |
| Multi-node backup correctness (R-04) | Data loss class | M | engine-factory everywhere + explicit refusals | dispatch node Snapshot/Restore verbs | remote-service backup uses remote engine or refuses loudly |
| Node v1 out-of-box activation (R-05) | Flagship feature | M | installer templates mTLS + CA export + env | `node-ca` verb, verify step | fresh install enrolls a node with zero manual file edits |
| Job timeout + selective retry (R-06) | hung-build freeze | M | per-kind timeout | retry classes + backoff | hung build times out; queue advances |

## B. High-value product gaps (P1 — "usable version" tier)

### B1. Deployment & build
- **Build timeout & cancel from CLI/UI everywhere** (S/M) — cancel exists in-process; expose queue position + cancel button on Queued cards; CLI `harbora cancel <id>`.
- **Deploy retry action** (S) — re-enqueue failed deployment with same config snapshot.
- **PR preview environments — finish the lifecycle** (L) — pieces exist (`PreviewEnvironmentService`, `PreviewSweeper`, per-branch fields). MVP: open PR → preview env + URL comment-ready; merge/close → destroy. This is the single most "wow" competitive flow (Vercel/Render evidence, 11 §A). Acceptance: branch push creates `pr-{n}.{app}.{root}` and teardown is automatic (idle 7 d already enforced).
- **Repo-committed IaC (`harbora.yml` service block)** (L) — Render Blueprints analog; extend the existing CLI yml (parser already tolerates unknown keys) to declare env/domains/volumes; panel "apply from repo" with diff preview. Start by making `env:`/`domains:` in `examples/harbora.yml` real (R-37).
- **Build args + monorepo watch filters** (M).

### B2. Operations
- **Retention sweeper for logs/audit/idempotency/etc.** (M) — R-14.
- **Scheduled disk cleanup incl. buildkit cache + pre-build free-space gate** (M) — R-24.
- **Alert edit/toggle + digest** (M) — R-33 + 09 plan.
- **Uptime/restart-count collection** (M) — containers report `RestartCount`/`StartedAt` already in agent contract; persist per app, chart, alert on crash-loop rate. (Response time/error rate stay out — need ingress instrumentation, P3.)

### B3. Databases & storage
- **Fix external-access rotation (R-08)**; real node TCP gateway (L, phase-gated with node GA).
- **DB logical export/import (dump download / restore upload)** (M) — the engines' own tools are already containerized for backups; reuse.
- **Connection snippets per language** (S) — template-driven strings on the connection panel.
- **Engine version upgrade flow** (L) — snapshot → new container on new version → verify → cutover, PG/MySQL/MariaDB first.
- **Orphan volume report** (M) — R-43.

### B4. Nodes
- **Populate heartbeat gauges (R-25) + publish declared node events (R-26)** (S/M).
- **App move between servers** (L) — volume snapshot/restore verbs + port/route rewire; refuse when source is legacy agent.
- **Node build capability** — keep **out** (contract stance is sane; builds on panel or future dedicated builder), document clearly instead (S).

### B5. Teams
- **Per-app access grants** (M) — extend `ProjectGrant` shape with optional `AppId`.
- **Ownership transfer UI** (S).
- **Service accounts / workspace-scoped tokens** (M) — tokens currently user-bound.

## C. Product-completion gaps (P2)

- Marketplace: screenshots, backup notes, curated growth to ~30 apps with a validation harness (each app deploy-tested in CI on a real host — competitor lesson: curated-30 beats unmaintained-280), fleet-wide "update available" view. (L)
- Multi-service template deploy-as-unit: resolve the docs contradiction by a live-host proof test; then allow broker requirements (R-30). (M)
- CLI phase 2: `apps create`, `env set/list`, `domains add`, `rollback`, `deployments list`, `logs --app --tail`, `--json` everywhere, shell completion. Driven by expanding `/api/v1` (see 13 §API). (L)
- OpenAPI for `/api/v1` + generated docs page. (M)
- Custom certificate upload; DNS-01/wildcard via provider plug-ins (Cloudflare first). (L)
- Real client IP / proxy-protocol guidance per app; gRPC/h2c toggle on routes. (M)
- Registration/self-serve signup with email verification — needed only when Harbora hosts strangers; keep behind a flag. (M)
- Import/export of env var sets; variable groups. (M)
- App maintenance mode (static page via Traefik middleware). (S)
- Duplicate app; archive app. (M)
- Backup module GA path: verify enqueue (R-10), safety snapshot (R-11), Kopia strategy honesty (R-27), Kopia maintenance schedule, S3-for-Kopia decision, then default-on. (L)
- Metering completeness: managed services + volumes + buckets into `UsageRecord`. (M)
- Node pools UI; anti-affinity hints. (M)

## D. Explicit non-gaps (deliberately absent — keep it that way for now)
Kubernetes/Swarm orchestration; microservice split; multi-region; autoscaling; scale-to-zero; in-house APM; payment processing; GPU support (Fly.io cautionary tale); CDN/edge; per-second metered billing. Rationale: 11 §A cross-cutting takeaways + `docs/overhaul/03-feature-matrix.md` REJECT row still stands.

## E. Competitor-inspired opportunities ranked by (user value ÷ effort)

1. **Attach-injects-env** — already shipped ✅ (Heroku's best idea; keep and market).
2. **PR preview URLs** (B1) — highest wow-per-effort remaining.
3. **Dev inbox for email** (see 10) — no self-hosted PaaS peer has it.
4. **Repo IaC file** (B1).
5. **Portainer-style escape hatch** — one click from app → raw container inspect/logs/exec; terminal exists, add inspect view. (M)
6. **Railway-style reference variables** — `${{service.VAR}}` cross-service references; template engine already resolves references, generalize to user apps. (M)
7. **Coolify-style Cloud pricing shape** (hosted control plane managing your servers) — business option, not code; noted in 20.
