# What is left, and what is worth adding — planned 2026-08-27

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development.
> Nothing here is committed to. The owner picks from the table; only picked items get built.

**Purpose:** a decision table, not a roadmap. Everything below is either an open backlog item
verified against today's code, or a product gap found by probing for it. Each entry says what it is,
how big it is, what it depends on, and — where it matters — why it might *not* be worth doing.

---

## How this was assembled

`docs/product-audit/backlog.json` now carries evidenced statuses (`BacklogStatusTests` enforces the
shape): **33 done · 17 partial · 16 open** of 66. Everything marked `partial` was checked against the
code, not taken on trust.

Then the gaps the backlog cannot know about — it predates most of this month's work — were probed
by behaviour. **Most came back already built:** auto-restart on crash, rollback to any past
deployment, choosing a branch or tag, a one-off console into a container, and scheduled jobs for
ordinary apps all exist. That is the twenty-second time in this programme a capability assumed
missing already existed, and it is why this list is shorter than it might have been.

**Three genuinely do not exist**, verified twice each: horizontal autoscaling, per-app rate
limiting, and any warning to a customer that they are approaching a quota. Alert rules today watch
only `CpuPercent`, `MemoryPercent` and restart count (`Enums.cs:298-308`) — not disk, not quota.

---

## Group A — finish what is half-built

*These are `partial` in the backlog. Each one works for the common case and stops short somewhere
specific. The value is that the stopping point is already known and narrow.*

### A1 · Adminer on a remote node — the tunnel half (`HARBORA-0059`)
Grants were fixed on 2026-08-26 and resolve per server. Adminer still cannot open against a database
on another machine — **not** because the node agent forbids it (its container verbs are allowlisted)
but because this panel's Traefik config only addresses containers by name on its own local networks.
Refused by name today rather than left broken. **Effort M.** Needs the tunnel work the backlog
already separates out.

### A2 · Live-host E2E CI lane (`HARBORA-0022`, P0)
Marked `partial` because its acceptance needs a real host and this machine has neither Docker nor
Postgres. **This is the item that would retire the phrase "unproven locally" from every report.**
Everything shipped this month — logical databases, queue-triggered functions, config overrides,
Cloudflare DNS, SMTP — is proven against fakes. **Effort M.** Highest leverage on confidence.

### A3 · Node Agent v1 out of the box (`HARBORA-0011`, P0) — **Effort M.**
### A4 · Uptime and restart-count collection (`HARBORA-0030`) — **Effort M.**
### A5 · Alert incident lifecycle and timeline (`HARBORA-0031`) — **Effort M.**
### A6 · Volume safety: the orphan-on-disk half (`HARBORA-0033`)
The `Protected` flag and the database-side orphan report shipped. What did not: finding volumes that
exist **on disk** with no database row, which needs a per-server "list volumes" call that does not
exist anywhere yet. **Effort M.**

### A7 · `Jobs:MaxConcurrency` on a compose install (`HARBORA-0065`, P1)
The setting exists in code and cannot be reached from `deploy/.env`. **Effort S.** The smallest real
item on this page.

---

## Group B — the large open pieces

### B1 · PR preview environments to GA (`HARBORA-0040`, P1, **open**)
**The only item on this page that is actively costing money.** Preview environments are not
collected on merge — the sole signals are a 7-day idle sweep and a GitHub-shaped `deleted:true`,
so merged previews sit for a week holding containers, volumes and ports. Webhook PR events are not
read at all. Teardown code exists with **zero test coverage**. **Effort L.** Three decisions were
taken on 2026-08-18 and recorded in the progress ledger — keep branch keying, no comment-back in GA,
cancel-then-tear-down when a branch dies mid-deploy.

### B2 · Customer email, the rest of it (`HARBORA-0038`)
BYO SMTP, injection and a Dev Inbox shipped. Not shipped: domain DNS guidance and live checks, a
per-message delivery log, a sandbox/production gate, and rotation without redeploy. **And a
correction that still needs resolving:** Harbora has run its *own* Stalwart mail server since
2026-08-11 (`MailController`/`MailPlatformService`), which the 0038/0039 phase split does not
describe. **Effort L**, and it needs the phase split reconciled against reality first.

