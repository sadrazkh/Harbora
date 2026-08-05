# Harbora Backup & Sync — Threat Model

Scope: the Backup and Sync modules, their engine adapters, the agent protocol they extend, and the
restore path. Assumes the platform's existing authentication, `ISecretProtector` and audit log.

A backup system is an unusually attractive target. It holds a copy of everything the platform
protects, in one place, in a format designed to be extracted — and its restore path is the one code
path in the product whose *purpose* is to overwrite live data.

---

## T1 — Command injection through engine arguments

**Vector.** Repository paths, bucket names, snapshot ids and restore destinations all reach a
`kopia` or `syncthing` invocation. A value like `foo; rm -rf /` reaching a shell is arbitrary code
execution as the panel's user.

**Mitigations.**
- Arguments are passed as a list (`ProcessStartInfo.ArgumentList`). Never a concatenated string.
- No shell is ever spawned — no `sh -c`, no `cmd /c`. Without a shell, `;` and `&&` are ordinary
  characters in an argument.
- Repository names, bucket names and snapshot ids are validated against a character allowlist before
  use.
- Enforced by test: `KopiaArgumentTests` feeds injection payloads through the argument builder and
  asserts they survive as single literal arguments.

**Residual.** An argument that Kopia itself interprets (e.g. one beginning with `-`) could be read as
a flag. Values that may start with `-` are separated with `--`.

---

## T2 — Path traversal on restore

**Vector.** Restore takes a destination path and a selection of entries inside a snapshot. Either
can contain `..`, an absolute path, or a symlink pointing out of the restore root. A snapshot is
attacker-influenced data whenever an attacker could write a file into a backed-up volume.

**Mitigations.**
- Destination paths are canonicalised with `Path.GetFullPath` and required to sit under a configured
  root; the check is on the *resolved* path, never on the string as supplied.
- Comparison is `Ordinal`, against a root normalised to end in a separator, so `/data/restore-evil`
  does not pass as being inside `/data/restore`.
- Archive entries are validated before extraction: rejected if rooted, if any segment is `..`, or if
  the joined path escapes the sandbox.
- Symlinks and Windows junctions in an archive are not recreated during restore.

**Residual.** An engine restoring through its own binary (Kopia writing files itself) is trusted to
confine its own output. The destination handed to it is validated; its internal behaviour is not
re-implemented.

---

## T3 — Credential disclosure

**Vector.** Repository credentials (S3 keys, SFTP passwords, repository passwords) can leak through
four channels: the database, the process table, logs, and API responses.

**Mitigations.**
- **Database:** credentials are encrypted via `ISecretProtector`. Entities hold a
  `CredentialReferenceId`, not a secret.
- **Process table:** the repository password is passed as `KOPIA_PASSWORD` in the child process
  environment, never as a CLI flag. `--password=…` on the command line is world-readable via
  `/proc/<pid>/cmdline` on Linux.
- **Logs:** all engine `stdout`/`stderr` passes through `SecretRedactor` before being logged.
- **API:** secrets are never included in responses — not even masked round-trips. Write-only fields.
- Database dump passwords use the engine's environment-variable form (`PGPASSWORD`, `MYSQL_PWD`),
  matching what `DatabaseDumpPlan` already does.

**Residual.** A repository password in the environment is readable by a process running as the same
user. Accepted: that process is the panel itself, which already holds the master key.

---

## T4 — Cross-tenant access

**Vector.** Snapshots, repositories and devices belonging to one workspace being read, restored or
deleted by another.

**Mitigations.**
- Every new entity carries `WorkspaceId` with an EF global query filter, so isolation is a property
  of the model rather than something each query must remember.
- API endpoints resolve the workspace from the session, never from a request parameter.
- `IgnoreQueryFilters()` is used only in sessionless system work, and each use carries a comment
  saying why.
- Enforced by test: cross-tenant fetch, restore and delete attempts must return not-found.

**Residual — and it cuts the other way.** The same filter that prevents leakage causes *silent
data loss of function* in sessionless code: a scheduler or sweeper without an unscoped context reads
an empty set and reports success having done nothing. This has bitten this codebase before. Any
sessionless component must run unscoped, and its test must assert it sees more than one workspace.

---

## T5 — Restore as a destructive weapon

