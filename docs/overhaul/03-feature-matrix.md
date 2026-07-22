# 03 — Feature Matrix & Decisions

Two parts: **(A)** how Harbora compares to the market on the capabilities that matter, and **(B)**
the decision for every Harbora feature — Keep / Improve / Redesign / Replace / Remove / Add Now /
Add Later / Reject — with a one-line rationale. Decisions are grounded in doc 01 (what exists) and
doc 02 (what the market does).

Guiding rule (from the brief): every feature must earn its place on **real user need × clear value
× acceptable maintenance cost**. Complexity for its own sake is rejected.

---

## Part A — Market comparison (capability bar)

Scale: ● strong · ◐ partial · ○ absent. "Harbora now" = as-built (doc 01). "Harbora target" = end
of Version 1 (doc 12).

| Capability | Coolify | Dokploy | CapRover | Railway/Render (cloud bar) | Cloud Run/ACA | Harbora **now** | Harbora **target** |
|---|:--:|:--:|:--:|:--:|:--:|:--:|:--:|
| One-command install + guided server setup | ◐ | ◐ | ● | n/a | n/a | ● | ● (best-in-class) |
| Deploy: Git + Dockerfile + image | ● | ● | ● | ● | ● | ◐ (works, code-only verified) | ● |
| Deploy: Compose (first-class) | ◐ | ● | ◐ | ○ | ○ | ○ (throws) | ● |
| Deploy: buildpacks/Nixpacks | ● | ● | ◐ | ● | ● | ◐ (basic, .NET pin stale) | ● |
| One-click templates / marketplace | ● | ◐ | ● | ● | ○ | ○ (stored, not deployed) | ● |
| Preview / PR environments | ● | ● | ○ | ● | ◐ | ○ | ◐→● |
| Immutable revisions + instant rollback | ◐ | ◐ | ◐ | ● | ● | ◐ (re-deploy old ref) | ● |
| Zero-downtime deploy strategy | ◐ | ◐ | ◐ | ● | ● | ◐ (no verified cutover) | ● |
| Build + runtime logs (live) | ● | ● | ● | ● | ● | ● | ● |
| Env vars/secrets + cross-service refs | ◐ | ◐ | ◐ | ● | ● | ◐ (no refs) | ● |
| Domains + auto SSL | ● | ● | ● | ● | ● | ● (by construction) | ● |
| DNS-provider automation | ○ | ○ | ○ | n/a | n/a | ○ | ◐ |
| Health probes (startup/live/ready) | ◐ | ◐ | ◐ | ● | ● | ◐ (single) | ● |
| Scaling (manual/auto) | ◐ | ◐ | ◐ | ● | ● | ○ (sizes only) | ◐ |
| Managed databases | ● | ● | ◐ | ● | ● | ◐ (real, unverified) | ● |
| Backups + restore (scheduled, S3) | ◐ (often paywalled) | ◐ | ○ | ● | ● | ● (real, unverified) | ● |
| In-browser DB client | ◐ (Easypanel) | ○ | ○ | ◐ | ○ | ○ | ● (differentiator) |
| Monitoring + alerting (built-in) | ◐ | ○ | ◐ | ● | ● | ◐ | ● |
| Multi-server | ● | ● | ● (Swarm) | n/a | n/a | ● (host-port routing) | ● |
| CLI + API + webhooks | ● | ◐ | ◐ | ● | ● | ◐ | ● |
| GitOps config file (`harbora.yaml`) | ○ | ○ | ◐ (captain-definition) | ● | ● | ◐ (`harbora.yml` scaffold) | ● |
| Teams / RBAC | ◐ | ◐ | ○ | ● | ● | ◐ (coarse) | ● |
| Audit log + export | ◐ | ○ | ○ | ● | ● | ◐ (entity only) | ● |
| Multi-tenancy / resale | ○ | ○ | ○ | n/a | n/a | ● | ● (unique in segment) |
| AI Gateway (providers/models/usage) | ○ | ○ | ○ | ◐ (Railway/Zeabur adjacent) | ○ | ○ | ◐ (Add Later, if validated) |

**Reading:** Harbora is already at or above the self-hosted segment on install, multi-tenancy,
backups, and multi-server. It is behind on revisions/rollback correctness, previews, buildpacks
freshness, cross-service wiring, RBAC depth, and monitoring depth. Those are the target-Version-1
priorities. The in-browser DB client and multi-tenant resale are the clearest defensible edges.

---

## Part B — Per-feature decisions

### KEEP (works and fits; minimal change)
| Feature | Why |
|---|---|
| One-command installer + interactive setup | Best-in-class already; core differentiator. |
| Traefik dynamic-config engine (atomic apply + rollback) | Correct design; substrate for domains/previews/traffic. |
| Port/adapter seams (`IDockerEngine`, `IProxyEngine`, …) | Enable safe evolution; textbook Clean Architecture. |
| Security primitives (AES-GCM, PBKDF2, redactor, HMAC, CSRF) | Solid; harden not replace. |
| Bilingual fa/en + RTL/LTR + no-flash theme | Expensive to retrofit; done right. |
| Domain model core (24 entities) | Rich and coherent; extend, don't rewrite. |
| Managed-service & backup engine **mechanisms** | Real logic; keep, verify, correct. |
| PWA shell, SignalR live logs, background job worker pattern | Working; keep. |

