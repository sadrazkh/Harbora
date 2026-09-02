# Market gaps, round two — what is still worth adding, phased for the owner to choose from

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development.
> Nothing here is committed to. The owner picks phases or items; only picked work gets built.
> Each item is written so a fresh model with none of this session's context can execute it.

**Purpose:** the owner asked for a market-and-product review of what *else* would be useful and
would improve the system — beyond the twelve sub-projects of 2026-08-20, the nine of 2026-08-21,
the config-delivery and shared-database work, and the 2026-08-27 decision table. This is that
review: sourced market research crossed against a behaviour-level audit of the code, ranked by
likely value to an Iranian PaaS customer, and phased by dependency.

---

## How this was assembled, and what it corrected

**Two inputs, checked against each other.** A market-research pass (Liara, ArvanCloud, Railway,
Render, Fly.io, Coolify, Dokploy, Vercel; sources cited per claim, `unverified` where a claim could
not be confirmed) and a code audit that probed ~40 capabilities **by behaviour**, not by the name
we would have given them.

**The audit's first sweep returned seven false positives**, every one verified individually before
this plan was written:

| Claimed present | Reality |
|---|---|
| Error tracking | A one-click GlitchTip **template** only — nothing native |
| Search engine | A Meilisearch **template** only — not a managed service |
| Prometheus `/metrics` | The **node agent's own** metrics — nothing for a customer's app |
| Image optimisation · free tier/trial · product tour · invoicing/tax · private-registry pull credentials | **None exist.** The greps matched unrelated words |

**And one thing the research ranked as a gap already ships.** Its item 6, "Jalali calendar across
the dashboard", was assumed missing. `src/Harbora.Web/Infrastructure/Dates.cs` says otherwise:
*"under fa these render in the Jalali calendar, which is the behaviour the panel already had and
must keep."* The default request culture is `fa` (`Program.cs:99`). **Twenty-third instance in
this programme of a capability assumed missing that already existed.** It is dropped from this plan.

Everything below survived both checks.

---

## Verified current state — 2026-09-02, trust these

| Fact | Evidence |
|---|---|
| Static-site deploys exist (Nginx buildpack) but there is **no CDN, no edge cache, no image pipeline** | `Enums.cs:51 StaticSite`, `Buildpacks.cs:140`; zero hits for Cdn/EdgeCache/ImageResize |
| Log search fans out over **fetched container tails only**; nothing is retained past the container | `LogsController` (2026-08-18); zero hits for LogRetention/LogSink |
| The only HTTP probe of a customer app is `HealthDiagnosis` **during a deploy** — no periodic outside-in uptime check exists | `HealthDiagnosis.cs:29-34`; zero hits for UptimeCheck/HttpMonitor |
| Alert rules watch `CpuPercent`, `MemoryPercent`, restart count, `DiskPercent` (2026-08-27) and quota — nothing external | `Enums.cs:298+` |
| Backups are scheduled dumps; **no WAL archiving, no PITR, no read replicas** | zero hits for archive_mode/wal_level/ReadReplica/PITR |
| Roles: 4 system (`Owner/Admin/Member/Viewer`), 4 workspace (`Admin/Member/Viewer/Operator`), 16 capabilities. Per-app/per-service grants are `partial` (HARBORA-0035); **no approval gate on deploy** | `Enums.cs:6-21`, `Capabilities.cs`, `backlog.json` |
| Environment promotion exists (`PromotionPlan`) — the mechanism, not a workflow with approvals | `Deployments/PromotionPlan.cs` |
| Release commands (pre-start) exist; **no build cache between deploys, no monorepo sub-path, no private-registry pull credentials** | `ReleaseCommand` migration 2026-07-31; zero hits for CacheFrom/RootDirectory/RegistryAuth |
| CLI has `init/login/whoami/accounts/status/apps/deploy/logs/cancel/update/doctor`; **no `env pull`, no `run`, no `dev`** | `harbora --help`; zero hits for EnvPull |
| Billing: hourly PAYG, wallet, ledger, vouchers, budget/spend cap, cost forecast. **No invoice document, no tax fields** | `Billing/*`; zero hits for class Invoice/TaxRate/VAT |
| Redis ships with `--appendonly` on; **no configurable eviction policy** | `ServiceCatalog.cs:132` |
| Object storage (MinIO) exists platform-wide and buckets attach to apps — so a WAL/PITR archive **has somewhere to go** | `StorageBucket`, F5 (2026-08-21) |
| Harbora runs its **own Stalwart mail server** since 2026-08-11 (mailboxes on customer domains, hourly billed) — relevant to nothing here, recorded so nobody rediscovers it | `MailController`, `StalwartClient` |

---

## Global constraints — binding for every item

