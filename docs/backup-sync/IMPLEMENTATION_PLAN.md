# Harbora Backup & Sync — Implementation Plan

Branch: `feature/harbora-backup-sync`, cut from `master` at `a55d27f`.

---

## Scope decision

The original brief listed nine phases covering backup, database backup, a remote agent, file sync,
an always-on encrypted node, and fifteen UI pages. Two facts reshaped that:

1. **Much of it already exists** (see ARCHITECTURE.md § 1) — a full backup engine, and a node agent
   with enrollment, a command allowlist, and a volume archiver.
2. **Docker is unavailable on this development machine**, so anything container-backed can be
   written but not verified here.

Agreed with the maintainer: deliver one **real vertical slice** rather than a broad, shallow, partly
fake surface. The brief's own instruction — *"هیچ صفحه نمایشی Fake، داده Mock در Production، دکمه
بدون عملکرد یا API Placeholder به‌عنوان قابلیت کامل تحویل نده"* — is the governing constraint.

**In this branch:**

```
Repository (Local + S3)
  → snapshot a Harbora app volume
  → list snapshots
  → browse a snapshot
  → restore one file/folder
  → verify the result
```

plus policies, retention, background jobs, the Kopia adapter, UI for the above, and tests.

**Contracts and documentation only** (no UI, no half-built runtime): Sync, and the backup-specific
extension of the existing agent. Marked as such in the UI's absence, not by a disabled button.

---

## Phases

Each phase ends in one commit and leaves the build green.

### Phase 0 — Analysis and architecture ✅
`docs/backup-sync/{ARCHITECTURE,IMPLEMENTATION_PLAN,THREAT_MODEL}.md`
→ `docs(backup-sync): define architecture and implementation plan`

### Phase 1 — Backup domain and contracts
- `src/Modules/Backup/Harbora.Backup.Contracts` — `IBackupEngine`, `IDatabaseBackupProvider`,
  requests/results, enums.
- `src/Modules/Backup/Harbora.Backup.Domain` — `BackupRepository`, `BackupPolicy`, `RetentionPolicy`,
  `BackupSnapshot`, `RestoreJob`.
- Retention calculation and policy validation as pure, testable functions.
- `DbSet`s + query filters in `HarboraDbContext`; **new** migration.
- Appended `JobKind` members.
→ `feat(backup): add backup domain model and contracts`

### Phase 2 — Engine adapters
- `IKopiaProcessRunner` + `KopiaProcessRunner` — argument lists, no shell, password via environment,
  output redacted.
- `KopiaBackupEngine` — repository create/connect, snapshot, restore, health.
- `HarboraNativeBackupEngine` — the existing engine behind the new port. **No behaviour change.**
- `IBackupEngineResolver` — per-repository adapter selection.
→ `feat(backup): integrate kopia backup engine`

### Phase 3 — Policies, scheduling, retention, verification
- Policy scheduler (unscoped; see the tenancy trap in ARCHITECTURE.md § 6).
- Job handlers: snapshot, verify, prune, health check — idempotent, cancellable, timed out,
  progress-reporting, mutually excluded per target.
→ `feat(backup): add policies scheduling and retention jobs`

### Phase 4 — App, volume and database targets
- Volume discovery for an app; app metadata capture (secret *references*, never secret values).
- `IDatabaseBackupProvider` implementations reusing the existing `DatabaseDumpPlan` approach.
- Restore-to-same / restore-to-new, with confirmation before any live overwrite.
→ `feat(backup): support application volume and database backups`

### Phase 5 — Agent (contracts only in this branch)
The existing `Harbora.NodeAgent` already enrolls, holds keys, and executes an allowlisted command
set. This branch adds the *contract* for backup job dispatch, not a second agent.
→ `feat(agent): add remote backup job contracts`

### Phase 6 — Sync domain and contracts
- `ISyncEngine`, sync enums, `SyncSpace`/`SyncDevice`/`SyncFolder` entities, conflict model.
- `SyncthingSyncEngine` against the REST API.
- No UI. Not enabled.
→ `feat(sync): add sync domain and syncthing engine contracts`

### Phase 7 — Backup UI
Within the existing design system — existing components, existing tokens, no new colours, radii,
shadows or type scales. Overview, Repositories, Policies, Snapshots, Restore Center.
Simple/Advanced split, loading/empty/error states.
→ `feat(ui): add backup management experience`

### Phase 8 — Deployment
Pinned Kopia/Syncthing services in a development compose file, health checks, non-root, resource
limits, documented environment variables. Nothing bound to a public interface.
→ `chore(backup-sync): add pinned development compose services`

### Phase 9 — Tests and documentation
Unit: retention calculation, policy validation, tenant isolation, snapshot state transitions,
restore validation, path confinement, argument escaping, enrollment token lifecycle, sync mode
validation.
Integration (CI-runnable, container-backed): repository create, snapshot, list, restore, prune.
Failure: repository unavailable, engine exits unexpectedly, dump fails, verification fails, invalid
token, revoked device, cross-tenant access, path traversal, duplicate job.
Plus `MERGE_GUIDE.md`.
→ `test(backup-sync): add coverage and merge documentation`

---

## Rules held throughout

- No commit on `master`. No merge, rebase or force-push. No push at all unless asked.
- No existing migration edited — new migrations only.
- No existing test deleted or skipped.
- No repository-wide reformatting; no unrelated file touched.
- Enum values appended, never renumbered.
- Package versions pinned; no opportunistic dependency upgrades.
- Feature flags default to **off**.
- Nothing reported as verified that was not actually run — specifically, container-backed tests on
  this machine.
