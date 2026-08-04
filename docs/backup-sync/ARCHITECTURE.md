# Harbora Backup & Sync — Architecture

Status: **in progress** (branch `feature/harbora-backup-sync`)
Both modules ship behind feature flags that default to **off**.

---

## 1. The finding that shaped this design

The brief for this work was written as though backup were a greenfield feature. It is not.
Harbora already contains a complete, tested backup subsystem:

| Concept in the brief | What already exists |
|---|---|
| `BackupRepository` | `BackupDestination` — Local / S3 / SFTP, secrets encrypted at rest |
| `BackupPolicy` | `BackupSchedule` — interval hours + retention count |
| `BackupSnapshot` | `Backup` — status, checksum, size, verification verdict |
| Snapshot / Restore / Retention / Verify | `BackupEngine` (`RunAsync`, `RestoreAsync`, `EnforceRetentionAsync`, `VerifyAsync`) |
| Encryption | `ArchiveCipher` — AES-GCM, key derived from the master key via HKDF |
| Database backup | `DatabaseDumpPlan`, `RestoreRehearsal` — dumps run from the database's *own* image |
| Agent | `Harbora.NodeAgent` — enrollment tokens, key store, control channel, command allowlist |
| Background jobs | `Job` / `JobWorker` / `JobReconciler`, with `JobKind.Backup` already defined |
| Multi-tenancy | `WorkspaceId` + EF global query filters, with an explicit unscoped mode for system work |

That existing engine is not a prototype. It handles cases a fresh implementation would take a long
time to rediscover — pre-restore safety snapshots, checksum gates before a destructive `rm -rf`,
restore rehearsal into a scratch database, invariant-culture timestamps because the panel's default
culture is Persian and Jalali years were landing in artifact filenames.

**Therefore: nothing in the existing engine is replaced.** The decision taken with the maintainer
was to introduce the new port, adapt the existing engine behind it, and add Kopia as a *second*
implementation selectable per repository.

---

## 2. Vocabulary: two things are called `IBackupEngine`

This is the one genuinely confusing part of the codebase after this change, so it is stated plainly.

| Type | Namespace | Shape | Role |
|---|---|---|---|
| `IBackupEngine` (existing) | `Harbora.Application.Abstractions` | target-oriented: "back up *this app/volume/database* to *that destination*" | The panel's original backup service. Unchanged. |
| `IBackupEngine` (new) | `Harbora.Backup.Contracts` | repository/snapshot-oriented: `CreateRepositoryAsync`, `CreateSnapshotAsync`, `RestoreAsync`, `CheckHealthAsync` | The pluggable storage-engine port defined in the brief. |

They are not competitors. The new one is a *lower* layer: it knows about repositories, snapshots and
bytes, and nothing about apps or managed services. The old one knows about Harbora's resources.

Do not merge them. The old interface's `QueueBackupAsync(workspaceId, type, targetRef, …)` cannot be
expressed in terms of repositories without dragging app/service knowledge into the engine port,
which is exactly the coupling this design is meant to prevent.

---

## 3. Module layout

Harbora is a layered monolith (`Domain` → `Application` → `Data`/`Infrastructure` → `Web`), not a
modular monolith. Rather than scatter backup types through those layers, the new work lives in its
own projects under `src/Modules/`, keeping the module's surface reviewable and its dependencies
one-directional:

```
src/Modules/Backup/
├── Harbora.Modules.Backup.Contracts/       ports + DTOs + enums   → (nothing)
├── Harbora.Modules.Backup.Domain/          entities + pure logic  → Harbora.Domain, Contracts
└── Harbora.Modules.Backup.Infrastructure/  adapters + services    → Domain, Harbora.Infrastructure, Harbora.Data
```

### Two naming decisions worth explaining

**`Harbora.Modules.Backup.*`, not `Harbora.Backup.*`.** The shorter name is a namespace sitting
directly under `Harbora`, which means C# name resolution finds it before any `using`-imported type
called `Backup` — shadowing the existing `Harbora.Domain.Backups.Backup` entity for *every* file in
the `Harbora.*` tree, `BackupEngine.cs` included. The alternative was scattering type aliases
through files this branch has no business editing.

**No separate `Application` project.** Harbora's own `Harbora.Application` contains only
abstractions; every implementation lives in `Harbora.Infrastructure`. Adding a module project that
held services would have introduced a layering convention the codebase does not use. The brief's
conceptual boundary is preserved — ports in `Contracts`, invariants in `Domain`, implementations in
`Infrastructure` — without inventing a fourth layer for this module alone.

