# Merge Guide — `feature/harbora-backup-sync`

Read this before merging. It records what is finished, what is deliberately not, and what a reviewer
should not have to discover by reading diffs.

- **Source branch:** `feature/harbora-backup-sync`
- **Cut from:** `master` at `a55d27f`
- **Not merged, not rebased, not pushed.** No commit was made on `master`.

---

## 1. Commits

| Commit | What |
|---|---|
| `9a182d0` | `docs(backup-sync): define architecture and implementation plan` |
| `76feb10` | `feat(backup): add backup domain model and contracts` |
| `0ca68a4` | `feat(backup): integrate kopia backup engine` |

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

**Not built in this branch — and there is no placeholder pretending otherwise.**

| Missing | Consequence |
|---|---|
| Service layer and API endpoints | Nothing calls the engines yet outside tests |
| Background jobs (snapshot / verify / prune / health) | `JobKind` members exist; no handlers are registered |
| Policy scheduler | Policies can be stored; nothing runs them |
| UI | No pages, no navigation entries, no buttons |
| App/volume targets, database providers | Directory sources only |
| Sync module | Not started — no projects, no entities |
| Agent backup dispatch | Existing node agent untouched |
| Docker compose services for Kopia/Syncthing | Not added |

**So: after merging, the backup module cannot be driven from the panel.** It is a tested engine
layer with schema, wired into DI, behind a flag that is off. That is the honest description.

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

No existing migration was edited. No existing test was deleted or skipped.

---

## 4. Migration

`20260804183506_BackupModule` — **purely additive**: 4 `CreateTable`, 12 `CreateIndex`, no `AlterColumn`
and no change to any existing table. `Down` drops only the four new tables.

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
                "StagingDirectory": "/var/lib/harbora/backups" }
  }
}
```

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
3. **Restore is not yet audited**, because nothing calls it outside tests. The audit entry must land
   with the service layer, not after it.

**Functional.**

4. Service layer, API, jobs, scheduler, UI — the bulk of the original brief.
5. App/volume targets need the existing Docker one-off tar path; database targets need
   `IDatabaseBackupProvider` implementations. The contract is defined; nothing implements it.
6. Sync and the always-on encrypted node: not started.
7. Container-backed integration tests: not written, and could not have been verified here.

---

## 10. Verification actually performed

Stated precisely, because "tested" is a word worth being exact about.

| Check | Result |
|---|---|
| `dotnet build Harbora.slnx` | Succeeded, **0 warnings, 0 errors** |
| `dotnet test Harbora.slnx` | **2335 passed, 0 failed**, 17 skipped (pre-existing, in NodeAgent tests) |
| Migration reviewed for additive-only | Confirmed |
| Backup round trip (snapshot → restore → byte comparison) | Passing, native engine, local repository |
| Encryption at rest of stored artifacts | Asserted in test |
| Path traversal via hostile archive entry | Asserted: refused and reported |
| **Docker Compose health checks** | **NOT RUN — Docker is not installed on this machine** |
| **Kopia CLI against a real binary** | **NOT RUN — no Kopia binary available** |
| **Syncthing** | **NOT RUN — module not built** |

The last three are the honest limits of this branch. Nothing depending on them is reported as working.
