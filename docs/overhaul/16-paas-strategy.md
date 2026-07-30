# 16 — From deploy engine to managed PaaS: audit, gaps, model and plan

Phase 0. No code changed to produce this document; it records what is actually true today, what the
product needs, and the smallest safe path between them.

---

## 1. Baseline — what is proven to work

Recorded so that nothing below is allowed to break it. Each line was verified on the live server
(`panel.91.99.205.231.nip.io`), not inferred from code.

| Capability | Evidence |
|---|---|
| Push-to-deploy from a developer machine | A real ASP.NET Core project (`SubscriptionLink`, 49 files) packed by `harbora deploy`, built on the server, served behind HTTPS with `server: Kestrel` |
| Git deploy + buildpack detection | Verified on a real repository |
| Prebuilt image deploy | Verified |
| Compose stack (multi-service) | Verified, including service-name DNS between containers |
| Zero-downtime cutover | New container starts, health gate passes, traffic switches, old container retires — asserted by tests over the real pipeline |
| Artifact rollback | Re-releases a prior image; refuses up front if retention pruned it |
| Managed databases | PostgreSQL provisioned, credentials revealed, attached to an app, env injected, DNS resolved, **authenticated from the app's own container** |
| Backup → restore | Round trip verified, extract-then-swap restore, encrypted archives, checksums |
| Backup delivery | Telegram/email copy of each backup, with size refusal and honest failure reporting |
| Multi-server | A real agent node joined, two apps deployed to it with tracked host ports, both served through the proxy |
| Domain readiness | DNS + live TLS handshake per domain, real verdicts |
| CLI | login (password or token), multi-account, deploy, self-update, version notice |
| Automatic pre-upgrade restore point | Fires on every migrating upgrade, verified twice in production |

**Rule for every phase below: this table is a regression suite, not history.**

---

## 2. Audit — the actual state

197 source files, 50 test files, 575 tests, 11 migrations, 0 warnings.

### 2.1 Two premises in the brief are out of date

**"Harbora has no ready-made databases."** It does. `ManagedServiceType` covers PostgreSQL, MySQL,
MariaDB, Redis and MongoDB; `ServiceCatalog` describes each as data (image, port, data path, env,
connection string, attach variables); provisioning, credential reveal, attach-to-app and internal DNS
were verified end to end on the server. Phase 3 is therefore **upgrade and harden**, not build. What is
genuinely missing is listed in §3.

**"Plans and quota need to be made ready."** `Plan`, `InstanceSize`, `UsageRecord`, `QuotaService`,
`NodeCapacityService`, `SchedulerService` and a metering background service already exist and gate app
creation. What is missing is the *shape* of the limits (§3) and the admin surface to change them.

### 2.2 The real structural gap

The model is flat:

```
Workspace ──┬── App ──┬── Deployment ── DeploymentLog
            │         ├── EnvironmentVariable
            │         ├── Volume
            │         └── DomainName ── Route ── Certificate
            ├── ManagedService        (databases, siblings of App, not children)
            ├── BackupDestination / Backup / BackupSchedule / BackupDelivery
            ├── Alert / MonitoringMetric / AuditLog
            └── Server ── HostPortAllocation
```

`App` is doing four jobs at once: a deployable unit, a project, an environment, and a routing target.
There is no `Project`, no `Environment`, and no shared notion of "a thing that can be deployed" that a
worker or a cron job could also be. Networking is per-workspace (`harbora-ws-{slug}`), not per
project/environment, so isolation is coarser than the brief requires.

That single gap is the reason the product cannot express: staging vs production, a worker next to an
API, a cron job, a release task, or an architecture view.

---

## 3. Gap analysis

Verified against the codebase. "Have" means proven, not merely coded.

