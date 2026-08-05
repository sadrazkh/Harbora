# Backup API — `/api/v1/backup`

Auth: bearer API token (`TokenAuthenticationHandler`), same as `/api/v1`.
Every route returns **404 while `Features:Backup` is off** — the routes do not exist rather than
existing and refusing.

Errors are **RFC 7807 Problem Details**. This differs from `/api/v1`'s `{"error": "..."}` shape;
deliberate, because this is a new surface with no clients, where changing the existing endpoints
would break the CLI.

> **On OpenAPI.** No machine-generated document is served. The project has no OpenAPI package, and
> adding one would emit a document describing *every* controller in the panel — a broader change and
> a broader disclosure surface than this branch should introduce on its own. This file is the
> reference; wiring `Microsoft.AspNetCore.OpenApi` and gating it to Development is listed as
> follow-up in MERGE_GUIDE.md § 9.

---

## Conventions

**Paging** — `?page=1&pageSize=50`. `pageSize` is clamped to **200**. Responses wrap results:

```json
{ "items": [ ... ], "page": 1, "pageSize": 50, "totalCount": 137, "totalPages": 3, "hasMore": true }
```

**Sorting** — `?sort=field` or `?sort=-field` for descending. An unrecognised field is a `400`, not a
silent fallback: a caller who thinks they sorted and did not will read the first page and draw the
wrong conclusion.

**Filtering** — an unrecognised enum value is likewise a `400`.

**Idempotency** — `Idempotency-Key: <=128 chars` on `POST /snapshots` and `POST /restore-jobs`.
A repeat returns the original id with `"replayed": true` and starts no new work. The key is stored
in the database (not in process memory), so a retry landing on another replica gets the same answer.
A present-but-unusable key is a `400` — ignoring it would give the caller a guarantee they do not
have. Absent header simply means no idempotency was requested.

**Secrets** — never returned. Not the repository password, not access keys, not masked. Write-only
on the way in.

---

## Repositories

| Method | Path | Notes |
|---|---|---|
| `GET` | `/repositories` | `?status=` `?sort=name\|-name\|created\|-created` |
| `POST` | `/repositories` | Creates **and opens** the repository; fails if the engine cannot |
| `GET` | `/repositories/{id}` | |
| `POST` | `/repositories/{id}/health` | Re-checks and records the verdict |
| `DELETE` | `/repositories/{id}` | `409` while policies or snapshots still reference it |

```jsonc
// POST /repositories
{
  "name": "Nightly", "type": "Local", "engine": "Native",
  "password": "…",            // write-only; without it the repository cannot be read at all
  "localPath": "/var/lib/harbora/repo"
}
```

For S3-family: `"type": "S3Compatible"`, plus `endpoint`, `bucket`, `region`, `accessKeyId`,
`secretAccessKey`. **Kopia supports `Local` only** — its object-storage backends take credentials as
command-line flags, which is readable by any local user via `/proc/<pid>/cmdline`.

---

## Targets

`GET /targets` — what this deployment can actually back up, so a client need not guess:

```json
{
  "supported": ["Directory", "DockerVolume", "Database"],
  "allowedSourceRoots": ["/srv/data"],
  "unsupported": [{ "type": "Application", "reason": "Application targets are not implemented yet." }]
}
```

`allowedSourceRoots` is empty by default and **no directory can be backed up until it is set**.

`targetRef` depends on the type: an allowed path for `Directory`, the volume's name for
`DockerVolume`, and the managed database's **id** for `Database`.

**Database engines.** PostgreSQL, MySQL and MariaDB are dumped through their own client. MongoDB and
Redis are refused with a specific reason: Redis has no logical dump and its data volume is the honest
artifact (use `DockerVolume`), and `mongodump` has no way to take a password that does not end up in
the process table.

**Restoring a database** uses `"restoreType": "Database"` with the **service id** as `destination`,
and `confirmationText` set to the database's display name. It always replaces the live contents —
there is no version of loading a dump that leaves what is there alone.

---

## Policies

| Method | Path | Notes |
|---|---|---|
| `GET` | `/policies` | `?enabled=true` |
| `POST` | `/policies` | `400` with `ValidationProblemDetails`, one entry per field |
| `DELETE` | `/policies/{id}` | Snapshots keep their history (`PolicyId` is set to null) |

```jsonc
// POST /policies
{
  "name": "Nightly data", "repositoryId": "…",
  "targetType": "DockerVolume", "targetRef": "harbora_app_data",
  "schedule": "0 3 * * *", "timezone": "Asia/Tehran",   // cron read in the tenant's own timezone
  "keepLatest": 3, "keepDaily": 30, "keepMonthly": 12
}
```

Retention tiers are **additive** — a snapshot survives if any tier still wants it. `keepLatest` is a
floor that an age ceiling cannot cut below. A policy that would keep nothing is rejected.

---

## Snapshots

