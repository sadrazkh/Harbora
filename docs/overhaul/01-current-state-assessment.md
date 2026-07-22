# 01 — Current State Assessment (Harbora)

> Snapshot date: 2026-07-22 · Baseline commit `84603e0` (branch `master`).
> Method: full read of the repository, a clean `dotnet build`, a live run of the panel against
> PostgreSQL 15, and an authenticated walk of all 16 UI routes. Docker was **not** available in the
> assessment sandbox, so container/deploy runtime paths were verified by **code reading**, not by a
> live deployment. Where that distinction matters it is called out explicitly.

---

## 1. Executive summary

Harbora is **substantially more real than a demo**. It is a clean, modular .NET 10 solution
(~17.7k lines of C# across 8 projects) that builds with **0 errors**, migrates and seeds a real
schema, renders a genuinely designed bilingual (fa/en, RTL/LTR) dashboard, and contains
working implementations of the hard parts: a Traefik dynamic-config engine with atomic apply +
rollback, a deployment pipeline with a health gate, zero-config buildpacks, managed-database
provisioning, a volume backup/restore engine (local + S3), a remote multi-server agent with
optional mTLS, and real security primitives (AES-GCM secrets, PBKDF2, secret redaction, webhook
HMAC, CSRF, token auth).

It is **not** finished, and some of the README's claims outrun the code. The most important
truths for planning the overhaul:

- **There are zero automated tests.** No test project exists in the solution. Every refactor in
  the overhaul is currently unguarded. This is the single highest-priority gap.
- **Three of six advertised deploy sources do not work.** `DockerCompose`, `StaticSite`, and
  `Template` are enum values and README bullet points, but `DeploymentPipeline.AcquireImageAsync`
  throws `NotSupportedException` for anything other than `GitRepository`, `Dockerfile`, and
  `PrebuiltImage`. The one-click **Template** deploy in particular is stored but never executed.
- **The deployment "state machine" is thin.** Status is a single enum field mutated inline; there
  is no queue persistence, no crash recovery for in-flight deploys, no cancellation wired to a
  token, and no immutable-revision model — so rollback is "queue a new deploy of an old ref," not
  an instant artifact promotion.
- **Runtime realism is unverified end-to-end.** The build proves the code compiles and the UI
  renders; a real `docker build → run → health → route → SSL` cycle was not executed here and must
  be validated on a Docker host as step 0 of execution.

Verdict: **keep the foundation, do not rewrite it.** The domain model, the port/adapter seams
(`Harbora.Application.Abstractions`), the Traefik engine, and the installer are assets worth
preserving. The overhaul should stabilize (tests + CI + a real deploy smoke test), then correct
the domain/state-machine core, then close the "claimed but missing" gaps, then layer
differentiators.

---

## 2. Current architecture (as-built)

**Shape:** a modular monolith with Clean-Architecture layering and a satellite agent + CLI.

```
Harbora.Domain          Entities + enums (no dependencies)
Harbora.Application     Ports (interfaces): IDeploymentEngine, IDockerEngine, IProxyEngine,
                        IBackupEngine, IManagedServiceEngine, IGitService, IQuotaService, …
Harbora.Infrastructure  Adapters: DeploymentPipeline, DockerEngine (+RemoteDockerEngine),
                        TraefikProxyEngine, BackupEngine, ManagedServiceEngine, Git*, Tenancy*,
                        Monitoring*, Notifications, Security (AES-GCM/PBKDF2/redactor/tokens), Jobs
Harbora.Data            EF Core DbContext (24 DbSets) + 5 migrations (PostgreSQL/Npgsql)
Harbora.Web             ASP.NET MVC controllers (18), Razor views, Vue islands (Vite), SignalR hub,
                        cookie + bearer-token auth, setup guard, localization (fa/en)
Harbora.Agent           Minimal-API worker on remote nodes; exposes Docker ops over HTTP (+mTLS)
Harbora.Cli             Spectre.Console CLI (login/apps/deploy/logs/status/init) over the v1 API
Harbora.Shared          Cross-cutting helpers
```

**Runtime composition (docker-compose):** Traefik v3.6 (ingress + Let's Encrypt HTTP-01),
PostgreSQL, Redis, and the panel container. The panel talks to the Docker socket in-process via
`Docker.DotNet`; remote nodes run the agent and are reached over HTTP.

**Request/latency model:** HTTP requests return immediately; deploys and backups are handed to an
in-process `ChannelBackgroundJobQueue` drained by a hosted `BackgroundJobWorker`. Live logs are
pushed over SignalR (with a polling fallback baked into the Razor view). Metrics are collected by a
30-second hosted timer.

**Frontend:** Razor renders every page; Vue 3 "islands" (DeploymentLogs, RouteDesigner,
MetricsChart) are mounted only on the nodes that need interactivity, compiled by Vite into
`wwwroot/build` and referenced through a manifest (`ViteManifest.cs`). There is **no** separate SPA
server — this matches the user's constraint and should be preserved.

**Assessment of the architecture:** appropriate and mature for the problem. The port/adapter seams
are real (e.g. `IDockerEngine` has both in-process and remote-agent implementations behind
`IServerEngineFactory`). No Kubernetes, no gratuitous microservices — correct for the target users.
The main structural weaknesses are in *lifecycle/state* management, not in layering (see §5).

---

## 3. Feature inventory — claimed vs. actually working

Legend: ✅ works (verified by code + UI) · 🟡 partial/degraded · ❌ declared but not implemented ·
🔬 needs live-Docker verification.

| Area | State | Evidence / notes |
|---|---|---|
| One-command installer (`install.sh`) | ✅ | Production-grade: OS/arch checks, installs Docker/git/openssl, interactive bilingual domain setup, `nip.io` zero-DNS fallback, DNS pre-checks, random secrets, idempotent, post-install verification (Traefik↔Docker API, panel route, SSL). Genuinely a strength. |
| First-run setup wizard | ✅ | `/setup` creates owner + default workspace, seeds settings, self-locks. Verified live. |
| Auth (cookie UI + bearer API) | ✅ | Cookie auth for UI, `TokenAuthenticationHandler` for API/CLI; CSRF header; setup-guard middleware. |
| Dashboard | ✅ | Health strip (servers/Traefik/SSL/Docker), resource cards, quick actions, onboarding empty state, recent deploys/errors. Degrades cleanly when Docker is down. |
| Deploy from Git repo | 🔬 | Code path complete (checkout via LibGit2 → build → run → health → route). Not executed live here. |
| Deploy from Dockerfile | 🔬 | Same pipeline; uses repo Dockerfile if present. |
| Deploy from prebuilt image | 🔬 | Pull + run path complete. |
| Zero-config buildpacks | 🔬 | `Buildpacks.Detect` generates Dockerfiles for Node/.NET/Go/PHP/Python/static. Note: .NET buildpack pins SDK **9.0** while the platform targets .NET 10 — stale. |
| Deploy from docker-compose | ❌ | Enum + README claim it; pipeline throws `NotSupportedException`. No compose parsing/orchestration anywhere. |
| Deploy static site | ❌ | Enum + README claim it; not handled by the build engine (only reachable indirectly via a Dockerfile). |
| One-click template deploy | ❌ | Templates are seeded (7) and shown, `TemplateId` is stored on the app, but no code translates a template manifest into a deployment. `Template` source type throws. |
| Deploy history | ✅ | Immutable `Deployment` rows with incrementing `Number`, commit metadata, trigger, timestamps. |
| Rollback | 🟡 | Exists as "queue a new deployment pointing at an old ref/deployment"; **not** an instant image-artifact promotion; no traffic pre-check. `RolledBackFrom` recorded. |
| Build + runtime logs | ✅ | Persisted `DeploymentLog` rows + SignalR live stream + polling fallback + download. Runtime logs proxied from the container. |
| Env vars & secrets | ✅ | Per-app vars; `IsSecret` → AES-GCM at rest; `AvailableAtBuild` flag separates build/runtime; redacted in logs. No cross-service references or env groups. |
| Domains | ✅ | Add/remove per app; auto-assigns `{slug}.{root}`; uniqueness enforced. |
| SSL / ACME | 🔬 | Every generated route carries `certResolver: letsencrypt` (HTTP-01). Correct by construction; not issued live here. |
| Reverse proxy / routing | ✅ | `TraefikProxyEngine` renders YAML, validates, atomic-swaps with `.bak` rollback; supports host/path, redirect, HTTPS-redirect, custom headers, basic-auth. Visual RouteDesigner island. |
| Health checks | 🟡 | Single HTTP GET on a health path (or container-liveness fallback) with retry loop. No distinct startup/liveness/readiness probes; no zero-downtime cutover (old container is removed before/around start). |
| Scaling | ❌ | Instance **sizes** (CPU/mem limits) exist; there is no horizontal scaling, autoscaling, or replica concept. |
| Managed databases | 🔬✅ | `ManagedServiceEngine` really pulls the image and runs the container for pg/mysql/mariadb/redis/mongo, stores encrypted creds, exposes connection info + attach-env. Container run unverified live. |
| Backups & restore | 🔬✅ | `BackupEngine` tars a target volume via a throwaway alpine one-off container, supports Local + S3, retention, download, and restore (stop→wipe→untar→restart). Real logic; unverified live. |
| Scheduled backups | ✅ | `BackupSchedule` entity + `BackupScheduler` hosted service. |
| Monitoring | 🟡 | Host + container stats via a 30s collector into `MonitoringMetric`; dashboard + MetricsChart island. Depends on Docker; no per-route/request metrics; retention prune present. |
| Alerting | ✅ | `Alert` opt-ins + `NotificationService` for Email/Telegram/Discord/Webhook; fired on deploy-fail etc. |
| Multi-server | 🔬 | Add remote node by URL+token; `RemoteDockerEngine` proxies ops to the agent; optional mTLS; capacity-aware scheduler places apps. Cross-node routing via published host ports (documented limitation — no shared overlay). |
| Git providers | 🟡 | GitHub/GitLab/Gitea repo import + OAuth + HMAC webhooks (push/tag). Bitbucket is in the enum but has no client. |
| CLI | ✅ | `login/whoami/apps/deploy/logs/status/init`; follows live logs; `harbora.yml` scaffolding. |
| Public API (v1) | 🟡 | `whoami`, list apps, deploy, deployment status, deployment logs. Minimal but real and token-secured. Not versioned beyond `/v1`; no OpenAPI. |
| Multi-tenancy (PaaS) | ✅ | Workspaces, plans, instance sizes, quotas, capacity scheduler, per-tenant Docker network isolation, usage metering (GB-h / vCPU-h). No billing/invoicing engine (documented). |
| Teams / RBAC | 🟡 | `SystemRole` (Owner/Admin/Member/Viewer) + `WorkspaceRole`; enforced coarsely (`[Authorize]`, role checks in nav). Not consistently enforced per-action; no fine-grained project permissions despite the comment promising them. |
| Audit log | 🟡 | `AuditLog` entity exists; writing/coverage is sparse and there is no audit UI/export. |
| Global search / command palette | ❌ | The header search box has no handler — decorative. |
| PWA | ✅ | manifest + service worker + offline shell. |
| Preview / PR environments | ❌ | Not present. |

---

## 4. What actually works well (keep)

1. **The installer.** It is better than most competitors' onboarding and is a differentiator on its
   own. Keep and extend (DNS-provider automation, unattended profile).
2. **Traefik dynamic-config engine.** Atomic write + `.bak` rollback + validation is exactly right.
   Reuse as the routing substrate for domains, preview envs, and traffic splitting.
3. **Clean port/adapter seams.** `IDockerEngine` / `IServerEngineFactory` / `IProxyEngine` /
   `IBackupEngine` make it possible to evolve behavior without touching controllers. Preserve.
4. **Security primitives.** AES-GCM secret protector, PBKDF2 hasher, secret redactor, HMAC webhook
   verification, hashed API tokens, CSRF, per-tenant network isolation. Solid base; harden, don't
   replace.
5. **Bilingual + RTL/LTR + theme-before-paint.** The i18n/RTL groundwork and no-flash theme script
   are done properly and are expensive to retrofit — keep.
6. **Buildpacks concept + managed services + backup/restore engines.** The mechanisms are real;
   they need correctness passes and live verification, not reinvention.

---

## 5. Technical debt & critical issues

**Critical (block or endanger the overhaul):**

- **C1 — No tests, no CI gate.** Zero unit/integration/E2E tests; CI only builds the CLI. Any
  refactor is unguarded. *Fix first.*
- **C2 — Deploy lifecycle is not crash-safe.** In-memory job queue; if the process restarts mid-
  deploy, the `Deployment` row is stranded in `Building`/`Deploying` forever. No reconciler. The
  mission explicitly requires recovery of unfinished operations after restart.
- **C3 — Claimed-but-missing sources.** Compose/StaticSite/Template throw at runtime; this is a
  correctness and trust bug (README says otherwise). Either implement or remove from UI/claims.
- **C4 — Health/cutover is not zero-downtime.** The previous container is replaced without a
  verified new-container-healthy-then-swap sequence at the routing layer; a failed boot can drop
  traffic. Pipeline comments claim safety the code does not fully guarantee.

**High:**

- **H1 — Thin deployment state machine.** Status transitions are ad-hoc field writes; no explicit
  states for Queued→Building→Pushing→Deploying→Healthchecking→Live/Failed with persisted
  ownership, timeouts, and cancellation. Rollback is not artifact-based.
- **H2 — RBAC not enforced per-action.** Roles exist but authorization is coarse; risk of a Viewer/
  Member performing privileged actions via direct POST. Needs a policy layer + tests.
- **H3 — Concurrency/idempotency.** No guard against two concurrent deploys of the same app, or
  duplicate webhook deliveries; host-port allocation for remote apps is a hash (collision-prone).
- **H4 — Audit coverage.** Entity exists but most privileged actions are not audited; no export.

**Medium:**

- **M1 — Buildpack drift:** .NET buildpack pins SDK 9.0; images are unpinned by digest.
- **M2 — API surface is minimal and undocumented** (no OpenAPI/versioning policy).
- **M3 — Decorative UI:** global search does nothing; some pages are single-purpose where a detail/
  tabs layout is expected (apps detail is functional but flat).
- **M4 — Metrics are host/container coarse:** no per-app request/latency metrics; retention only.
- **M5 — Warnings:** 3 compiler warnings (unread `clock` primary-ctor params) — trivial but signal.
- **M6 — Frontend build fragility:** islands silently disappear if the Vite manifest is missing;
  fallback CSS covers the shell but not island functionality.

**Low:** licensing is "TBD"; no `CONTRIBUTING`/issue templates; examples are minimal.

---

## 6. Security posture (summary; full model in doc 10)

Good foundations: secrets encrypted at rest (AES-GCM), passwords PBKDF2, tokens hashed, logs
redacted, webhooks HMAC-verified, per-tenant network isolation, optional agent mTLS, CSRF on. Key
risks to model and harden: the **master key defaults to a hardcoded dev value** if unset
(`dev-insecure-master-key-change-me`) — must fail closed in production; Docker socket access is
effectively root and is the platform's core trust boundary; malicious repo/Dockerfile/compose build
inputs; SSRF via user-supplied git/image/webhook URLs; cross-tenant authorization; agent token
handling; backup/certificate storage encryption. See doc 10.

---

## 7. UX assessment (from the live walk)

Strengths: coherent visual language (not a stock admin template), real empty states and a 3-step
onboarding card, a health strip that communicates system state honestly (including "Docker
unreachable — metrics paused"), progressive disclosure on the create-app form ("Advanced settings
(optional)"), and a clean bilingual/RTL shell.

Gaps: the app **detail** page is flat (no tabbed IA for deploys/logs/env/domains/metrics); deploy
progress is a log pane rather than a staged progress visualization; there is no command palette or
working search; source selection hides 4 of 6 types; no visual multi-service/topology view; error
recovery guidance is limited to logs. These are addressed in docs 06–08.

---

## 8. Keep / Improve / Redesign / Replace / Remove (headline; full matrix in doc 03)

- **Keep:** installer, Traefik engine, port/adapter seams, security primitives, i18n/RTL shell,
  domain model core, CLI shape, backup/managed-service engine mechanisms.
- **Improve:** deployment pipeline (→ explicit state machine + zero-downtime), rollback (→ artifact
  promotion), health checks (→ startup/liveness/readiness), monitoring (→ per-app/request),
  RBAC enforcement, audit coverage, API (→ documented v1 + OpenAPI), buildpacks (unpin/refresh).
- **Redesign:** app **detail** page (tabbed), deploy progress UX, information architecture +
  add a command palette and real search, create-app source picker (card grid, all real sources).
- **Replace:** in-memory-only job handling → durable queue + reconciler; hash host-port allocation
  → tracked allocation table.
- **Remove (or gate honestly):** compose/static/template as "supported" until implemented; the
  decorative search box until wired; the Bitbucket enum value until a client exists.

---

## 9. Baseline facts for execution

- Build: `dotnet build Harbora.slnx -c Release` → **Build succeeded, 0 errors, 3 warnings.**
- Migrations: 5 applied cleanly on PostgreSQL 15; seed produces 7 templates, 5 sizes, 3 plans, 1
  local server.
- Runtime: all 16 authenticated routes return HTTP 200; app tolerates absent Docker.
- Frontend: `npm run build` blocked by the assessment sandbox's registry firewall (not a project
  defect); fallback CSS keeps the shell usable. Must be validated on a normal network.
- Tests: none.
- **Execution step 0 must be:** reproduce the baseline on a Docker-capable host, run one real
  end-to-end deploy (image + git), and record the result in `progress.md` before any refactor.