| Area | Have | Missing | Priority |
|---|---|---|---|
| Deploy engine | Git, upload, image, compose, cutover, rollback | Release task, build cache, preview envs | keep |
| Project/Environment | — | The whole concept | **P1** |
| Service types | Web only (implicitly) | Private service, worker, cron, release task, static as a type | **P1** |
| Databases | 5 engines, attach, internal DNS | Version pinning UI, credential rotation, storage usage, delete protection, per-engine backup/restore, connected-services view | **P2** |
| Private networking | Per-workspace docker network, container DNS | Per project+environment isolation, stable internal hostname, connection test, explicit public/private toggle | **P2** |
| Templates | `AppTemplate` (single-container) | Versioned manifest, multi-service, generated secrets, approval workflow, catalog | P3 |
| Backups | Engine, encryption, checksum, schedule, retention, S3/local, Telegram/email, restore | SFTP, restore test, verification job, per-engine dump strategy | P3 |
| Deploy UX | Live logs, staged progress, honest failures | Named timeline steps, retry-from-step, correlation id, log download | P2 |
| History/rollback | List, artifact rollback, pre-confirm diff | Config snapshot per deployment, post-rollback health verdict | P2 |
| Logs/metrics | Live logs, host+container metrics, per-app CPU/mem | Search, time range, level filter, instance/deployment filter, download | P3 |
| Domain/SSL | Real readiness verdicts, auto certificates | Guided add-domain flow with DNS record shown before verification | P2 |
| Variables/secrets | Encrypted at rest, redacted in logs, build-time flag | Variable vs secret distinction, env-level vs service-level, bulk editor, .env import, reveal endpoint | P2 |
| Multi-tenancy | Workspace, members, roles, capabilities, global query filters, cross-tenant tests | Platform-level roles (support, billing), resource-based checks on every new surface | P1 (as we add) |
| Plans/quota | Plan, InstanceSize, UsageRecord, quota gate, metering | Build minutes, concurrent builds, backup storage, team seats, domain count | P3 |
| Admin platform | Servers page, capacity, audit log | Workspace management, plan assignment, job retry, drain, feature flags, maintenance mode | P3 |
| Design system | Tailwind, dark only, fa/en | Tokens, light/system theme, skeletons, empty/error states, a11y, real RTL audit | **P1** |
| API/CLI | 7 endpoints, CLI on the same API | Endpoints for the new model, JSON output mode, `link`, `variables`, `domains`, `backups` | P2 |

### 3.1 Competitor reference

**This was written from working knowledge, not from browsing the vendors' documentation this
session.** The patterns are stable and widely documented; the specifics (limits, pricing, exact wording)
are not asserted here and should be verified before any of them appears in marketing copy.

| Pattern | Railway | Render | Heroku | Coolify | CapRover | Harbora today | Proposal |
|---|---|---|---|---|---|---|---|
| Project groups services | ✅ | ✅ | ~ (pipelines) | ✅ | ❌ | ❌ | **adopt** |
| Environments per project | ✅ | ~ | ✅ | ~ | ❌ | ❌ | **adopt** |
| Architecture/canvas view | ✅ | ❌ | ❌ | ~ | ❌ | ❌ | adopt, simplified |
| Managed databases | ✅ | ✅ | ✅ | ✅ | ~ | ✅ | harden |
| Attach db → auto env | ✅ | ✅ | ✅ | ~ | ❌ | ✅ | keep |
| Private networking | ✅ | ✅ | ~ | ~ | ~ | ~ (workspace-wide) | scope to project+env |
| One-click templates | ✅ | ✅ | ~ | ✅ | ✅ | ~ (single image) | versioned manifest |
| Cron jobs | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | **adopt** |
| Release/pre-deploy task | ~ | ✅ | ✅ | ❌ | ❌ | ❌ | **adopt** |
| Rollback to prior release | ✅ | ✅ | ✅ | ~ | ~ | ✅ | keep |
| Usage/quota visible to user | ✅ | ✅ | ✅ | ❌ | ❌ | ~ | surface |
| Self-hosted install | ❌ | ❌ | ❌ | ✅ | ✅ | (not a goal) | — |

**Harbora's differentiation is not features — it is honesty.** Every phase of this project has found
platforms' most common failure: a panel that says something worked when it did not. The health message
that names the container's own crash output, the domain check that performs a real TLS handshake, the
backup channel that admits a 401, the port taken from the image instead of a guess. That is a product
position, and it should be stated on the marketing page and defended in every feature.

---

## 4. Personas

**Sara — independent developer.** One ASP.NET API, one Postgres, one small React front end. Wants a
URL in ten minutes and to never read a Dockerfile. Fails today at: no project grouping, no staging.

