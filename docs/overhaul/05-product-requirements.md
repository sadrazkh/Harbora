# 05 — Product Requirements (PRD)

Requirements are grouped by capability and tagged with a scope phase: **[F]** Foundation ·
**[MVP]** usable MVP · **[V1]** Version 1 · **[V1.x]** · **[Later]**. Each has acceptance criteria
(AC). "The system" = Harbora. Priorities follow doc 03 and the brief's insistence on a small,
reliable MVP (Deploy, Domain, SSL, Logs, Database, Backup, Monitoring).

Requirement IDs are stable (`R-<area>-<n>`) so the roadmap (doc 12) and tests (doc 13) can
reference them.

---

## A. Platform, install & upgrade

- **R-INST-1 [Keep/F]** One-command install on a fresh Linux VPS that installs all prerequisites,
  configures domains (or `nip.io`), generates secrets, builds, starts, and verifies.
  *AC:* on a clean Ubuntu/Debian/Fedora/Alpine host, a single command yields a reachable HTTPS
  panel; re-running is idempotent and never overwrites secrets.
- **R-INST-2 [F]** The master encryption key **must fail closed**: the app refuses to start in
  Production without an explicit `HARBORA_MASTER_KEY` (no insecure default).
  *AC:* Production boot with no key → clear fatal error; Development retains a dev key with a loud
  warning.
- **R-INST-3 [V1]** Guided **upgrade** flow with an automatic pre-upgrade backup and a documented
  rollback. *AC:* `install.sh update` snapshots data first; a failed upgrade can be rolled back per
  the runbook.
- **R-INST-4 [V1.x]** Optional **unattended** install profile (all inputs via env/flags) for CI.

## B. Deployment (core)

- **R-DEP-1 [Keep/MVP]** Deploy from: Git repo (Dockerfile or buildpack), Dockerfile, prebuilt
  image. *AC:* each source produces a running container reachable via its route.
- **R-DEP-2 [Add Now/MVP]** Deploy from **Docker Compose** as a first-class multi-service app.
  *AC:* a repo/compose file with N services deploys all N, wires them on one network, and routes the
  web service; status reflects the whole stack.
- **R-DEP-3 [Add Now/MVP]** **One-click template** deploy actually provisions the template's
  services + env + volumes. *AC:* choosing "WordPress" provisions WordPress + its DB, wired, with a
  URL — no manual steps.
- **R-DEP-4 [Add Now/V1]** Deploy from **static site** source (auto Nginx). *AC:* a repo with
  `index.html`/build output serves over HTTPS with no Dockerfile.
- **R-DEP-5 [Improve/F]** An explicit, **persisted deployment state machine**:
  `Queued → Building → Pushing → Deploying → HealthChecking → Live` / `Failed` / `Cancelled` /
  `RolledBack`, each transition timestamped and owned. *AC:* status is never mutated outside a
  defined transition; the current state is always derivable from the DB.
- **R-DEP-6 [Add Now/F]** **Crash recovery:** on startup the system reconciles deployments left
  in a non-terminal state (resume if safe, else mark `Failed` with reason). *AC:* killing the
  process mid-deploy and restarting leaves **no** deployment stuck in `Building`.
- **R-DEP-7 [Improve/MVP]** **Zero-downtime cutover:** the previous container keeps serving until
  the new one passes health checks and the route is switched; failure leaves the old version live.
  *AC:* a deploy whose new container fails health never interrupts traffic to the running version.
- **R-DEP-8 [Improve/MVP]** **Immutable revisions + instant rollback:** each successful build is a
  retained image artifact; rollback re-points traffic to a prior artifact without rebuilding.
  *AC:* rollback completes in seconds and does not re-run the build; a pre-confirm diff shows image
  + env changes.
- **R-DEP-9 [F]** **Concurrency + idempotency:** at most one active deploy per app; duplicate
  webhook deliveries are de-duplicated. *AC:* two concurrent deploy triggers result in one active
  deploy + one queued/rejected, deterministically.
- **R-DEP-10 [V1]** **Pre-deploy / release command** (e.g., DB migration) that must succeed before
  traffic cutover. *AC:* a failing release command aborts the deploy and keeps the old version.
- **R-DEP-11 [V1.x]** Named **deploy strategies** (Rolling default; Recreate; later Blue/Green).
- **R-DEP-12 [Later]** **PR/preview environments** created on PR open and torn down on close, each
  on its own route. *AC:* opening a PR yields a unique preview URL; closing it removes the env.

## C. Build system

