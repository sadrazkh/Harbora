# Round five — progress, and where to pick it up

Baseline: `2b4ae90`, 6286 `Harbora.Tests`. **Now 6429**, CI green on `9a27445`, deployed as `0.11.0`.

## The finding that reframed the round: CI had not run since 8 August

Every one of the last 100 CI runs — back to 16 August and in fact to the 8 August consolidation —
had **zero jobs** and was named after the file path rather than `CI`. That is GitHub failing to load
the workflow. The cause was one expression in the final job:

```yaml
results='${{ join(needs.*.result, " ") }}'   # double quotes: not valid in an expression
```

So for seven weeks nothing in `ci.yml` executed: no .NET suite, no Postgres lane, no bundle build, no
script check, no agent publish, and not the guard whose purpose is "one green tick means every suite
ran". **Every "proven in the Postgres/CI lane" claim made since 8 August was unfounded** — including
several made by agents and by me in rounds three and four. Fixed in `f1da42a`.

Turning CI back on exposed what had rotted underneath. In order of discovery:

| Failure | Cause | Fix |
|---|---|---|
| 7 × `RestoreDrillScriptTests` | fake binaries committed `100644` from Windows; Git Bash does not check the bit | `13db2f5` |
| 4 × restore/import HTTP tests (500 on Linux) | `Backups:StagingDir` never overridden → `/var/lib/harbora/backups`; on Windows drive-relative (`E:\var\lib\...`, 953 junk files), on Linux unwritable | `5e6c23d` |
| — (latent) | `Harbora:WorkDir` override bound nothing: `HarboraRuntimeOptions` binds `Runtime` | `5e6c23d`, isolation test fails 6/7 without it |
| 29 × Postgres upgrade tests | D1 (`b0ad318`, 26 Aug) seeded `AppManagedServices` at the 6 Aug boundary; the table is created 23 Aug | `803e219`, own boundary |
| 1 × `WorkspaceFilterDeleteTests` | App with no environment after `EnvironmentRequired` (17 Aug) | `803e219` |
| 1 × sign-in token, Postgres only | `ExecuteUpdateAsync` bypasses the tracker; `LiveAsync` got the stale tracked row | `7140c4d` |
| 25 × upgrade tests | shared seed's apps had no environment — the migration refuses that loudly, on purpose | `0f8a113` |
| 25 × upgrade tests | **real product bug**: `DeploymentActiveIndex` (HARBORA-0057, round 3) built a UNIQUE index with no settle step; an install with a queued + building pair could not upgrade | `9a27445` |

The last one matters most: it is a migration that would have left a panel unable to boot. The live
server applied it while it happened to hold no such pair.

CI tooling improved on the way: failing tests are now named on the run page (`380fccc`), the full list
is always the first annotation (`13db2f5`), and failures are grouped by distinct message (`63fbaa3`) —
which is what exposed the sign-in-token failure hiding behind 29 identical fixture errors.

## Landed features

| | Commit | Harbora.Tests |
|---|---|---|
| A — a build that produced no image fails honestly; cache-restore failure retries once without cache | `a340d67` | 6328 |
| C — resizing an open web terminal reaches the shell again (and resizes are ordered, newest wins) | `48af27d` | 6349 |
| B — dangling images pruned daily, shown in the disk report | `6adb251` | 6426 |
| Dangling sweep waits for a running build (< 2 h old) instead of racing it | `55fd872` | 6429 |

## Deployed and verified on the live server

- HEAD `9a27445`, image built 21:22:31, panel `Up (healthy)`, `/api/v1/version` → `0.11.0`
- 119 migrations (none new this round — correct)
- `Runtime__ImageRetentionCount=2` survived the update; `Jobs__MaxConcurrency=4`
- `Runtime__DanglingImageSweepHours` is not in `.env`; `docker inspect` lists it as a bare key but the
  process does not see it, so the code default (24 h) applies — correct, but not discoverable in `.env`
- **The sweep ran unattended 10.5 minutes after boot:** `pruned 92 dangling image(s), the daemons
  reported 8.82 GB reclaimed … failed on 0 server(s)`. Disk 40% → **30%**. 15 containers before and after.
- Rollback: `harbora/panel:before-0.11.0`

## Releases had silently stopped

`tag-release.yml` creates a tag on a version bump, but a tag pushed with `GITHUB_TOKEN` cannot trigger
another workflow, so `release-cli.yml` never ran for any bot-made tag: **v0.6.0, v0.7.0, v0.8.0 and
v0.11.0 got no release** while the tagging job reported success. Only hand-pushed tags (v0.9.0,
v0.10.0) were ever released. Fixed in `4921002` by dispatching the release on the tag ref. v0.11.0 was
released by re-pushing the identical tag object as the owner; `release-cli` succeeded.

## Not done — start here next time

1. **Nightly live-host lane** fails at "Every secret this lane needs must be configured" — repository
   secrets are the owner's to set; nothing in code can do it.
2. **Remote-agent builds** do not carry the daemon's error text: the agent aborts the connection after
   headers, so the panel sees a truncated-response error and the cache-restore retry cannot fire there.
   No remote nodes today. Needs an in-band failure line like `PullErrorMarker`.
3. **`PullImageAsync` has the same `Progress<T>` race** A fixed for builds (the last messages can be
   handled after the call returns). Small.
4. `install.sh` does not backfill `Runtime__DanglingImageSweepHours`, so the setting works but is not
   visible in `.env` the way retention and job concurrency are.
5. Test litter on the owner's machine from before the factory fix: `E:\var\lib\harbora` (1.4 MB) and
   `C:\var\lib\harbora`. Only test output; not deleted without asking.
6. Backlog: 9 open (mostly L), 16 partial.
