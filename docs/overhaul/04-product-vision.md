# 04 — Product Vision

## 1. One-line vision

**Harbora is the self-hosted PaaS that makes shipping and running apps on your own servers feel as
calm and reliable as a managed cloud — installable in one command, honest about what it's doing,
and safe by default.**

## 2. The core problem

Developers and small teams want the Heroku/Railway experience — push code, get a URL with SSL,
see logs, roll back when it breaks — **without** the per-seat/usage bills, vendor lock-in, or the
fear that a pricing change or shutdown strands them. The self-hosted options that exist force a
trade-off:

- They onboard quickly but then **paywall the safety net** (backups, monitoring, RBAC) — so the
  free tier is a demo, not a foundation (Easypanel).
- They are powerful but **fragile or reactive on security** and rough on day-2 operations (several
  segment tools have shipped shell-injection/IDOR fixes).
- They are open and flexible but **UI-thin**, leaving operators in terminals for routine work
  (Dokku, raw Portainer).
- None of them combine a **truly frictionless install**, a **reliable rollback**, **backups +
  monitoring from day one**, and a **resale/multi-tenant** story in one open product.

Harbora's bet: the winning self-hosted PaaS is not the one with the most features — it's the one
that is **trustworthy on the boring, critical things** (install, deploy, rollback, backup, SSL,
logs, monitoring) and pleasant for non-experts, while staying out of a power user's way.

## 3. Target users

1. **The solo developer / indie hacker.** Has 1 VPS, wants their side-projects online with SSL and
   auto-deploy-on-push, and wants a one-click restore when they break something. Success = first
   app live in under 10 minutes without reading docs.
2. **The small agency / dev shop.** Hosts client apps across a few servers, needs isolation between
   clients, backups they can trust, and a clean dashboard they can occasionally hand to a client.
   Success = onboard a new client app + DB + domain in minutes; restore any client to yesterday.
3. **The small business / internal platform owner.** Runs a handful of internal tools and
   databases, cares about backups, uptime visibility, and not needing a Kubernetes expert. Success
   = it "just runs," alerts before disks fill, and upgrades don't scare them.
4. **The provider / reseller (secondary).** Uses Harbora's multi-tenant layer to sell quota-limited
   workspaces on their own hardware. Success = define plans, invite tenants, meter usage, keep
   tenants isolated. (Harbora already has the strongest story here in its segment.)

Explicitly **not** the target: large enterprises needing full Kubernetes/multi-region/complex
compliance. Harbora may interoperate with those (export, K8s target later) but will not contort its
core for them.

## 4. Value proposition

> "Own your platform. One command to install, one workflow to deploy, one click to roll back —
> with backups, SSL, and monitoring built in, in your language, on your servers."

Concretely, Harbora promises:
- **Install in one command**, guided, with SSL that just works (incl. zero-DNS `nip.io` for
  trying it).
- **Deploy anything Docker can run** — Git (with or without a Dockerfile), Compose, an image, a
  static site, or a one-click template — with **live logs** and a **staged progress** view.
- **A safety net that isn't sold separately:** immutable revisions, instant rollback, scheduled
  encrypted backups + restore, health checks, and monitoring/alerts — at every tier.
- **Clarity for non-experts, power for experts:** progressive disclosure in the UI; a `harbora.yaml`
  and a real CLI/API for GitOps; a visual routing designer that emits the config it validates.
- **Yours, and honest:** open-core, no secret telemetry, secrets encrypted, actions audited, and
  the product never claims a capability it can't actually perform.

## 5. Competitive differentiation — "why Harbora in 2026?"

Against Coolify/Dokploy/CapRover/Railway/Portainer, Harbora's defensible edges:

1. **Onboarding that ends in a *working* HTTPS panel**, not a checklist — the interactive installer
   + zero-DNS fallback + post-install verification is already ahead; we double down.
2. **Rollback and backups you'd actually bet production on** — artifact-based instant rollback with
   a pre-confirm diff, and Cloudron-grade backups included free. This is where the segment is
   weakest and users hurt the most.
3. **Backups + monitoring from day one, un-paywalled** — the anti-Easypanel.
4. **In-browser database client** — the single highest-praised missing feature in the segment;
   query your DB without leaving the panel.
5. **Multi-tenant resale built in** — no direct open competitor offers plans/quotas/isolation/
   metering as a first-class layer.
6. **Bilingual, RTL-first, non-expert-friendly** — an underserved audience (Persian + English,
   proper RTL) with a UI that guides rather than assumes.
7. **AI-workflow-ready, API-first** — a documented API + `harbora.yaml` so AI coding assistants and
   CI can drive it; an optional AI Gateway module *if* demand is proven (not a gimmick bolted on).

## 6. Product principles

1. **Trustworthy over flashy.** Every destructive or long operation shows progress, success,
   failure, and a recovery path. The system reconciles unfinished work after a restart.
2. **Honest software.** No mock buttons, fake metrics, or advertised-but-missing features. If it's
   in the UI, it works.
3. **Safe by default.** Sensible secure defaults (encrypted secrets, fail-closed master key, HTTPS,
   least privilege). The easy path is the safe path.
4. **Progressive disclosure.** Simple by default; advanced settings are one click away, never in
   your face. A beginner and an expert use the same screens comfortably.
5. **Boring reliability first.** Deploy, rollback, backup, SSL, logs, monitoring must be rock-solid
   before anything novel is added.
6. **Own your data and your platform.** Open-core, self-hosted, exportable, no lock-in, no secret
   phone-home.
7. **One product, coherent.** A modular monolith and a single UI — not a constellation of services.
   Keep build/deploy/maintenance simple (the user's explicit constraint).
8. **Bilingual and accessible by construction**, not as an afterthought.

## 7. Non-goals (for the overhaul)

- Not becoming a Kubernetes distribution or requiring K8s. (Optional K8s *target* only if proven.)
- Not a general microservices platform or service mesh.
- Not an enterprise compliance suite (SOC2/HIPAA tooling) in V1.
- Not a billing/payment processor — Harbora meters usage; invoicing is a later, optional layer.
- Not a multi-region global load balancer.
- Not reinventing APM/tracing — integrate with Prometheus/Grafana.
- Not a visual low-code app builder — Harbora deploys and runs apps; it doesn't author them.

## 8. What success looks like (qualitative)

- A first-time user goes from `curl … | bash` to a deployed app with valid SSL in **one sitting**,
  in Persian or English, without opening the docs.
- When a deploy breaks, the user **sees why** (staged progress + logs) and **recovers in one click**
  (rollback) — and the old version never stopped serving.
- An agency trusts Harbora enough to **restore a client from a backup** during an incident.
- A power user drives everything from `harbora.yaml` + CLI in CI and never feels the UI is a
  limitation.
- The product **never surprises** the user with a feature that looks present but isn't.