- **R-BLD-1 [Improve/MVP]** Buildpacks for Node, .NET (current LTS), Go, PHP, Python, static — with
  base images pinned by digest and a build cache. *AC:* a plain repo of each stack builds without a
  Dockerfile; the .NET pack targets the current runtime, not a stale one.
- **R-BLD-2 [V1]** Respect a repo-root **`harbora.yaml`** (port, health path, resource size, env
  refs, domains, pre-deploy cmd) as the source of truth overriding UI defaults. *AC:* a repo with
  `harbora.yaml` deploys reproducibly with no UI input.
- **R-BLD-3 [V1]** Build logs are streamed live and persisted per deployment with download.

## D. Logs & history

- **R-LOG-1 [Keep/MVP]** Live build + runtime logs via streaming, with a polling fallback and
  full-log download. *AC:* logs appear within ~1s of emission; refreshing mid-deploy loses nothing.
- **R-LOG-2 [V1]** Unified per-deployment view combining build + runtime output with basic
  filtering. *AC:* one screen shows both phases; secrets are redacted.
- **R-LOG-3 [Keep/MVP]** Immutable deployment history per app (number, commit, trigger, actor,
  duration, result).

## E. Env vars & secrets

- **R-ENV-1 [Keep/MVP]** Per-app env vars; secret values encrypted at rest (AES-GCM); build-time vs
  runtime distinction; redaction in logs. *AC:* a secret never appears in logs or non-secret reads.
- **R-ENV-2 [Later]** **Cross-service references** (`${{postgres.CONNECTION_URL}}`) resolved at
  deploy time. *AC:* attaching a DB to an app injects its URL with no copy-paste.
- **R-ENV-3 [Later]** **Env groups** shared across apps; edits propagate on next deploy.

## F. Domains, DNS & SSL

- **R-NET-1 [Keep/MVP]** Add/remove domains per app; auto-assign `{slug}.{root}`; enforce
  uniqueness. *AC:* a new app is instantly reachable on a generated subdomain.
- **R-NET-2 [Keep/MVP]** Automatic SSL via Let's Encrypt (HTTP-01) per domain. *AC:* a domain
  pointed at the server gets a valid cert on first HTTPS hit; failures surface the ACME reason.
- **R-NET-3 [Improve/MVP]** In-UI **DNS guidance**: show the exact records to create and a live
  "resolves correctly?" check. *AC:* the domain screen tells the user precisely what to set and
  whether it's done.
- **R-NET-4 [Keep/V1]** Visual routing designer: host/path, redirects, HTTPS-redirect, headers,
  basic-auth, priority — validated and applied atomically with rollback.
- **R-NET-5 [Later]** DNS-provider automation (Cloudflare/DO/etc.) to create records + enable
  wildcard/DNS-01. *AC:* with an API token, records and wildcard certs are provisioned automatically.

## G. Health & scaling

- **R-HLT-1 [Improve/MVP]** Distinct **startup / liveness / readiness** checks; readiness gates
  routing. *AC:* a slow-booting app isn't killed prematurely; traffic only flows when ready.
- **R-SCL-1 [Keep/MVP]** Instance **sizes** map to real CPU/memory limits and are quota-checked.
- **R-SCL-2 [Later]** Manual **horizontal replicas** behind the proxy; then autoscaling on
  RPS/concurrency. *AC:* setting replicas=N runs N healthy instances load-balanced by Traefik.

## H. Databases & services

- **R-DB-1 [Improve/MVP]** Provision managed PostgreSQL/MySQL/MariaDB/Redis/MongoDB with encrypted
  credentials and safe connection info; one-click attach to an app. *AC:* provisioning yields a
  running service and, on attach, the app receives connection env automatically.
- **R-DB-2 [Later]** **In-browser DB client** (run SQL/inspect keys) scoped to the workspace.
  *AC:* an authorized user runs a query and sees results without a local client.

## I. Backups & restore

- **R-BAK-1 [Keep/MVP]** Back up app config, volumes/databases, and full platform to Local + S3;
  scheduled; retention; download; restore with typed confirmation. *AC:* a scheduled backup runs,
  is restorable, and restore returns the target to the backed-up state.
- **R-BAK-2 [V1]** **Encrypted** backups and a **dry-run restore** (validate without applying).
  *AC:* backup artifacts are encrypted at rest; dry-run reports what would change.
- **R-BAK-3 [V1]** Automatic **pre-upgrade** and **pre-restore** backups. *AC:* upgrades/restores
  snapshot first and abort if the snapshot fails.

## J. Monitoring & alerting