One consequence is recorded honestly: both `IBackupEngine` types are in scope inside
`HarboraNativeBackupEngine.cs`, which needs `IBackupStorage` and `ISecretProtector` from
`Harbora.Application.Abstractions`. That file carries an explicit `using` alias rather than relying
on resolution order.

Sync (`src/Modules/Sync/`) is not yet created — see § 10.

### Dependency rules

- `Contracts` depends on nothing but `Harbora.Shared`. It is what an out-of-process agent or a future
  engine adapter compiles against.
- `Domain` holds entities and their invariants. No EF, no HTTP, no process execution.
- Nothing in `Modules/` references `Harbora.Web`.
- `Harbora.Data` references `Harbora.Backup.Domain` (one line, for `DbSet`s and the migration). This
  is the only inward edge, and it does not create a cycle: `Backup.Domain` never references `Data`.

### Why the reference graph is safe

```
Shared ← Domain ← Application ← Data ← Infrastructure ← Web
                                  ↑                ↑
                       Backup.Domain ─── Backup.Infrastructure
```

`Data → Backup.Domain → Harbora.Domain` and `Backup.Infrastructure → Harbora.Infrastructure → Data`.
No cycle, because `Backup.Domain` sits below `Data`.

---

## 4. Engine ports

### `IBackupEngine` (new)

```csharp
Task<BackupRepositoryResult>       CreateRepositoryAsync(CreateBackupRepositoryRequest, CancellationToken);
Task<BackupSnapshotResult>         CreateSnapshotAsync(CreateBackupSnapshotRequest, CancellationToken);
Task<RestoreResult>                RestoreAsync(RestoreBackupRequest, CancellationToken);
Task<BackupRepositoryHealthResult> CheckHealthAsync(Guid repositoryId, CancellationToken);
```

Adapters:

| Adapter | Engine | Notes |
|---|---|---|
| `HarboraNativeBackupEngine` | the existing tar + `ArchiveCipher` pipeline | The default. Behaviour identical to today. |
| `KopiaBackupEngine` | Kopia CLI | Content-addressed, deduplicated, real snapshot history. Feature-flagged. |

Selection is **per repository**, not global: a `BackupRepository` row carries an `Engine` column, and
`IBackupEngineResolver` hands back the adapter for that row. This matters because a repository's
stored data is in the engine's format — a repository written by Kopia can only be read by Kopia.
Making the choice global would silently strand existing artifacts the first time someone flipped it.

### `ISyncEngine`

```csharp
Task<SyncDeviceResult>       RegisterDeviceAsync(RegisterSyncDeviceRequest, CancellationToken);
Task<SyncFolderResult>       CreateFolderAsync(CreateSyncFolderRequest, CancellationToken);
Task<PairDeviceResult>       PairDeviceAsync(PairSyncDeviceRequest, CancellationToken);
Task<SyncFolderStatusResult> GetFolderStatusAsync(Guid folderId, CancellationToken);
```

Adapter: `SyncthingSyncEngine`, against Syncthing's REST API.

---

## 5. Talking to Kopia

Kopia is driven through its **CLI**, not its API server, and the reasoning is worth recording because
the brief expressed a preference for the server.

`kopia server` is designed for a long-lived process holding an *unlocked* repository. Running it
inside the panel means the repository password lives in a resident process for the panel's whole
uptime, and the server's control API becomes a second authentication surface to protect. The CLI, by
contrast, is a short-lived process per operation with the password supplied out-of-band and the
repository re-opened each time.

The adapter is nonetheless written against `IKopiaProcessRunner`, so an API-server implementation can
replace it without touching the engine — which is the substance of what the brief asked for.

### Process execution rules (non-negotiable)

1. Arguments are passed as a **list**, never a concatenated string. `ProcessStartInfo.ArgumentList`
   quotes each element itself.
2. **No shell.** No `sh -c`, no `cmd /c`. A path containing `;` is then just a path.
3. The repository password goes in via the `KOPIA_PASSWORD` environment variable, never `--password`
   on the command line, where any local user could read it from `/proc/<pid>/cmdline`.
4. Every argument that originates from user input is validated against an allowlist before it is
   used. Paths are canonicalised and confined (§ THREAT_MODEL).
