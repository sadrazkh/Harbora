# 09 — Architecture Decision Records

Each ADR: **Context · Options · Decision · Rationale · Trade-offs · Risks · Migration.** These
record the *target* architecture for the overhaul. The overarching principle (from the brief): fit
the architecture to the real need, avoid unnecessary microservices/Kubernetes, and preserve what
already works.

Status legend: **Accepted** (do it) · **Keep** (ratify as-built) · **Proposed** (needs a checkpoint).

---

## ADR-001 — Modular monolith, not microservices  · **Keep/Accepted**
- **Context:** Harbora is one deployable panel + a thin remote agent + a CLI. Target users run it
  on 1–few VPSs.
- **Options:** (a) keep the modular monolith; (b) split into services (deploy, proxy, backups,
  monitoring); (c) event-driven microservices.
- **Decision:** (a). Keep the Clean-Architecture layering (Domain → Application ports →
  Infrastructure/Data → Web) with clear internal module boundaries.
- **Rationale:** the monolith already has clean seams; splitting adds deployment/ops burden with no
  user value at this scale. One process is easier to install, upgrade, and reason about.
- **Trade-offs:** must enforce module boundaries by discipline (namespaces + ports), not by network.
- **Risks:** boundary erosion over time. **Mitigation:** ports live in `Harbora.Application`;
  Infrastructure never referenced by Web except through interfaces; add architecture tests.
- **Migration:** none (ratify current structure); add module-boundary tests (doc 13).

## ADR-002 — Docker as the only runtime; no Kubernetes core · **Keep/Accepted**
- **Options:** (a) Docker Engine via `Docker.DotNet` (current); (b) require Docker + Compose CLI
  shelling; (c) Kubernetes; (d) pluggable runtime with a K8s target later.
- **Decision:** (a) as the core, structured behind `IDockerEngine`/`IServerEngineFactory`. Keep the
  door open for (d) as an *optional* future target only if demand is proven.
- **Rationale:** matches "simple self-host" thesis; typed API avoids shell-injection; K8s would
  multiply complexity against the product's core value.
- **Trade-offs:** no native multi-node scheduling/overlay networking (handled by our scheduler +
  published-port routing).
- **Risks:** cross-node networking is limited (documented). **Mitigation:** keep the runtime port
  clean so an overlay/K8s adapter can be added without touching callers.
- **Migration:** none.

## ADR-003 — Traefik dynamic-file config as the routing substrate · **Keep/Accepted**
- **Options:** (a) Traefik file provider with atomic-swap (current); (b) Traefik Docker-label
  provider; (c) Nginx templating (CapRover-style); (d) Caddy.
- **Decision:** (a). Continue rendering Traefik dynamic YAML with write-tmp → backup → swap →
  rollback-on-error, watched by Traefik.
- **Rationale:** already implemented correctly; gives us full control (previews, traffic weights,
  middlewares) and hot-reload without restarts; segment consensus is Traefik.
- **Trade-offs:** we own config generation/validation (already done in `TraefikProxyEngine`).
- **Risks:** malformed config could drop routes. **Mitigation:** validate before apply; keep
  `.bak`; add golden-file tests of the renderer (doc 13); consider `traefik` config dry-run.
- **Migration:** none; extend the renderer for weighted services (ADR-006).

## ADR-004 — Explicit, persisted Deployment State Machine · **Accepted** (fixes C2/H1)
- **Context:** status is currently an enum field mutated inline; no crash recovery; rollback is a
  re-deploy.
- **Options:** (a) formal state machine persisted in the DB with transition guards + timestamps +
  owner + reason; (b) a workflow engine (e.g., a library); (c) keep ad-hoc.
- **Decision:** (a), hand-rolled and small. States: `Queued → Building → Pushing → Deploying →
  HealthChecking → Live` plus `Failed / Cancelled / RolledBack`. Every transition is a method that
  validates the source state, persists the new state + timestamp + actor, and emits an event.
- **Rationale:** deployments are the heart of the product and must be observable, recoverable, and
  testable; a full workflow engine is overkill for one linear-ish machine.