- **R-MON-1 [Keep/MVP]** Host + per-container CPU/RAM/disk/network metrics with retention. *AC:*
  the dashboard shows live resource use and degrades honestly when the runtime is unreachable.
- **R-MON-2 [Improve/V1]** Per-app and per-route request/latency/error metrics (from Traefik).
- **R-MON-3 [Keep/MVP]** Alerts (deploy-failed, app-crashed, disk warning, SSL-expiring,
  backup-failed) via Email/Telegram/Discord/Webhook. *AC:* each event, when enabled, delivers to
  the configured channel.
- **R-MON-4 [V1]** Threshold alerts (CPU/mem over limit, disk >80%, N consecutive deploy failures).

## K. Multi-server

- **R-SRV-1 [Keep/V1]** Add remote nodes (URL + token, optional mTLS); capacity-aware scheduler
  places apps; health/status per node. *AC:* adding a node lets new apps schedule onto it; a
  down node is shown as such and not scheduled onto.
- **R-SRV-2 [V1]** Reliable cross-node routing with tracked port allocation (replace hash). *AC:*
  no two remote apps collide on a host port.

## L. CLI, API & webhooks

- **R-API-1 [Improve/V1]** Documented, token-secured **v1 API** with OpenAPI and a versioning/
  deprecation policy, mirroring UI deploy capabilities.
- **R-CLI-1 [Keep/MVP]** `harbora` CLI: login, apps, deploy (ref/tag), logs (follow), status, init.
- **R-HOOK-1 [Add Now/V1]** Per-app **deploy webhook URL** + Git push/tag webhooks (HMAC-verified,
  de-duplicated). *AC:* a POST to the URL triggers a deploy; forged/duplicate deliveries are rejected.

## M. Teams, permissions & audit

- **R-RBAC-1 [Improve/V1]** Roles enforced **per action** via authorization policies:
  Owner, Admin, Operator (ops: restart/logs/console, no create/delete), Developer (own apps),
  Viewer. *AC:* a Viewer/Developer cannot perform privileged actions via direct API/POST.
- **R-RBAC-2 [Keep/V1]** Multi-tenant workspaces with strict isolation (network + data + scheduling).
  *AC:* no cross-tenant data or network reachability.
- **R-AUD-1 [Improve/V1]** Audit every privileged action (who/what/when/where) with a UI and CSV/
  webhook export. *AC:* deploys, config/secret changes, member changes, restores all appear.

## N. Templates & marketplace

- **R-TPL-1 [Add Now/MVP]** Curated built-in templates that deploy real, wired stacks (single- and
  multi-service). *AC:* every listed template deploys to a working app.
- **R-TPL-2 [Later]** Community template catalog (import by definition), no lock-in to catalog-only.

## O. UX foundations (detail in doc 08)

- **R-UX-1 [Redesign/V1]** Tabbed app **detail** page; staged deploy-progress visualization.
- **R-UX-2 [Redesign/V1]** Command palette (Cmd/Ctrl-K) + working global search across apps/
  deployments/domains/services.
- **R-UX-3 [Keep/F]** Dark/Light/System themes; fa/en; RTL/LTR; accessible (keyboard, contrast,
  ARIA); responsive to mobile.
- **R-UX-4 [F]** Every long/destructive operation shows Progress · Success · Failure · Recovery.

## P. AI Gateway (conditional)

- **R-AI-1 [Later, gated]** Optional module to register AI providers/models, issue scoped tokens,
  and meter usage with hard limits, reusing the proxy + metering. *AC (if built):* a request through
  the gateway is authenticated, routed to a provider, metered, and blocked past its limit — with
  **zero** added complexity to the core deploy flow. Ship only after demand validation.

---

## Cross-cutting non-functional requirements

- **NFR-SEC** Secrets encrypted at rest; fail-closed key; least-privilege; all inputs treated as
  untrusted (repo/Dockerfile/compose/URLs). See doc 10.
- **NFR-REL** Idempotent operations; crash recovery; no data loss on restart; atomic proxy/config
  changes with rollback.
- **NFR-OBS** Structured logs, per-operation status, and metrics for every long-running job.
- **NFR-PERF** UI pages respond quickly on a 2 GB VPS; deploy overhead (excluding build) is seconds.
- **NFR-I18N** All user-facing strings localized (fa/en); layout correct in RTL and LTR.
- **NFR-COMPAT** Preserve existing API shapes and data where possible; migrations for any breaking
  change (see doc 11).
- **NFR-MAINT** Stay a modular monolith; no new service unless justified; keep the Vue-in-MVC build
  simple.
