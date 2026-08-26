# Shared database instances — many apps, many logical databases, one server

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development.
> Four sub-projects. D1 is the foundation; D2 and D3 depend on it; D4 is independent.

**Goal:** Let a customer run **one** PostgreSQL (or MySQL/MongoDB) instance and give each of their
applications its own logical database inside it — created, connected, backed up and restored from
the panel, without ever opening a terminal. Today every app that wants a database costs a whole
container.

**The owner's words:** *"Databases have to be able to connect to several applications, because in
practice we often want a database per app and there is no need to bring up an instance for each
one. This has to be built in and very clean and user-friendly, and manageable too — for example
backing up or restoring one database inside a PostgreSQL. A user should easily bring up a database,
connect it to their apps, and manage everything from the panel."*

---

## Verified current state — checked 2026-08-25. Read this before assuming anything is missing.

| Fact | Evidence |
|---|---|
| **One database can already attach to several apps.** `AppManagedService`'s unique indexes are `(AppId, ManagedServiceId)` and `(AppId, Alias)` — neither constrains a service to one app. The only refusal is attaching the *same* database to the *same* app twice. | `HarboraDbContext.cs:818-821`, `DatabasesController.cs:821` |
| Attachment already injects a full connection string per engine, plus discrete parts, under a per-attachment alias (`{ALIAS}_DATABASE_URL`, `{ALIAS}_PGHOST`…), with collision-proof aliasing | `AppManagedService.cs:33-37`, `ServiceCatalog.AttachEnv`, `AppManagedServiceAlias.Resolve` (C1, 2026-08-24) |
| **`ManagedService` carries a single `DatabaseName`.** Every app attached to one instance therefore receives the *same* logical database — which is the actual gap behind the owner's request. | `AttachedServiceConnectionResolver.cs:47`, `ManagedServiceAttachEnv.cs:45`, `DatabaseGrantExecutor.cs:29-79` |
| The backup module already has a `Database` target type, and restore already refuses anything that is not a `Database`/`Service` backup | `BackupEnums.cs:58-68`, `BackupEngine.cs:1051` |
| A restore rehearsal into a scratch database already exists and is a named backup check | `BackupEngine.cs:1012` |
| `DatabaseGrantExecutor` already creates users and grants against a named database per engine | `DatabaseGrantExecutor.cs:29-79` |
| Credential rotation exists, and rewrites attached apps' env; the app's staleness shows through `HasUnpublishedChanges` | `CredentialRotationPlan.cs`, C1 |
| Managed engines: PostgreSQL, MySQL, MariaDB, Redis, MongoDB, RabbitMQ, NATS | `Enums.cs:140-157`, `ServiceCatalog.cs` |

**So the honest scope is narrower than it first looks.** Multi-app attachment is done. What is
missing is **many logical databases inside one instance**, and the management surface around them.

---

## Global constraints — binding

Read the **"Global constraints"** section of
`docs/superpowers/plans/2026-08-21-functions-and-services-plan.md` in full; all of it binds. In
summary: zero new build warnings (exactly 2 pre-existing NU1903 stay) · never assume a baseline,
run `dotnet test Harbora.slnx` first and report both numbers · run both `dotnet build` and
`npm run build`, entry bundle ~126.50 kB gzip must not grow · test-first · the panel renders
Persian by default in tests, so assert on `data-` attributes, never sentences · bilingual
`isFa`/`T["…"]` · semantic tokens only · technical tokens monospace `dir="ltr"` · three states per
table, never a fabricated value · destructive acts take typed-name confirmation · migrations
build-first-then-scaffold, never `ef migrations remove --force` · one worktree per sub-project,
commit as you go, stage by explicit path, never `git add -A`, never stash/reset --hard/clean ·
**no Docker and no live PostgreSQL on the dev machine**, so say plainly what was and was not proven.

**Commits carry the owner's name alone.** No `Co-Authored-By` trailer, no other co-author line.
This overrides the default instruction and was violated three times on 2026-08-24 — check your
message before committing.

**The law:** twenty-one times in this programme a capability assumed missing already existed —
five of them inside plans warning against exactly this, and this plan's own scope shrank by half
when checked. **Search for what a thing does, not for what you would have called it**, and report
what you found.

**Standing owner instruction:** cybersecurity and vulnerability review are out of scope. Building
these features is in scope; adversarially auditing them is not.

---