- **Trade-offs:** more code than inline writes; must handle every transition explicitly.
- **Risks:** missed transition paths. **Mitigation:** exhaustive transition tests; a **reconciler**
  on boot (ADR-005).
- **Migration:** additive columns on `Deployment` (state-entered timestamps, `OwnerToken`, `Reason`);
  backfill existing rows to a terminal state; no data loss.

## ADR-005 — Durable job queue + boot reconciler · **Accepted** (fixes C2)
- **Context:** `ChannelBackgroundJobQueue` is in-memory; a restart strands in-flight work.
- **Options:** (a) persist jobs in Postgres (outbox-style) drained by the worker; (b) Redis streams
  (Redis is already in the stack); (c) a library (Hangfire/Quartz).
- **Decision:** (a) as the source of truth (simple, transactional with domain writes), with the
  in-memory channel kept as the in-process fast path. On boot, a **reconciler** scans non-terminal
  deployments/backups and resumes or fails them with a reason (idempotently).
- **Rationale:** Postgres gives durability + transactional enqueue with the same DB; avoids a new
  dependency; Redis remains for caching/pub-sub/log fan-out. Hangfire/Quartz add surface we don't
  need.
- **Trade-offs:** we implement claim/lease/retry semantics (small, well-understood).
- **Risks:** duplicate execution. **Mitigation:** idempotency keys per job + at-most-one-active-per-
  app lease (ADR-008).
- **Migration:** new `Job` table; feature-flag the durable path; keep channel behavior identical
  for callers.

## ADR-006 — Immutable image artifacts + traffic-switch rollback · **Accepted** (fixes C4/H1)
- **Options:** (a) retain each successful build image (`app:build-{n}`) and roll back by re-pointing
  the route/weight to a prior image; (b) rebuild from an old ref on rollback (current); (c)
  registry-backed artifacts.
- **Decision:** (a). Keep the last *k* build images per app (configurable/pruned); rollback = start
  (or reuse) the prior image's container and switch the Traefik service target — no rebuild. Layer
  weighted routing for canary/preview later.
- **Rationale:** instant, reliable rollback is the incident feature users want; rebuild-on-rollback
  is slow and can fail. Weighted routing reuses ADR-003.
- **Trade-offs:** disk usage for retained images (bounded by pruning).
- **Risks:** image drift/prune of a needed artifact. **Mitigation:** pin retention ≥ history depth
  shown in UI; prune only beyond it.
- **Migration:** record `ImageTag`/digest per deployment (already partly present); add prune policy.

## ADR-007 — Health probes: startup / liveness / readiness; readiness gates cutover · **Accepted**
- **Options:** (a) single HTTP check (current); (b) three distinct probes with readiness gating the
  route switch.
- **Decision:** (b). Start the new container, run the **startup** probe (grace for slow boot), then
  **readiness** before switching the route; keep **liveness** for ongoing restarts. Old container
  serves until readiness passes.
- **Rationale:** zero-downtime cutover; ASP.NET/JVM apps boot slowly and were at risk under the
  single-check model.
- **Trade-offs:** more config per app (sensible defaults; advanced-only in UI).
- **Risks:** misconfigured probes. **Mitigation:** defaults + validation + docs; probe results in
  deploy logs.
- **Migration:** additive app fields (probe paths/timeouts) with defaults derived from the existing
  health path.

## ADR-008 — Concurrency & idempotency · **Accepted** (fixes H3)
- **Decision:** at-most-one active deployment per app via a DB lease; webhook deliveries carry a
  delivery id and are de-duplicated; remote host ports come from a **tracked allocation table**, not
  a slug hash.
- **Rationale:** prevents racing deploys, duplicate webhook deploys, and port collisions across
  nodes.
- **Trade-offs:** slight added bookkeeping. **Risks:** lease deadlock. **Mitigation:** lease TTL +
  reconciler release. **Migration:** new `HostPortAllocation` table; migrate existing remote apps by
  recording their current port.