### B3 · API v1, OpenAPI and CLI phase 2 (`HARBORA-0042`) — **Effort L.**
API tokens exist; a documented public API does not. Matters for a customer automating against the
platform, not for one using the panel.

### B4 · Backup module parity and GA flip (`HARBORA-0046`) — **Effort L.**
### B5 · Onboarding wizard and protect-it checklist (`HARBORA-0053`) — **Effort M.**
### B6 · What a workspace operator sees in the audit log (`HARBORA-0056`)
Support-session entries ship at `/workspaces/support-access`. Unresolved: `AuditLog` has no
`WorkspaceId`, `AuditController` is unchanged, and the provider-only-vs-scoped decision is still
unrecorded. **Effort M**, and it needs a decision before code.

---

## Group C — genuinely new, and verified absent

### C1 · Warn a customer before they hit a quota — **Effort S–M**
Today a customer discovers a limit by being refused. Alerts watch CPU, memory and restarts only;
nothing watches quota or disk. The alerting and notification machinery is entirely built — this is a
new metric plus a threshold, not a new subsystem. **Cheapest item here with a direct effect on how
the platform feels.**

### C2 · Disk as an alertable metric — **Effort S**
Same argument, same machinery. A database filling its volume is the outage nobody sees coming, and
`AlertMetric` has no disk member.

### C3 · Per-app rate limiting — **Effort M**
The panel rate-limits its own routes; a customer's app gets nothing. Traefik middleware can do this
and the config writer already exists. Worth it if customers host public APIs; not otherwise.

### C4 · Horizontal autoscaling — **Effort L, and probably not yet**
Replicas became real on 2026-08-18, so the mechanism exists to scale. But autoscaling needs a metric
loop, a cooldown, and a cost story — and **no competitor in the researched set offers it**. Listed
for completeness; recommended against for now.

---

## Group D — the three things only a real server can prove

Not features. Each is an hour of verification that would convert a stated assumption into a fact.

| | What it would prove | Blocked by |
|---|---|---|
| **D1** | The first real backup and restore of one logical database | Nothing — can run today |
| **D2** | A real AMQP round-trip for a queue-triggered function | A RabbitMQ instance on the server |
| **D3** | A real Google/GitHub sign-in | Owner supplying provider credentials |

**D1 can be done immediately** and closes the largest untested surface shipped this week.

---

## Decision table

| # | Item | Effort | Depends on | Worth it because |
|---|---|---|---|---|
| **C2** | Disk as an alertable metric | **S** | — | The outage nobody sees coming; machinery exists |
| **A7** | `Jobs:MaxConcurrency` from `.env` | **S** | — | Smallest real item; a setting that cannot be set |
| **C1** | Quota warnings before refusal | **S–M** | — | Changes how the platform feels, for very little code |
| **D1** | Prove logical-database backup on the server | **S** | — | Converts this week's biggest assumption into a fact |
| **A2** | Live-host E2E CI lane | **M** | A host | Retires "unproven locally" from every future report |
| **A1** | Adminer tunnel to remote nodes | **M** | — | Finishes 0059; needed the moment there are two servers |
| **B1** | PR previews to GA | **L** | — | **The only item leaking real resources** |
| **C3** | Per-app rate limiting | **M** | — | Only if customers host public APIs |
| **A6** | Volume orphans on disk | **M** | Per-server volume listing | Data safety's remaining half |
| **B6** | Workspace audit log | **M** | A decision first | Tenancy completeness |
| **B5** | Onboarding wizard | **M** | — | First-run experience |
| **A3·A4·A5** | Node agent · uptime · incidents | **M** each | — | Monitoring completeness |
| **B2** | Email, the rest | **L** | Phase split reconciled | Structural market advantage |
| **B3** | API v1 + OpenAPI | **L** | — | Automation customers |
| **B4** | Backup module GA | **L** | — | Parity work |
| **C4** | Autoscaling | **L** | — | **Recommended against for now** |

**If you want a recommendation:** take **C2, A7, C1 and D1** together — four small items, one
afternoon, and every one of them either closes a silent failure or converts an assumption into a
fact. Then **B1**, because it is the only thing on this page actively costing money.
