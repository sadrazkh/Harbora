# Merge Guide — `feature/harbora-backup-sync`

Read this before merging. It records what is finished, what is deliberately not, and what a reviewer
should not have to discover by reading diffs.

- **Source branch:** `feature/harbora-backup-sync`
- **Base:** `df95b0d` (`Mutation-test what publishes ports and what goes into SQL`).
- **Merged into `master` locally. Not pushed** — publishing is the maintainer's call.

### How the merge was done, and what it needed

`master` had moved five commits ahead (the managed-service TLS work), so this is a real merge, not a
fast-forward. `master` was merged **into the feature branch first**, so `master` never held an
untested intermediate state; only once the merged tree built and the whole suite passed was `master`
advanced onto it.

Git auto-merged `HarboraDbContextModelSnapshot.cs` with no conflict — which is exactly the file where
a clean auto-merge can still be wrong, because it is generated and nothing about it fails to compile
when it drifts from the model. It was verified rather than trusted:

```bash
dotnet ef migrations has-pending-model-changes --project src/Harbora.Data --startup-project src/Harbora.Web
# → No changes have been made to the model since the last migration.
```

**Migration order interleaves, and that is fine.** `master`'s `20260804191858_ManagedServiceTls`
falls between this branch's `…183506_BackupModule` and `…201737_BackupIdempotency`. EF applies
migrations in timestamp order regardless of which branch produced them, and none of them touch the
same tables.

> **One incident, recorded rather than tidied away.** Partway through this branch, something else
> operating in this repository committed `df95b0d` onto the feature branch, switched `HEAD` back to
> `master`, and fast-forwarded `master` onto it. Because that happened between commits and the branch
> was not re-checked, the four commits below were initially made on `master`. They were moved to
> `feature/harbora-backup-sync` and `master` was restored to `origin/master`; nothing was pushed and
> no commit was lost. If you use worktrees or another agent against this repository, expect `HEAD` to
> move under a long-running session.

---

## 1. Commits

| Commit | What |
|---|---|
| `9a182d0` | `docs(backup-sync): define architecture and implementation plan` |
| `76feb10` | `feat(backup): add backup domain model and contracts` |
| `0ca68a4` | `feat(backup): integrate kopia backup engine` |
| `493ae40` | `docs(backup-sync): add merge guide and correct the module layout` |
| `1dab1ba` | `docs(backup-sync): correct the branch base and record the HEAD incident` |
| `f14d52b` | `Add modular backup system with feature-flag integration` — the service layer, jobs and scheduler. Committed by the other actor in this repository rather than by the session that wrote it; the content is the intended change, the message is not in this branch's style. |
| `687ef15` | `feat(ui): add backup management experience` |
| `57a78a1` | `docs(backup-sync): update merge guide for the service, job and UI work` |
| `71ad8ff` | `feat(backup): add versioned REST API and Docker volume targets` |
| `780d2f7` | `docs(backup-sync): record the API, volume targets and the second migration` |
| `325faa1` | `feat(backup): support managed database targets` |
| `5ee837b` | `docs(backup-sync): record database targets and what they still cannot do` |
| `2f8796e` | `feat(sync): add sync module with syncthing engine` |
| `2b20c63` | `feat(ui): add sync management experience` |
| `4715ae7` | `docs(backup-sync): record the sync module and its unverified edges` |
| `b6532be` | `feat(sync): add versioned REST API` |
| `f085002` | `docs(backup-sync): document the sync API and the rename migration` |
| `7615881` | `chore(backup-sync): add pinned compose overlay for syncthing and kopia` |
| _(this one)_ | `feat(backup): support application targets` |

---

## 2. State: what actually works

**Finished and tested.**

- The engine port (`IBackupEngine`) and two adapters, resolved per repository.
- `HarboraNativeBackupEngine` — snapshot a directory, list, browse, restore whole or partial,
  delete, health-check. Encrypted at rest with the existing `ArchiveCipher`, stored through the
  existing `IBackupStorage`.
- `KopiaBackupEngine` — repository create/connect, snapshot, list, browse, restore, delete,
  health-check, for **local repositories only**.
- Safe process execution: argument lists, no shell, password via environment, output redacted.
- Domain model, retention calculation, path confinement, snapshot lifecycle, policy validation.
- **Service layer** — repositories (create/health/delete), snapshots (queue/run/browse/delete),
  policies (validate/save/schedule), restores (queue/run), retention.