Read the **"Global constraints"** section of
`docs/superpowers/plans/2026-08-21-functions-and-services-plan.md` in full; all of it binds. The
short form: zero new build warnings (exactly 2 pre-existing NU1903 stay) · never assume a baseline,
run `dotnet test Harbora.slnx` first and report both numbers (~6,072 passing / 0 failing at planning
time) · run both `dotnet build` and `npm run build`, entry bundle ~126.50 kB gzip must not grow ·
test-first · the panel renders Persian by default in tests, assert on `data-` attributes, never
sentences · bilingual `isFa`/`T["…"]` · semantic tokens only · technical tokens monospace
`dir="ltr"` · three states per table, never a fabricated value · destructive acts take typed-name
confirmation · migrations build-first-then-scaffold, never `ef migrations remove --force` · one
worktree per item, commit as you go, stage by explicit path, never `git add -A`, never
stash/reset --hard/clean · **no Docker and no live PostgreSQL on the dev machine**, so say plainly
what was and was not proven.

**Commits carry the owner's name alone** — no `Co-Authored-By` trailer, no other co-author line.
Use `git -c user.name="sadra zadeh khameneh" -c user.email="1986.aandrii@gmail.com" commit`.

**The law:** twenty-three times a capability assumed missing already existed — five of them inside
plans that warn against exactly this, and one inside the research this plan is built on. **Search
for what a thing does, not for what you would have called it**, and report what you found.

**Standing owner exclusions:** nothing payment-gateway (no ZarinPal/Zibal, no top-up, nothing that
moves money); cybersecurity/vulnerability review is a separate later process — building features is
in scope, auditing them adversarially is not.

---

## Phase 1 — small, independent, no prerequisites

*Each is an afternoon for one agent. All nine can run in parallel; they touch disjoint files.*