### IMPROVE (right idea, needs correctness/depth)
| Feature | Change |
|---|---|
| Deployment pipeline | Introduce an explicit, persisted **state machine** + crash reconciler (fixes C2). |
| Rollback | Move to **immutable image artifact promotion** (instant), not re-deploy of old ref. |
| Health checks | Add **startup / liveness / readiness** probes; verified **zero-downtime cutover** (fixes C4). |
| Buildpacks | Unpin/refresh (.NET 10), pin base images by digest, add caching; adopt a clearer detection contract. |
| Managed databases | Live-verify; add connection-string injection into attached apps automatically. |
| Backups/restore | Live-verify; add per-app scheduled S3 backups at every tier; dry-run restore. |
| Monitoring | Add per-app CPU/mem + request/latency (via Traefik metrics); keep retention prune. |
| RBAC | Enforce **per-action** via authorization policies; add Operator role; test coverage. |
| Audit log | Cover all privileged actions; add UI + CSV/webhook export. |
| Public API | Document, add OpenAPI, define versioning/deprecation policy. |
| Git providers | Finish Bitbucket or remove; make GitHub App onboarding first-class. |

### REDESIGN (rework UX/structure)
| Feature | Change |
|---|---|
| App **detail** page | Tabbed IA: Overview · Deployments · Logs · Env · Domains · Metrics · Settings. |
| Deploy progress | Staged progress visualization (Queued→Build→Deploy→Health→Live) above the log stream. |
| Create-app source picker | Card grid exposing **all** real sources (Git, Dockerfile, Compose, Image, Static, Template). |
| Information architecture | Add command palette (Cmd/Ctrl-K) + working global search; group nav by Project. |
| Rollback UX | Prominent single action with a **pre-confirm diff** (image + env changes). |
| Routing | Keep the visual designer; integrate preview-env + traffic-weight editing. |

### REPLACE (swap implementation, keep capability)
| Feature | Change |
|---|---|
| In-memory-only job handling | Durable, restart-safe queue + reconciler (Postgres-backed or Redis stream). |
| Hash-based remote host-port allocation | Tracked allocation table with conflict avoidance. |

### REMOVE / GATE (stop advertising what isn't real)
| Feature | Change |
|---|---|
| "Deploy from Compose/Static/Template" claims | Gate behind real implementation; remove from README/UI until shipped. |
| Decorative header search | Hide until the command palette/search backend exists. |
| Bitbucket provider enum | Remove until a client exists (or implement). |

### ADD NOW (MVP-critical, closes trust/parity gaps)
| Feature | Why |
|---|---|
| Test suite + CI build/test gate | Non-negotiable protective net (fixes C1). |
| Real Compose deploy (first-class) | Advertised; high demand; Dokploy's strongest draw. |
| Working one-click template deploy | Advertised; top-of-funnel adoption; already modeled as data. |
| Crash-safe deploy state machine + reconciler | Required by brief (resume unfinished ops after restart). |
| Zero-downtime cutover + artifact rollback | Production safety; the feature users reach for in incidents. |
| Deploy webhook per app + `harbora.yaml` as source of truth | CI/CD + GitOps table stakes. |

### ADD LATER (valuable, post-V1)
| Feature | Why later |
|---|---|
| PR/preview environments (Traefik weighted routes) | High value; depends on revisions + routing rework landing first. |
| In-browser DB client | Strong differentiator; build after DB provisioning is verified. |
| DNS-provider automation (Cloudflare/etc.) | Removes the last manual step; additive to existing SSL. |
| Cross-service env references (`${{db.URL}}`) + env groups | Big UX win; needs a service-graph model. |
| Named routing slots (staging/prod subdomains) | Depends on revisions + traffic weights. |
| Autoscaling (RPS/concurrency) & horizontal replicas | Real need only for a subset; measure first. |
| Topology/canvas view (OpenShift/Railway style) | Delightful; only pays off with multi-service graphs. |
| Billing/invoicing on top of existing metering | Provider persona; large scope; validate demand. |

### ADD LATER — conditional
| Feature | Condition |
|---|---|
| **AI Gateway** (manage providers/models/tokens/usage/limits) | Only if the target users show real demand; scope as an isolated module reusing the existing proxy + metering, with hard usage limits. Do **not** let it complicate core deploy UX. Assessed as *interesting, unproven* — gate behind a product-validation checkpoint. |

### REJECT (not worth the cost for this product)
| Feature | Why rejected |
|---|---|
| Kubernetes / Swarm orchestration core | Contradicts the "simple, Docker-based, self-host" thesis; huge maintenance cost; only add an *optional* K8s target if proven demand emerges. |
| General microservices split of the backend | The modular monolith is correct at this scale; splitting adds ops burden with no user value. |
| Full APM/tracing suite in-house | Integrate with Prometheus/Grafana instead of reinventing. |
| Proprietary/closed core | The market punishes it (Easypanel); open-core is the trust advantage. |
| Serverless "scale-to-zero" billing engine | Not meaningful for single-VPS self-host; a pause/unpause concept covers the real need. |