- **Background jobs** — snapshot, restore, verify, prune, repository health, dispatched through a
  new `IJobHandler` seam so the core dispatcher needs no reference to the module.
- **Policy scheduler** — a hosted service that fires due policies and queues maintenance. Returns
  immediately when the flag is off.
- **UI** — Backup Center and a snapshot browser, built from the existing design system, with the
  sidebar entry contributed only when the feature is on.
- **REST API** — `/api/v1/backup/*`: repositories, targets, policies, snapshots (list, browse,
  queue, delete) and restore jobs. Paged, filterable, sortable, Problem Details, idempotency keys
  backed by a table. Documented in [API.md](API.md).
- **Docker volume targets** — staged into the backup area by a read-only helper mount, held by a
  lease that deletes the staged copy when the snapshot finishes.
- **Database targets** — PostgreSQL, MySQL and MariaDB, dumped through the database's own client
  running in a container built from its own image, and restored the same way. Shell-free commands,
  password via environment, dump deleted after use.
- **Sync module** — its own contracts, domain, Syncthing engine, service, status refresher, UI,
  sidebar entry and REST API (`/api/v1/sync/*`). Shares nothing with Backup by design.
- **Application targets** — volumes plus a definition (image, ports, domains, health check, env var
  NAMES) in one snapshot. Secret VALUES are deliberately excluded.
- 231 new tests. Full suite: **2465 passed, 0 failed.**

**Not built in this branch — and there is no placeholder pretending otherwise.**

| Missing | Consequence |
|---|---|
| **Application restore** | An application snapshot is taken and browsable; putting one back means restoring its volumes and reading `application.json` by hand. The capture side is done; the automated rebuild is not |
| **MongoDB and Redis backup** | Refused with a specific reason rather than half-working — Redis has no logical dump (use the volume target), and `mongodump` cannot take a password that stays out of the process table |
| **Machine-generated OpenAPI** | Deliberately not added: the package pulls `Microsoft.OpenApi` with a **known high-severity advisory** (GHSA-v5pm-xwqc-g5wc), and no available version cleared it. [API.md](API.md) is the reference |
| Agent backup dispatch | The existing node agent is untouched; backups run on the panel's own host |
| Always-on encrypted node, end to end | The mode and its guards exist and are tested; no node has ever been run in it |
| Compose actually run | The files exist and the YAML parses; nothing was started, because there is no Docker here |
| Download of a snapshot artifact | No short-lived link; restore-to-a-new-folder is how a backup is inspected |

**So: after merging and enabling the flags, an operator can create a repository, back up a directory,
a Docker volume, a PostgreSQL/MySQL/MariaDB database or a whole application — on a schedule or on
demand — browse the result, restore it into a confined destination or back into the live database,
see it verified and pruned, drive all of it over a versioned API, and separately keep folders in step
across devices.** Everything beyond that is listed above.

---

## 3. Files changed outside the module

Kept as small as possible. Nothing unrelated was touched and no repository-wide reformatting was run.

| File | Change |
|---|---|
| `Harbora.slnx` | Six project entries (three per module) |
| `src/Harbora.Data/Harbora.Data.csproj` | References to both modules' `Domain` projects |
| `src/Harbora.Data/HarboraDbContext.cs` | 9 `DbSet`s, 9 query filters, two `Configure*Module` methods |
| `src/Harbora.Domain/Jobs/Job.cs` | 5 **appended** `JobKind` members |
| `src/Harbora.Domain/Common/IdempotencyRecord.cs` | Moved up from the backup module so both APIs share it |
| `src/Harbora.Shared/PathGuard.cs` | Moved out of the backup module — a general path-confinement control, not a backup concept |
| `src/Harbora.Infrastructure/Common/IdempotencyStore.cs` | New, platform-level |
| `src/Harbora.Infrastructure/DependencyInjection.cs` | One `IIdempotencyStore` registration |
| `src/Harbora.Web/Harbora.Web.csproj` | References to both modules' Infrastructure |
| `src/Harbora.Web/Program.cs` | `AddBackupModule(...)` + `AddSyncModule(...)` + two `using`s |
| `src/Harbora.Web/appsettings.json` | `Features`, `Backups:Kopia`, `Backups:Module`, `Sync:*` |
| `tests/Harbora.Tests/Harbora.Tests.csproj` | Five project references |
| `deploy/` | New overlay: compose file, Kopia Dockerfile, env example. Existing files untouched |
| `src/Harbora.Application/Abstractions/IJobQueue.cs` | New `IJobHandler` interface (additive) |
| `src/Harbora.Infrastructure/Jobs/JobDispatcher.cs` | Consults registered `IJobHandler`s before the existing switch; the built-in kinds are untouched |
| `src/Harbora.Web/Views/Shared/Design/_Sidebar.cshtml` | One `Augment(...)` call + one label pair |