**Reza — two-person agency.** Eight client projects, each needing staging and production, separate
databases, and a backup they can prove exists. Fails today at: no environments, no per-project
isolation, backups not visible per client.

**Mina — small product team (4 people).** A web app, a worker, a cron, Redis. Needs roles so a junior
cannot touch production secrets. Fails today at: no worker/cron types, roles exist but no
project-scoped permissions.

**You — platform operator.** Capacity, placement, failed jobs, plan assignment, incidents. Fails today
at: no workspace/plan admin surface, no drain, no job retry.

### 4.1 The journey that must not regress

```
sign up → create project → add service from Git → detected stack shown, editable
→ add PostgreSQL → attach (variables proposed, editable) → deploy → live URL + certificate
→ backups on a schedule, delivered where they can be seen
```

Everything after "create project" already works in some form. The missing words in that sentence are
*project*, *environment*, and *editable build plan*.

---

## 5. Proposed model, mapped onto what exists

```
Workspace (exists)
└── Project (new)
    └── Environment (new; every project gets "production" on creation)
        ├── Service (App, renamed in concept, not in table)
        │     kind: Web | Private | Worker | Cron | ReleaseTask | Static
        ├── Resource (ManagedService, re-parented)
        │     engine: Postgres | MySQL | MariaDB | Mongo | Redis
        ├── Domain, Volume, Variable, Backup policy (re-parented)
        └── Network (per project+environment)
```

### 5.1 Table-level mapping

| Concept | Table today | Change |
|---|---|---|
| Project | — | new `Projects` |
| Environment | — | new `Environments` |
| Service | `Apps` | **keep the table**; add `EnvironmentId`, `Kind`; keep `WorkspaceId` |
| Resource | `ManagedServices` | add `EnvironmentId`; keep everything else |
| Deployment, logs, domains, volumes, variables | unchanged | unchanged — they hang off `App` |
| Network name | `harbora-ws-{slug}` | `harbora-{project}-{env}`, with the old name kept as an alias during transition |

**`Apps` is not renamed and not rewritten.** Renaming a table that the entire verified deploy engine
writes to would put the baseline at risk for a cosmetic gain. The UI says "Service"; the table stays
`Apps`; a later cleanup can rename once nothing else is moving.

### 5.2 Migration plan (data-safe)

1. **Additive migration**: create `Projects`, `Environments`; add nullable `EnvironmentId` to `Apps`
   and `ManagedServices`; add `Kind` to `Apps` defaulting to `Web`.
2. **Backfill in the same migration**: for each workspace, create one project named after the
   workspace and one environment named `production`; point every existing app and service at it.
   Every existing app keeps working, and appears in the new UI inside a project on first load.
3. **Adapter phase**: new code reads through `EnvironmentId`; old code paths keep working via the
   workspace scope. Both are live at once, on purpose.
4. **Tighten**: once every read path is migrated and verified, make `EnvironmentId` required in a
   second migration.
5. **Networks**: new deployments join the project/environment network *and* the workspace network
   during transition, so a redeploy is never required to keep an app reachable. The workspace network
   is dropped only after every app has been redeployed at least once.

Each step is independently revertible, and the automatic pre-upgrade restore point already covers the
database side.

---

## 6. Information architecture

```
Workspace
  Home · Projects · Templates · Activity · Usage · Team · Docs · Support · Account

Project
  Overview · Architecture · Environments · Services · Databases · Networking ·
  Storage · Backups · Activity · Settings

Service
  Overview · Deployments · Logs · Metrics · Variables · Networking · Domains ·
  Storage · Backups · Settings

Database
  Overview · Connection · Metrics · Backups · Restore · Access · Logs · Settings
```

Progressive disclosure: the default view of every page answers "is it working, and what do I do
next?". Anything requiring infrastructure vocabulary lives under **Advanced**, with one sentence of
explanation and an example.

### 6.1 Wireframes (text)

**Dashboard** — answers seven questions, nothing decorative:

```
┌ Needs your attention ─────────────────────────────┐
│ ✗ api (production) — deploy failed 12m ago  [open]│
│ ! shop.example.com — DNS not pointing here  [fix] │
└───────────────────────────────────────────────────┘
┌ Projects ─────────────────────┐ ┌ Recent activity ┐
│ ● shop      3 services  ok    │ │ 12m deploy #14  │
│ ● internal  1 service   ok    │ │ 1h  backup ok   │
└───────────────────────────────┘ └─────────────────┘
[+ New project] [Deploy from Git] [Add database]
```

Empty workspace shows one thing only: *Create your first project*.

**Project → Architecture**

```
        ┌──────────────┐
 web ──▶│  api (Web)   │──▶ postgres (Resource)  ● healthy
        │  ● healthy   │──▶ redis (Resource)     ● healthy
        └──────┬───────┘
               └────────▶ worker (Worker)        ● healthy
 public: api.example.com          private: api.production.internal
```

On mobile this becomes an indented list, not a pannable canvas. Connections change only through an
explicit action with a confirm — never by dragging.

**Service → Overview**

```
api                                    ● Running   [Deploy] [Logs]
production · Web service · .NET 8 · 1 instance
URL   https://api.example.com          certificate valid 74 days
Internal  api.production.internal:8080
Last deploy #14 · 2m 41s · commit a19f3c "add invoices" · succeeded
[Deployments] [Variables] [Domains] [Backups] [Settings]
```

**Deploy timeline** — real steps, elapsed time, no invented percentages:

```
✓ Queued              0s
✓ Preparing source    4s
✓ Detecting stack     1s     .NET 8 (found SubscriptionLink.csproj)
✓ Building           1m 52s  [logs]
✓ Release task        6s     dotnet ef database update
✓ Health check       11s     http://…:8080/ answered 404 — accepted, no health path set
✓ Switching traffic   2s
✓ Completed                  total 2m 41s
```

---

## 7. Phasing (scope-bounded)

Every phase ends with: build clean, tests green, migration reviewed, authorization and tenant
isolation checked, manual test script in Persian, changed-file report, honest gaps list. Nothing is
pushed without instruction.

| Phase | Scope | Touches the deploy engine? |
|---|---|---|
| **0** (this) | Audit, baseline, model, plan | no |
| **1** | Design tokens, light/dark/system, navigation shell, dashboard, project+environment model **with backfill**, create-project wizard | no — engine untouched |
| **2** | Service kinds (private, worker, cron, release task), architecture view, deploy timeline | additive only |
| **3** | Database hardening: version pinning, rotation, storage usage, delete protection, per-engine backup/restore, connected services | no |
| **4** | Per project+environment networking, internal hostnames, connection test, explicit public/private | careful, staged |
| **5** | Template manifest, validation, catalog, admin approval | no |
| **6** | Backup providers (SFTP), verification job, restore test | extends existing |
| **7** | Logs search/filter/download, metrics depth, deployment config snapshots | no |
| **8** | Team/permissions per project, plan+quota shape, admin platform | no |
| **9** | Preview environments, promotion, AI assistants behind a flag | later |

---

## 8. Risks

| Risk | Mitigation |
|---|---|
| The new model breaks the verified deploy path | `Apps` table untouched; additive migration; baseline table in §1 re-run after every phase |
| Backfill produces wrong ownership | One project+environment per workspace, deterministic; reversible; pre-upgrade restore point automatic |
| Network change makes a live app unreachable | Dual-attach during transition; old network removed only after every app has redeployed |
| Scope creep turns this into a rewrite | Phases are capped above; anything not listed is explicitly out |
| Competitor comparison drifts into marketing claims | §3.1 is marked unverified; no specifics published without checking sources |
| Single-node assumptions leak into the new model | Multi-node is already real and tested; every new placement decision goes through the existing scheduler |

---

## 9. Decisions that need your approval

1. **Data model** — add `Project` + `Environment` with an additive migration and automatic backfill,
   keeping the `Apps` table name. (Alternative: flat model with a project label only.)
2. **API compatibility** — keep `/api/v1/apps` working unchanged and add project/environment
   endpoints alongside it, rather than a breaking `v2`.
3. **Networking** — move new deployments to per-project+environment networks with dual-attach during
   transition. This changes the main network topology, which is on your approval list.

Nothing in phases 1–9 starts until 1–3 are answered.