**Vector.** Restore is the most dangerous authenticated operation in the product. An attacker who can
trigger one — or a user who mis-clicks — overwrites live application data with old or attacker-chosen
contents. Restoring an *old but valid* backup is a denial-of-service that passes every integrity
check.

**Mitigations.**
- Restore onto a live target requires explicit confirmation naming the target; it is never a
  side-effect of another action.
- Integrity is verified *before* anything is destroyed. The existing engine's checksum gate stays: a
  corrupt archive must not be discovered halfway through an `rm -rf`.
- A pre-restore safety snapshot of the current data is taken by default, so a restore of the wrong
  backup is itself recoverable.
- Restore-to-new-location is offered as a first-class option, so inspecting a backup does not require
  overwriting anything.
- Every restore is written to the audit log with actor, target, snapshot and outcome.

---

## T6 — Enrollment token abuse

**Vector.** A device-enrollment token is a bearer credential that converts into long-lived device
credentials. Stolen, guessed, or replayed, it yields a device inside the tenant.

**Mitigations.**
- Tokens are short-lived, single-use, and revocable.
- Only a **hash** is stored; the raw value is displayed exactly once and never logged.
- Comparison is constant-time.
- Redemption is rate-limited per source address, and failures are audited.
- Redemption is atomic — a conditional update on the unused row — so two concurrent redemptions
  cannot both succeed.

**Residual.** A token pasted into an install command is visible in that machine's shell history. The
short lifetime and single use are what bound this.

---

## T7 — Exposing Kopia's and Syncthing's own interfaces

**Vector.** Both ship web UIs and APIs. Reachable from the internet, either is a direct path to every
byte the platform protects, bypassing Harbora's authentication entirely.

**Mitigations.**
- Neither is published to a public interface. Ports bind to loopback or a private network only.
- Any debug UI is behind a feature flag, **off** by default, and authenticated when on.
- Containers run non-root, with read-only filesystems where the workload allows, and with explicit
  resource limits.
- Images are pinned by version tag — never `latest`, whose meaning changes under you.

---

## T8 — Malicious or corrupt archive content

**Vector.** Archive bombs (small archive, enormous expansion) exhausting disk; hostile entries
(device files, hardlinks, symlinks) written during extraction.

**Mitigations.**
- Extraction happens in a sandbox directory, not directly onto the target.
- Expanded size and entry count are bounded; exceeding the bound aborts the restore.
- Only regular files and directories are materialised. Special file types are skipped.
- Free space is checked before extraction begins.

---

## T9 — Sync is not backup

**Vector.** Not an attacker — a design error with the same consequence. Deletion and corruption
*propagate* across synced devices. Treating a sync folder as a backup means ransomware encrypting one
laptop replicates the encryption to every other device, and to the always-on node.

**Mitigations.**
- Separate modules, separate data models, separate UI, separate restore paths. A sync space is never
  presented as, or counted as, a backup.
- Versioning is available per sync folder — it limits the damage but does not make sync a backup, and
  the UI says so rather than implying otherwise.
- The UI does not offer to "restore" a sync space.

---

## T10 — Trust placed in the always-on node

**Vector.** The always-on Harbora node relays files between devices that are rarely online at the
same time. If it holds plaintext, compromising it yields every synced file.

**Mitigations.**
- `EncryptedReceiveOnly` mode: the node stores ciphertext and holds no decryption material.
- Decryption keys exist only on trusted devices; the node is never issued one.
- The mode is marked **experimental** in the UI, because the guarantee comes from the sync engine's
  untrusted-device support rather than from Harbora, and because the failure mode — the node quietly
  holding plaintext — is invisible to the user.

---

## T11 — Duplicate and conflicting job execution

**Vector.** Two backups of the same target running concurrently produce a torn or wasteful result;
two restores concurrently produce an indeterminate one.

**Mitigations.**
- Jobs are claimed with an optimistic-concurrency stamp, so two workers cannot execute the same job.
- Incompatible operations on one target are mutually excluded by a lock keyed on the target.
- Job handlers are idempotent: re-running a claimed-then-crashed job converges rather than
  duplicating.
- Every job has a timeout; a hung engine process does not hold its lock forever.

---

## Out of scope for this branch

- Billing and payment (usage metrics are recorded; nothing is charged).
- mTLS between control plane and agent — TLS today, with the design leaving room for it.
- Hardware security modules for key storage.
- Signed job payloads (the allowlist is the current control; signing is a later hardening step).