No existing migration was edited. No existing test was deleted or skipped.

---

## 4. Migration

Four migrations. Three are **purely additive** — no `AlterColumn`, no change to any existing table —
and the fourth is a rename.

| Migration | Adds |
|---|---|
| `20260804183506_BackupModule` | 4 tables, 12 indexes |
| `…_BackupIdempotency` | `BackupIdempotencyRecords` + 2 indexes (one unique on workspace + endpoint + key) |
| `…_SyncModule` | 4 tables, 7 indexes |
| `…_IdempotencyToPlatform` | **Rename** of `BackupIdempotencyRecords` → `IdempotencyRecords` |

The rename is **hand-written**. EF scaffolded a `DROP` + `CREATE`, which loses every stored key —
and in-flight retries would then start their work a second time. `RenameTable` preserves the rows
and reverses cleanly. If you re-scaffold anything in this area, check it did not revert to a drop.

`Down` drops only the new tables.

```bash
dotnet ef database update --project src/Harbora.Data --startup-project src/Harbora.Web
```

Applying it with the feature flag off is safe: the tables stay empty and nothing reads them. Schema is
deliberately not gated behind the flag, so enabling the feature is a config change rather than a
schema change.

---

## 5. New configuration

```json
{
  "Features": {
    "Backup": false, "Sync": false, "EncryptedSyncNode": false, "RemoteBackupAgent": false
  },
  "Backups": {
    "Kopia":  { "BinaryPath": "kopia",
                "ConfigDirectory": "/var/lib/harbora/kopia",
                "CacheDirectory": "/var/lib/harbora/kopia/cache" },
    "Module": { "RestoreRoot": "/var/lib/harbora/restore",
                "StagingDirectory": "/var/lib/harbora/backups",
                "AllowedSourceRoots": [] }
  }
}
```

**`AllowedSourceRoots` is empty by default and that is deliberate.** Until it names directories, no
directory policy can be created and nothing can be backed up. A backup engine pointed at an arbitrary
path is an arbitrary-file read with a download button on the end of it, and the default must not be
"anywhere the panel user can read" — which includes `/etc` and Harbora's own key directory. The UI
says so on the page rather than failing later.

No new package dependencies and no version bumps.

### Deployment (optional overlay)

`deploy/backup-sync.compose.yml` + `deploy/Dockerfile.panel-kopia` + `deploy/backup-sync.env.example`.
Apply only when a flag is being switched on. **Two steps, in order** — the overlay's `build:`
replaces the base one when Compose merges the files, so the base image must exist first:

```bash
docker compose -f docker-compose.yml build panel
docker compose -f docker-compose.yml -f backup-sync.compose.yml up -d --build
```

**Syncthing is a service; Kopia is not.** Syncthing is a daemon Harbora talks to over HTTP. Kopia is
driven as a short-lived local CLI process, so its binary goes *inside* the panel image — hence the
Dockerfile rather than a second service. A `kopia server` container would be a second unlocked
repository holder and a second control surface, which is what the CLI choice avoided.

**Ports.** Syncthing's admin API is published to `127.0.0.1` only: it is a direct path to every file
it holds, authenticated separately from Harbora. The sync protocol port (22000) *is* meant to be
reachable — it is TLS with mutual device-id authentication, and closing it only forces traffic
through a public relay, which is slower and puts a third party on the path.

---

## 6. Before enabling `Features:Backup`

1. **Validate the Kopia CLI flags against your installed binary.** They are written against a pinned
   release and are the one part of this branch that could not be exercised here — no Kopia binary and
   no Docker on the development machine. Run a repository-create and a snapshot-create once and check
   the exit codes. A renamed flag fails with a message nobody expects.
2. Confirm `RestoreRoot` and `StagingDirectory` exist and are writable by the panel user, and that
   they are **not** inside a directory served to the web.
