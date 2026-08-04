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
  "supported": ["Directory", "DockerVolume"],
  "allowedSourceRoots": ["/srv/data"],
  "unsupported": [{ "type": "Database", "reason": "Database targets are not implemented yet." }]
}
```

`allowedSourceRoots` is empty by default and **no directory can be backed up until it is set**.

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