### 1.1 Build cache between deploys — **S**
Every deploy rebuilds every layer. Tag the previous successful image and pass it as `--cache-from`
(or use BuildKit's inline cache) so an unchanged `npm ci`/`dotnet restore` layer is reused. The
build log must say when the cache was used and when it was not — a deploy that is fast for an
unknown reason is not a feature. Research note: not a differentiator at the CI layer, but on a
single self-hosted node it is the difference between a 40-second and a 4-minute deploy.

### 1.2 Monorepo: deploy from a sub-directory — **S**
`harbora.yml` gains a root path; the packer uploads and the build runs from there. `doctor` (shipped
2026-08-24) already inspects the manifest — extend its checks. Liara has no monorepo story either
(research §1), so this is parity-plus.

### 1.3 Private-registry pull credentials — **S**
`PrebuiltImage` deploys can only pull public images. Per-workspace registry credentials, encrypted
via `ISecretProtector`, passed to the engine's pull. A pull that fails on auth must say so by
registry name, never "image not found".

### 1.4 Require SSO for a workspace — **S**
SSO shipped 2026-08-20 with three providers. An admin cannot yet *require* it. One workspace flag;
password sign-in refused by name for members of a workspace that requires SSO; the owner exempt so
nobody can lock everyone out. Dokploy sells exactly this in its enterprise tier (research §3).

### 1.5 Audit log export — **S**
The workspace audit log shipped 2026-08-27 as a page. Add CSV and JSON export of the same rows, same
scoping, same both-directions tenancy tests. Enterprise buyers ask for it before they ask for
anything else on the audit page.

### 1.6 Redis eviction policy — **S**
`ServiceCatalog` starts Redis with `--appendonly`. Expose `maxmemory-policy` (and `maxmemory`) per
instance — `allkeys-lru` for a cache, `noeviction` for a queue. A Redis used as a cache with
`noeviction` fills up and refuses writes, which reads as an outage nobody can explain.

### 1.7 `pgvector` as an option on a PostgreSQL instance — **S**
A toggle that installs the extension and runs `CREATE EXTENSION vector` in a logical database.
Nothing more — no vector-database product. The research found no competitor offering a vector DB;
this is the cheapest honest answer to "can I build an AI feature here".

### 1.8 Sentry-compatible error-tracking path — **S**
Do **not** build APM — every competitor integrates rather than builds (research §2). Ship: a
managed GlitchTip one-click (the template exists — promote it to the managed-service catalogue so it
gets credentials, backups and billing like a database), `SENTRY_DSN` injection into attached apps the
way `S3_*` and `SMTP_*` already inject, and a Learning Centre guide per runtime.

### 1.9 Trial credit on signup — **S**
Billing already has vouchers and a ledger. A configurable signup credit (admin-set amount, default
0 so nothing changes until the owner decides) is one voucher issued automatically. PAYG-with-no-card
is the converting pattern regionally (research §6). **Do not build referral** — no evidence it
converts anywhere.

## Phase 2 — reliability and observability

### 2.1 Outside-in uptime checks with alert routing — **M**
**The highest-value item that is not large.** Today the only HTTP probe is during a deploy. Build a
periodic check (interval per app, from the panel, optionally from each node) that records status,
latency and body-match; feeds the existing alert/incident lifecycle and notification fan-out; and
shows history on the app page and the public status page (which today derives state from
`App.Status`, not from a probe). Liara does not clearly have this; Coolify and Render need bolt-ons
(research §2). **Honesty:** a check that could not run says so — never a green dot for a probe that
never fired.

### 2.2 Log retention and searchable history — **M**
Logs exist only as long as the container does. Add per-app retention (admin-set days, disk-budgeted,
honestly capped) into a store the existing `LogsController` search can read, so "what happened at
03:00 last Tuesday" is answerable. Reuse the search UI; extend its coverage reporting to say how far
back it reached. Depends on nothing; **pairs with 2.1** because an uptime failure without the log
around it is half an answer.

### 2.3 Scheduled database maintenance — **S–M**
`VACUUM`/`ANALYZE`/`REINDEX` on a schedule per PostgreSQL logical database, `OPTIMIZE TABLE` for
MySQL, through the same one-off runner grants and dumps already use. A run that fails must name the
database and the engine's own error. Nobody in the researched set offers this; it is cheap and it is
what an operator otherwise forgets until the disk alert fires.

### 2.4 Cost by project and environment — **M**
The ledger bills per workload; the bill page sums per workspace. Group the same rows by project and
environment (both are already on every `App`/`ManagedService`) so "what does staging cost" has an
answer. Reuse `BurnRate`/`CostForecast` per group — never a second computation. Research §3 found
no competitor with per-project cost centres; enterprise buyers ask.

## Phase 3 — data durability *(the large one)*

### 3.1 Point-in-time recovery for PostgreSQL — **L**
**ArvanCloud proves the regional bar:** 5-minute incremental backups, two object-storage copies,
standby failover (research §4). Harbora's per-database dumps are a step behind. Build WAL archiving
from each PostgreSQL instance into the platform's own object storage (MinIO exists; buckets exist),
a base-backup schedule, and a restore-to-timestamp flow that lands in a **new** logical database by
default (the clone-to-staging shape D2 established) with typed-name confirmation when overwriting.
**Prove restore, not just archive** — the DR drill (2026-08-20) exists precisely because an archive
nobody restored is a hope. MySQL binlog PITR is a follow-on, not part of this item.

### 3.2 Read replicas — **L**, depends on 3.1's streaming
A read-only replica of a PostgreSQL instance on the same or another server, surfaced to attached
apps as a second connection string (`{ALIAS}_REPLICA_URL`) so an app can route reads. Lag must be
measured and shown; a replica whose lag is unknown says unknown.

## Phase 4 — developer experience

### 4.1 Local-dev parity: `harbora run` and `harbora env pull` — **M**
`run` injects an app's *effective* environment (own vars + groups + attached services, exactly what
`ConfigGroupMerge` computes) into a local process; `env pull` writes it to `.env.local` with secrets
marked. Railway's own team is still iterating on the fuller `railway dev`; **no Iranian competitor
has any of it** (research §1). Secrets leave the panel only over the existing authenticated CLI
session, and `doctor` warns if `.env.local` is not gitignored.

### 4.2 Managed Meilisearch — **M**
Promote the template to the managed-service catalogue: credentials, attach-to-app with `MEILI_*`
injection, backups, billing — the same treatment RabbitMQ/NATS got. Liara only offers the
Docker-image workaround (research §4). Search-as-a-feature is a common app need.

### 4.3 GitHub App with check runs — **L**, depends on P8 previews (HARBORA-0040)
Deploy status as a GitHub check, preview URL posted on the PR. Explicitly deferred behind P8, which
is itself still open and is the only item on any list actively leaking resources.

## Phase 5 — team and enterprise

### 5.1 Per-app and per-service grants — **M** *(finishes HARBORA-0035)*
Today capability is workspace-wide. Let a Member be scoped to named apps/services. Dokploy charges
for exactly this (research §3). Reuse `ScopedToProjects` (which already exists for projects) as the
shape; every controller action that checks a capability must also check scope — a sweep, like the
audit-log one, and the same trap: do not default at the sink.

### 5.2 Approval gate on deploy to an environment — **M**, depends on 5.1
An environment flagged as protected requires a second person's approval before a deploy runs. The
job queue already holds queued deployments; add a pending-approval state, an approve/reject action
with audit, and expiry. `PromotionPlan` becomes the path that triggers it. No competitor in the set
ships this as a named feature — a real differentiator for teams past solo developers.