3. Remember the module has no UI yet — enabling the flag on its own changes nothing visible.

---

## 7. Merge risk

**Low.** The module is additive, the core touch points are small, and the feature is off.

Realistic conflicts, all mechanical:

- `HarboraDbContext.cs` — if `master` added `DbSet`s or filters in the same regions.
- `Job.cs` — **if `master` also appended a `JobKind` member, renumber this branch's, not `master`'s.**
  The enum is persisted by value; changing a shipped number reinterprets existing rows.
- `Harbora.slnx`, `Program.cs`, `appsettings.json` — adjacent-line additions.
- `HarboraDbContextModelSnapshot.cs` — regenerate rather than hand-merge if `master` has new
  migrations:

```bash
dotnet ef migrations remove --project src/Harbora.Data --startup-project src/Harbora.Web
```

then re-add after merging.

## 7a. Before you turn a flag on

Merging is the low-risk half. **Enabling is where the untested code starts running**, so do it in
this order:

1. Apply migrations. Safe with the flags off — the new tables stay empty and nothing reads them.
2. Set `Backups:Module:AllowedSourceRoots` (and `Sync:Module:AllowedRoots`). Both fail closed: until
   they name a directory, nothing can be backed up or synced. This is a deliberate speed bump.
3. Bring up the compose overlay and confirm the containers actually start — `cap_drop: ALL` on
   Syncthing is the first thing to relax if the daemon will not.
4. Turn on **`Features:Backup` only**, and take one backup of a small directory. Then restore it into
   a fresh folder and compare the contents yourself. That single round trip exercises the archive,
   the encryption, the storage hand-off and the extraction path at once.
5. Only then try a Docker volume, then a database. Each adds a container-backed step that has never
   run outside a fake engine.
6. `Features:Sync` last, and `Features:EncryptedSyncNode` only after you have tested the arrangement
   yourself — it is experimental and its failure mode is silent.

## 8. Rollback

Nothing to undo at runtime while the flag is off. To remove entirely:

```bash
git revert 0ca68a4 76feb10 9a182d0
```

