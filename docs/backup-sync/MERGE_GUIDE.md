# Merge Guide — `feature/harbora-backup-sync`

Read this before merging. It records what is finished, what is deliberately not, and what a reviewer
should not have to discover by reading diffs.

- **Source branch:** `feature/harbora-backup-sync`
- **Base:** `df95b0d` (`Mutation-test what publishes ports and what goes into SQL`), which is also
  `origin/master`. The branch is a clean fast-forward from it — merging needs no rebase.
- **Not merged, not rebased, not pushed.**

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
- Schema for four new tables.
- 101 new tests. Full suite: **2335 passed, 0 failed.**

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
- 177 new tests. Full suite: **2411 passed, 0 failed.**

**Not built in this branch — and there is no placeholder pretending otherwise.**

| Missing | Consequence |
|---|---|
| Application targets | Directory, Docker volume and database only; the resolver refuses the rest with a message saying so |
| MongoDB and Redis backup | Refused with a specific reason — Redis has no logical dump (back up its volume), and `mongodump` cannot take a password that stays out of the process table |
| Machine-generated OpenAPI | `docs/backup-sync/API.md` is the reference; no `/openapi` document is served |
| Sync module | Not started — no projects, no entities |
| Agent backup dispatch | Existing node agent untouched |
| Docker compose services for Kopia/Syncthing | Not added |
| Download of a snapshot artifact | No short-lived link; restore-to-a-new-folder is the way to inspect a backup |

**So: after merging and enabling the flag, an operator can create a local or S3 repository, back up
an allowed directory on a schedule or on demand, browse the result, restore part or all of it into a
confined destination, and see it verified and pruned.** Everything beyond that is listed above.

---

## 3. Files changed outside the module

Kept as small as possible. Nothing unrelated was touched and no repository-wide reformatting was run.

| File | Change |
|---|---|
| `Harbora.slnx` | Three project entries |
| `src/Harbora.Data/Harbora.Data.csproj` | Reference to `Harbora.Modules.Backup.Domain` |
| `src/Harbora.Data/HarboraDbContext.cs` | 4 `DbSet`s, 4 query filters, one `ConfigureBackupModule` method |
| `src/Harbora.Domain/Jobs/Job.cs` | 5 **appended** `JobKind` members |
| `src/Harbora.Web/Harbora.Web.csproj` | Reference to the module's Infrastructure |
| `src/Harbora.Web/Program.cs` | One `AddBackupModule(...)` call + one `using` |
| `src/Harbora.Web/appsettings.json` | `Features` section, `Backups:Kopia`, `Backups:Module` |
| `tests/Harbora.Tests/Harbora.Tests.csproj` | Three project references |
| `src/Harbora.Application/Abstractions/IJobQueue.cs` | New `IJobHandler` interface (additive) |
| `src/Harbora.Infrastructure/Jobs/JobDispatcher.cs` | Consults registered `IJobHandler`s before the existing switch; the built-in kinds are untouched |
| `src/Harbora.Web/Views/Shared/Design/_Sidebar.cshtml` | One `Augment(...)` call + one label pair |

No existing migration was edited. No existing test was deleted or skipped.

---

## 4. Migration

Two migrations, both **purely additive** — no `AlterColumn`, no change to any existing table.

| Migration | Adds |
|---|---|
| `20260804183506_BackupModule` | 4 tables, 12 indexes |
| `…_BackupIdempotency` | `BackupIdempotencyRecords` + 2 indexes (one unique on workspace + endpoint + key) |

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

No new environment variables, no new Docker services, no new package dependencies, no version bumps.

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

4. **Machine-generated OpenAPI.** The project has no OpenAPI package; adding one emits a document
   describing every controller in the panel, which is wider than this branch should reach on its
   own. Wire `Microsoft.AspNetCore.OpenApi` and gate it to Development.
5. Application targets need app-metadata capture (definition, image, env names, secret *references*,
   volumes, ports, domains). Not started; the resolver refuses them outright rather than half-working.
6. **MongoDB** needs a credential file so `mongodump` can authenticate without the password reaching
   the process table. **Redis** would need its RDB/AOF handled as a volume — which already works via
   the Docker volume target, so the refusal points there.
7. **Nothing container-backed has run against a real daemon.** Volume staging, database dumps and
   database restores are all tested against a fake Docker engine: the images, mounts, arguments and
   environment are pinned, but the first real `pg_dump` is CI's.
8. A database restore currently loads into the database it came from. Restoring into a *different*
   database means creating a service and restoring there; a target-database picker is follow-up.
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
| `dotnet test Harbora.slnx` | **2411 passed, 0 failed**, 17 skipped (pre-existing, in NodeAgent tests) |
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