## Phase 6 — Iranian market

### 6.1 Modian (سامانه مودیان) e-invoicing for Harbora's own billing — **M**
Not a feature for hosted apps — **a requirement for Harbora to sell to Iranian businesses.** The
ledger already has every number; what is missing is the invoice document, the tax fields, a
technical-ID registration, and submission through an approved intermediary or the direct API
(research §7 names the ecosystem: Sinapardazesh, Fardad, Asan). **Start with a one-day spike** to
confirm the integration route, because the API terms are not public and the choice of intermediary
is the owner's. Enterprise procurement blocks without this.

### 6.2 Native static/edge cache — **L**
The research is unambiguous: Cloudflare is blocked from operating inside Iran by sanctions *and*
periodically blocked by the Iranian government to push traffic to domestic CDNs (research §5). A
foreign CDN is therefore not an answer; this is a forced-domestic build. Scope for a first version:
a caching layer in front of static deploys and static assets of app deploys (Traefik-fronted cache
with honest cache headers), a `next/image`-style resize endpoint, and purge-on-deploy. **Not** a
multi-region CDN. Depends on nothing; expensive; strategically the most defensible item on this
page.

### 6.3 `.ir` domain registration — **S spike first, feasibility unverified**
nic.ir publishes no public EPP/reseller API; registrar automation likely needs an accredited-reseller
relationship (research §7). A one-day spike answers whether it is possible at all. **Do not plan the
feature until the spike reports.**

---

## Still open from the 2026-08-27 table, unchanged

Listed so the owner has one place to look, not repeated in detail: **B1** PR previews to GA (L, the
only resource leak) · **A2** live-host E2E CI lane (M) · **A1** Adminer tunnel to remote nodes (M) ·
**A7** `Jobs:MaxConcurrency` from `.env` (S) · **B2** email, the rest (L) · **B3** API v1 + OpenAPI
(L) · **B4** backup module GA (L) · **B5** onboarding wizard (M) · **A3–A5** node agent, uptime
collection, incident lifecycle (M each).

---

## Decision table

| # | Item | Effort | Depends on | Value comes from |
|---|---|---|---|---|
| 1.1 | Build cache between deploys | S | — | every deploy, every day |
| 1.2 | Monorepo sub-directory root | S | — | parity-plus vs Liara |
| 1.3 | Private-registry pull credentials | S | — | any customer with a private image |
| 1.4 | Require SSO per workspace | S | SSO (done) | enterprise; Dokploy charges for it |
| 1.5 | Audit log CSV/JSON export | S | audit log (done) | first enterprise ask |
| 1.6 | Redis eviction policy | S | — | prevents an unexplainable outage |
| 1.7 | pgvector toggle | S | logical DBs (done) | cheapest honest AI story |
| 1.8 | Sentry-compatible path (managed GlitchTip + DSN injection) | S | attach-inject (done) | integrate, don't build — industry norm |
| 1.9 | Trial credit on signup | S | vouchers (done) | regional converting pattern |
| **2.1** | **Outside-in uptime checks + alert routing** | **M** | alerts (done) | **highest value that is not large; Liara lacks it** |
| 2.2 | Log retention + history search | M | log search (done) | pairs with 2.1 |
| 2.3 | Scheduled DB maintenance | S–M | logical DBs (done) | nobody offers it; cheap |
| 2.4 | Cost by project/environment | M | forecast (done) | enterprise ask; no competitor |
| **3.1** | **PostgreSQL PITR** | **L** | object storage (done) | **ArvanCloud sets the bar; provable gap** |
| 3.2 | Read replicas | L | 3.1 | scale-out reads |
| 4.1 | `harbora run` / `env pull` | M | config merge (done) | uncontested locally |
| 4.2 | Managed Meilisearch | M | service catalogue | Liara gap |
| 4.3 | GitHub App + checks | L | **P8 (open)** | deferred behind P8 |
| 5.1 | Per-app/service grants | M | — | finishes 0035; Dokploy charges |
| 5.2 | Approval gate on deploy | M | 5.1 | no competitor ships it |
| **6.1** | **Modian e-invoicing** | **M** (spike first) | ledger (done) | **blocks enterprise procurement** |
| 6.2 | Native static/edge cache | L | — | sanctions make foreign CDN non-viable |
| 6.3 | `.ir` registration | spike | — | feasibility unverified |

**If you want a recommendation:** run **all of Phase 1** as one parallel wave — nine small,
disjoint items, one afternoon. Then **2.1 + 2.2 together** (uptime without logs, or logs without
uptime, is half an answer). Then the **6.1 spike**, because it decides whether Harbora can be sold
to a company at all. **3.1** is the largest single leap in durability and should be its own week.