Then drop the four tables (or apply the migration's `Down`). No existing data is involved: the module
never writes to `Backups`, `BackupDestinations`, `BackupSchedules` or `BackupDeliveries`.

---

## 9. Known gaps and follow-up work

**Security-relevant, listed first.**

1. **Kopia + object storage.** Kopia takes S3 credentials as command-line flags, readable via
   `/proc/<pid>/cmdline`. Until that is resolved — a credentials file with restrictive permissions,
   or the API server — Kopia is restricted to local repositories and S3-family repositories use the
   built-in engine.
2. **SFTP repositories are refused.** The existing destination type requires a pinned host key;
   `BackupRepository` has no column for one. Adding it is a small follow-up. A repository that
   skipped the check would be worse than one not offered.
3. **Restore audit covers request, completion and failure.** What it does not yet cover is a
   per-entry record of what was overwritten — the job row says a restore overwrote live data, not
   which files.

**Functional.**

4. **Machine-generated OpenAPI — blocked, not skipped.** `Microsoft.AspNetCore.OpenApi` 10.0.4 pulls
   in `Microsoft.OpenApi` 2.0.0, which carries a **known HIGH severity advisory**
   (GHSA-v5pm-xwqc-g5wc). Pinning 2.1.0, 2.3.0 and 2.4.0 did not clear it either — verified with
   `dotnet list package --vulnerable`. Adding a known-vulnerable package to a *backup* product for
   documentation convenience is not a trade worth making. The packages were added, tested and
   removed; the csproj is back to where it started. Revisit when a fixed version ships, and gate the
   endpoint to Development when it does.
5. **Application restore.** Capture is finished — volumes plus `application.json`, with secret
   values deliberately excluded. What is missing is the automated rebuild: recreating the app, its
   volumes, domains and ports from that metadata, and prompting for the secrets left out on purpose.
6. **MongoDB** needs a credential file so `mongodump` can authenticate without the password reaching
   the process table. **Redis** would need its RDB/AOF handled as a volume — which already works via
   the Docker volume target, so the refusal points there.
7. **Nothing container-backed has run against a real daemon.** Volume staging, application staging,
   database dumps and database restores are all tested against a fake Docker engine: the images,
   mounts, arguments and environment are pinned, but the first real `pg_dump` is CI's.
8. A database restore currently loads into the database it came from. Restoring into a *different*
   database means creating a service and restoring there; a target-database picker is follow-up.
9. **Syncthing's REST calls have never run against a real daemon.** They are written against the
   `/rest/config` API (v1.23+, where per-folder and per-device endpoints replaced whole-config PUTs).
   Smoke-test create-folder and share-device once before enabling `Features:Sync`.
10. **Compose has never been run.** Three specific unknowns, all stated in the files themselves:
    the image tags (`syncthing/syncthing:1.27.9`, Kopia `0.17.0`) were not verified against a
    registry and should be repinned by digest; `cap_drop: ALL` on Syncthing may be too strict for an
    entrypoint that drops privileges itself, and is the first thing to relax if the container will
    not start; and the Kopia install in `Dockerfile.panel-kopia` was never built.
11. **The panel still runs as root**, because it drives `/var/run/docker.sock`. The Kopia overlay
    deliberately does *not* switch users — that would look like hardening and break Docker access on
    the next deploy. Running the panel unprivileged needs the socket's group mapped onto the runtime
    user (or a socket proxy) and is a change to the base image, not to a backup overlay.
12. `EncryptedSyncNode` is off by default and marked experimental wherever it appears. The guarantee
    comes from Syncthing's untrusted-device support, not from Harbora, and the failure mode — the
    node quietly holding plaintext — is not something Harbora can detect. Do not present it as
    settled to users.
6. Sync and the always-on encrypted node: not started.
7. Container-backed integration tests: not written, and could not have been verified here.
8. Snapshot download: no short-lived one-time link. Restoring into a new folder is the way to
   inspect a backup today.

---

## 10. Verification actually performed

Stated precisely, because "tested" is a word worth being exact about.

| Check | Result |
|---|---|
| `dotnet build Harbora.slnx` | Succeeded, **0 warnings, 0 errors** |
| `dotnet test Harbora.slnx` (branch alone) | **2465 passed, 0 failed**, 17 skipped (pre-existing) |
| `dotnet test Harbora.slnx` (**after merging master**) | **2486 passed, 0 failed** — this branch plus master's TLS work, together |
| EF model snapshot matches the model after the merge | `has-pending-model-changes` reports none |
| Sync API paging, filtering, idempotency, tenancy, no-password-in-response | Asserted at controller level |
| Sync API exposes no restore-shaped route | Asserted by reflecting over the controller's methods |
| Application metadata excludes secret values | Asserted on the written JSON, not just the code |
| Compose YAML parses, with the intended port bindings and caps | Parsed and inspected with a YAML parser |
| **`docker compose up`, health checks, image tags, Kopia install** | **NOT RUN — no Docker and no registry access here** |
| Sync device-id, mode and conflict-name parsing | Asserted directly |
| **Syncthing against a real daemon** | **NOT RUN — none installed on this machine** |
| Migration reviewed for additive-only | Confirmed |
| Backup round trip (snapshot → restore → byte comparison) | Passing, native engine, local repository |
| Encryption at rest of stored artifacts | Asserted in test |
| Path traversal via hostile archive entry | Asserted: refused and reported |
| Restore confirmation over live data | Asserted: refused without the typed folder name |
| Restore destination confinement | Asserted for `..` and absolute paths |
| Source-root confinement | Asserted: a directory outside the allowlist is refused before queueing |
| Tenant isolation, both directions | Asserted: scoped context cannot see another workspace; unscoped sees all |
| API paging, filtering, sorting, idempotency replay | Asserted at controller level |
| No secret in an API response | Asserted by reflecting over the DTO, so a field added later fails |
| Volume staging orchestration (mounts, arguments, cleanup) | Asserted against a fake Docker engine |
| Database dump/restore commands (no shell, password in env, identifiers allowlisted) | Asserted directly |
| Database provider (own image, read-only dump mount, empty-dump refusal, scratch cleanup) | Asserted against a fake Docker engine |
| **Anything container-backed against a real Docker daemon** | **NOT RUN — no Docker on this machine** |
| **Panel rendered in a browser** | **NOT DONE — no run of the app; the views compile, but were not viewed** |
| **Docker Compose health checks** | **NOT RUN — Docker is not installed on this machine** |
| **Kopia CLI against a real binary** | **NOT RUN — no Kopia binary available** |
| **Syncthing** | **NOT RUN — module not built** |

The last three are the honest limits of this branch. Nothing depending on them is reported as working.