5. `stdout`/`stderr` pass through `SecretRedactor` before reaching a log.
6. No `Process` call appears in a controller, a view, or the Application layer — only in
   `Backup.Infrastructure`.

---

## 6. Background work

Long operations never run inside an HTTP request. They go through the existing durable queue: a
`Job` row is persisted, then a worker claims it with an optimistic-concurrency stamp, so a restart
resumes rather than loses the work, and two workers cannot execute the same job.

New `JobKind` values are **appended** — the enum is persisted by value and existing rows must keep
their meaning:

```
Deployment = 0, Backup = 1, ServiceProvision = 2, CronRun = 3,   // existing — untouched
BackupSnapshot = 4, BackupRestore = 5, BackupVerify = 6,
BackupPrune = 7, RepositoryHealthCheck = 8
```

### The tenancy trap in background work

Every backup entity carries `WorkspaceId` and an EF global query filter. Background jobs run under
`SystemWorkspaceScope` (`IsUnscoped == true`) so they see every tenant.

A scheduler or sweeper constructed with a *request* scope — or with no scope at all — reads an
**empty** result set and reports success having done nothing. This is a silent failure: no
exception, no alert, and a backup schedule that appears healthy while never running. Any new
sessionless component must resolve `HarboraDbContext` from a scope registered as unscoped, and its
test must assert it sees rows from more than one workspace.

---

## 7. Data model

New entities, all workspace-scoped and all in `Harbora.Backup.Domain`:

- **`BackupRepository`** — where snapshots live. `Engine` (Native | Kopia), `Type`
  (Local, S3Compatible, AmazonS3, MinIO, BackblazeB2, SFTP, WebDav, HarboraNode, Custom), endpoint,
  bucket, region, base path, `CredentialReferenceId`, health timestamps, storage usage.
  **Credentials are never stored in plaintext** — the row holds a reference, and the secret itself is
  encrypted through `ISecretProtector`.
- **`BackupPolicy`** — what to back up, where, when, and for how long. Owns a `RetentionPolicy`
  (`KeepLatest`, hourly/daily/weekly/monthly/yearly, `MaximumAge`, `MaximumRepositorySize`).
- **`BackupSnapshot`** — one run. Sizes (original / stored / deduplicated), file count, verification
  status, `EngineSnapshotId` (the engine's own handle), failure reason, trigger.
- **`RestoreJob`** — restore type, destination, conflict strategy, progress, requester.

The existing `Backup`, `BackupDestination`, `BackupSchedule` and `BackupDelivery` tables are **not
modified**. New tables are added alongside; no existing migration is edited.

---

## 8. Integration points with the existing platform

| Seam | How this module attaches | Blast radius |
|---|---|---|
| `HarboraDbContext` | new `DbSet`s + `HasQueryFilter` per entity | additive |
| `JobKind` | appended enum members | additive; values never renumbered |
| `JobDispatcher` | new cases routed to module handlers | one `switch` |
| DI | one `AddBackupModule()` extension called from `Program.cs` | one line |
| Navigation | entries added to the existing nav, guarded by the feature flag | additive |
| `INotificationService` | reused as-is; `AlertEvent.BackupFailed` already exists | none |
| `ISecretProtector` | reused for credential encryption | none |

---

## 9. Feature flags

```json
{
  "Features": {
    "Backup": false,
    "Sync": false,
    "EncryptedSyncNode": false,
    "RemoteBackupAgent": false
  }
}
```

With `Features:Backup` off, the module registers no routes, contributes no navigation entries, and
schedules no jobs. The migration still applies — empty tables are inert, and gating schema behind a
flag would make enabling it a schema change rather than a config change.

`EncryptedSyncNode` is marked **experimental** in the UI. Syncthing's untrusted-device support is the
mechanism behind it, and a feature whose failure mode is "the always-on node could read your files"
should not present itself as settled.

---

## 10. Deliberate limits of this branch

Recorded so review is not surprised:

- The vertical slice is **backup**: repository → snapshot of an app volume → list → restore a file →
  verify. Sync ships as contracts, domain and documentation, without UI, because a half-built sync
  UI would be exactly the "screen with no working feature behind it" the brief forbids.
- **Docker is not available on the development machine used for this branch.** Container-backed
  integration tests are written and CI-runnable, but they were not executed here. They are not
  reported as passing.
- Kopia and Syncthing images are pinned by version tag. No digest is asserted, because a digest that
  was not actually observed from a registry is a fabricated identifier that passes review and fails
  on deploy.