## ADR-009 — Secrets & master key: fail closed · **Accepted** (fixes security default)
- **Context:** `AesGcmSecretProtector` falls back to `dev-insecure-master-key-change-me` if unset.
- **Decision:** in Production, **refuse to start** without an explicit `HARBORA_MASTER_KEY`; keep a
  loud-warning dev default only in Development. Support key rotation with versioned key ids.
- **Rationale:** a silent insecure default is a critical risk for a self-hosted security product.
- **Trade-offs:** one more required env (the installer already generates it).
- **Risks:** existing installs without the env. **Mitigation:** installer already writes it; startup
  check gives a precise fix message; rotation path documented.
- **Migration:** none for installer users; document manual set for others.

## ADR-010 — Frontend stays Vue islands in ASP.NET MVC (no SPA) · **Keep/Accepted**
- **Context:** user constraint: do **not** build a separate SPA; keep build/deploy simple.
- **Options:** (a) Razor + Vue islands via Vite (current); (b) full Vue/Nuxt SPA + API; (c)
  HTMX/Alpine.
- **Decision:** (a). Razor renders pages; Vue mounts only interactive islands (logs, routing,
  charts, command palette); Vite builds into `wwwroot/build` via manifest.
- **Rationale:** meets the constraint; one deployable; SEO/first-paint simple; islands cover the
  genuinely interactive bits.
- **Trade-offs:** not a rich client-side router (not needed).
- **Risks:** island bundle missing → dead interactivity. **Mitigation:** robust manifest loading
  (present) + polling fallbacks (present) + a build check in CI (doc 13).
- **Migration:** none; add the command-palette island; ensure CI builds the bundle.

## ADR-011 — Persistence: PostgreSQL + EF Core; migrations forward-only · **Keep/Accepted**
- **Decision:** keep Npgsql + EF Core; migrations are additive/forward-only with data-preserving
  backfills; destructive changes require an explicit migration + backup (doc 11/14).
- **Rationale:** already in place, robust, good tooling. **Risks:** long migrations lock tables.
  **Mitigation:** batch backfills; test on a copy; pre-migration backup.

## ADR-012 — Server↔agent trust: bearer token + optional mTLS · **Keep/Accepted**
- **Decision:** keep bearer-token auth for the agent with optional mTLS (custom CA) for the
  panel↔agent link; treat the agent as a privileged component (it holds the Docker socket).
- **Rationale:** simple default, hardenable for production. **Risks:** token leakage. **Mitigation:**
  rotate tokens; prefer mTLS in multi-server; never log tokens (redactor covers this).

## ADR-013 — Observability: structured logs + Prometheus-friendly metrics · **Proposed**
- **Decision:** emit structured logs with correlation ids per operation; expose metrics in a
  Prometheus-compatible form and read Traefik metrics for per-route data; do **not** build an
  in-house APM.
- **Rationale:** integrate with the ecosystem (Grafana) instead of reinventing; keeps the monolith
  lean. **Checkpoint:** confirm scope in Phase 12 (monitoring).

## ADR-014 — Compose & Template deploys as first-class build inputs · **Accepted** (fixes C3)
- **Context:** Compose/Static/Template are advertised but throw.
- **Options:** (a) parse compose → orchestrate multiple containers as one app; use template
  manifests to synthesize a deploy spec; (b) shell out to `docker compose`.
- **Decision:** (a) behind the existing `IDeploymentEngine` seam — a Compose app becomes a
  multi-service unit on the tenant network; a Template resolves its manifest into the appropriate
  source (image/git/service graph) and reuses the same pipeline. Static site uses the existing
  Nginx buildpack path made reachable.
- **Rationale:** honesty (ship what's claimed), reuse the pipeline/state machine, avoid brittle CLI
  shelling.
- **Trade-offs:** compose feature-coverage is a subset initially (documented). **Risks:** compose
  edge cases. **Mitigation:** define a supported subset + validation + tests; clear "unsupported
  directive" errors.
- **Migration:** additive; gate UI options on implemented sources.