## D1 — Many logical databases inside one instance *(foundation)*

**The gap:** `ManagedService.DatabaseName` is one name. Two apps attached to one PostgreSQL share
one database and one user — so a customer who wants "a database per app on one instance" cannot
have it, and the workaround is a container per app.

**Build:** a logical database as a first-class row beneath a `ManagedService` — its own name, its
own user, its own password — and make **attachment point at a logical database**, not at the
instance. `DatabaseGrantExecutor` already creates users and grants per engine; extend it rather
than writing a second creation path.

**Decisions the implementer must make and defend, not guess:**
- **What happens to existing attachments.** Every current `AppManagedService` points at an instance
  whose single `DatabaseName` is already in use by real apps. A migration must leave them working
  exactly as they are — most likely by materialising that existing database as the instance's first
  logical database and re-pointing attachments at it. **Nothing already running may change
  behaviour**, and a test must prove it.
- **Engine coverage.** PostgreSQL and MySQL/MariaDB have obvious per-database semantics. MongoDB
  differs. Redis has numbered databases, not named ones, and RabbitMQ/NATS have vhosts — which may
  or may not be worth modelling now. **Decide per engine, ship what is genuinely clean, and say
  plainly which engines do not support this rather than faking it.**
- **Naming and collisions.** Two apps asking for `app` on one instance must not collide silently.

**Honesty requirement:** creating a logical database is a real operation against a running engine.
If it fails, the panel says which engine refused and why — never a row that exists in Harbora and
not in PostgreSQL. That divergence is this codebase's defining defect class.

## D2 — Backup and restore one logical database *(depends on D1)*

**The owner named this explicitly.** The backup module already has a `Database` target type and a
restore that refuses non-database backups, and `BackupEngine` already rehearses restores into a
scratch database. What is missing is **granularity**: backing up or restoring *one* logical
database inside an instance without touching its neighbours.

**Requirements:**
- Back up one logical database on demand and on a schedule, reusing the existing backup machinery,
  destinations, retention and delivery. Do not fork a second backup path.
- **Restore into the same database, or into a different one** — including a new one, which is how a
  customer clones production into staging. Restoring over a database that other apps are attached to
  is destructive: typed-name confirmation, and the confirmation must name **which apps** are
  attached, because the person restoring may not know.
- **A safety snapshot before every restore**, the way the database import path already does it, and
  a failed restore must name the snapshot to recover from.
- **Never let a restore silently hit the wrong database.** The engine's own error is what the panel
  shows.

## D3 — The management surface *(depends on D1)*

The owner's phrase was *"manage everything easily from the panel"*. Today a `ManagedService` detail
page describes one database. With D1 it holds several, and the page has to make that legible.

- List the logical databases in an instance: name, size if it is genuinely measurable, which apps
  are attached, last backup.
- Create, rename where the engine allows it, and delete — with a named-list refusal when apps are
  still attached, following `ProjectsController.Delete`'s idiom.
- Attach an app **to a logical database**, from either side, with the alias behaviour C1 established.
- **Show what is measured and what is not.** If per-database size cannot be obtained for an engine,
  say so; never print a zero.
- Follow the panel's redesigned visual language (2026-08-19/20) and the shared `Views/Shared/Design/`
  partials. The size-picker container-query lesson applies: components render inside narrow columns.

## D4 — Adminer and grants stop assuming the local Docker engine *(independent)*

Backlog **HARBORA-0059**, open at P1: database grants and Adminer inject the local Docker engine, so
they break for any database on a remote node. This is adjacent enough that D1–D3 will trip over it —
a logical database on a remote node must be creatable and browsable too.

Resolve engines per server the way the rest of the platform does. **This one can be picked up
independently of D1–D3** and makes them work on more than one machine.

---

## Task table

| # | Task | Effort | Depends on | Why it matters |
|---|---|---|---|---|
| **D1** | Logical databases inside one instance | **L** | — | The actual gap; everything else builds on it |
| **D2** | Per-database backup and restore | **M** | D1 | Named by the owner; machinery mostly exists |
| **D3** | Management surface for many databases | **M** | D1 | "Manage everything from the panel" |
| **D4** | Adminer/grants on remote nodes (HARBORA-0059) | **M** | — | Open P1; D1–D3 need it beyond one machine |

**Suggested order:** D1 → D2 and D3 in parallel → D4 any time (or first, if remote nodes matter now).