| Method | Path | Notes |
|---|---|---|
| `GET` | `/snapshots` | `?repositoryId=` `?status=` `?targetRef=` `?sort=created\|-created\|size\|-size` |
| `GET` | `/snapshots/{id}` | |
| `GET` | `/snapshots/{id}/entries?path=` | One directory level, so a large archive browses a screen at a time |
| `POST` | `/snapshots` | `202 Accepted`; idempotent. `409` if that target is already backing up |
| `DELETE` | `/snapshots/{id}` | Removed from the engine first, then the row |

`verificationStatus` is `NotVerified` / `Passed` / `Failed` / `Skipped`. **`NotVerified` and `Passed`
are different answers** — a backup nobody has checked is a promise, not a safety net.

---

## Restores

| Method | Path | Notes |
|---|---|---|
| `GET` | `/restore-jobs` | |
| `GET` | `/restore-jobs/{id}` | Poll `status` and `progress` |
| `POST` | `/restore-jobs` | `202 Accepted`; idempotent |

```jsonc
// POST /restore-jobs
{
  "snapshotId": "…",
  "destination": "/var/lib/harbora/restore/check",   // must resolve inside RestoreRoot
  "conflictStrategy": "Fail",                        // Fail | Skip | Rename | Overwrite | RestoreToNewLocation
  "entries": ["data/config.yml"],                    // omit to restore everything
  "confirmationText": "check"                        // required only when the destination has data
}
```

Restore is the most destructive authenticated operation here, and is guarded accordingly: the
destination is confined to `RestoreRoot`, `Fail` is the default strategy, writing over a non-empty
destination requires typing that folder's name back, and every request, completion and failure is
audited.

---

# Sync API — `/api/v1/sync`

Same conventions: bearer token, Problem Details, paging, sorting, `Idempotency-Key`, and **404 on
every route while `Features:Sync` is off**. Idempotency keys are namespaced per module, so the same
key used against both APIs is two requests rather than a replay of an unrelated result.

> **There is no restore endpoint here, and there will not be one.** A sync space has no earlier
> state to go back to — deletions and corruption propagate to every device, usually within seconds.
> A test reflects over this controller's routes and fails if one ever appears named `Restore`,
> `Snapshot` or `Recover`. For anything that may need recovering, use the Backup API.

## This node

`GET /node` — the identity another device needs in order to pair, plus what this deployment accepts:

```json
{
  "deviceId": "P56IOI7-MZJNU2Y-…",
  "engineReachable": true,
  "allowedRoots": ["/srv/sync"],
  "encryptedNodeAllowed": false,
  "notice": "Sync replicates deletions. It is not a backup."
}
```

`allowedRoots` is empty by default and **no space can be created until it is set**. A sync folder is
a directory this node both reads *and writes* on a remote device's instruction — a stronger
capability than backup's read, so the default is closed.

## Spaces

| Method | Path | Notes |
|---|---|---|
| `GET` | `/spaces` | `?status=` `?sort=name\|-name\|pending\|-pending` |
| `GET` | `/spaces/{id}` | |
| `POST` | `/spaces` | Idempotent. Creates the folder in the engine before the row is kept |
| `POST` | `/spaces/{id}/pause?paused=true` | Stops syncing without removing anything |
| `POST` | `/spaces/{id}/refresh` | Re-reads status and reconciles the conflict list |

```jsonc
// POST /spaces
{
  "name": "Documents",
  "localPath": "/srv/sync/documents",   // must resolve inside an allowed root
  "mode": "SendAndReceive",             // SendAndReceive | SendOnly | ReceiveOnly
  "versioningMode": "Trash",            // None | Trash | Simple | Staggered
  "versioningParameter": 7              // days for Trash, versions kept for Simple
}
```

Versioning limits the damage sync propagates. It does not make sync a backup.

## Devices and pairings

| Method | Path | Notes |
|---|---|---|
| `GET` | `/devices` | `?untrusted=true` |
| `POST` | `/devices` | `deviceId` is the engine's key fingerprint, exchanged out of band |
| `GET` | `/pairings` | `?spaceId=` |
| `POST` | `/pairings` | Idempotent. Returns `acceptedByPeer: false` — pairing is mutual |
| `DELETE` | `/pairings?spaceId=&deviceId=` | The device **keeps the files it already has** |

```jsonc
// POST /pairings
{
  "spaceId": "…", "deviceId": "…",
  "mode": "EncryptedReceiveOnly",
  "encryptionPassword": "at-least-12-characters"   // write-only; never returned
}
```

**The rule the API enforces in both directions.** An untrusted device exists so it cannot read what
it stores, so it may only be paired as `EncryptedReceiveOnly` — any other mode would send it readable
files. And a *trusted* device may not be given an encrypted-only share either, or "which devices can
read this folder" stops being answerable from the device list. Both return `400` with the field named.

A pairing response reports `isEncrypted`, never the password.

## Conflicts

| Method | Path | Notes |
|---|---|---|
| `GET` | `/conflicts` | `?spaceId=` `?openOnly=true` |
| `POST` | `/conflicts/{id}/resolution` | `KeptLocal` \| `KeptRemote` \| `KeptBoth` |

**Recording only.** Harbora writes down what was decided and moves or deletes nothing — whichever
copy an automatic rule discarded would be somebody's work, and the file operations belong on the
device holding them. There is deliberately no "resolve automatically".
