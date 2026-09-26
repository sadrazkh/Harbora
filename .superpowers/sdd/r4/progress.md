# Round four — progress, and where to pick it up

Stopped deliberately on 2026-09-26 at the owner's request. Everything below is landed, pushed and
deployed unless it says otherwise.

Baseline at start: `b5c7e28`, 6224 `Harbora.Tests`. **Now 6286.** Tagged `v0.10.0`.

Every landing was verified by me after rebase — full suite, exit code captured before piping, and the
`Harbora.Tests.dll` line confirmed present in the log. An agent's own number is never the one here.

## Landed

| Item | Commit | Harbora.Tests |
|---|---|---|
| C — mobile slide-over locks the page scroll | `a8adb2d` | 6224 (+8 node tests) |
| B — `Runtime__ImageRetentionCount` reaches the container | `e3c4846` | 6227 (+3) |
| A — read-only disk usage report | `635bb65` | 6245 (+18) |
| D — one-time sign-in tokens | `a5979d7` | 6286 (+41) |
| 0.10.0 version bump | `6d2d44a` | 6286 |

## Deployed and verified on the live server

Not "install.sh said OK", which looks identical whether or not it did anything:

- server HEAD `6d2d44a`
- panel image built 23:21:01, container started 23:21:12, `Up (healthy)`
- `https://platform.irnetfree.info/api/v1/version` → `{"server":"0.10.0","cli":"0.10.0"}`
- migrations 118 → **119**, newest is `20260925205235_SignInTokens`
- rollback tag `harbora/panel:before-0.10.0` exists

### The retention change the owner asked for

`install.sh update` backfilled `Runtime__ImageRetentionCount=5` into `deploy/.env` on its own — which
is live proof the backfill half works on a real install. Set to **2** and the panel restarted, then
confirmed the decisive thing:

```
docker inspect harbora-panel → Runtime__ImageRetentionCount=2
```

The variable **reaches the process**, verified on the server rather than by parsing the compose file.
`2` = the running image plus one previous, so one step of instant rollback survives.

## Not done — start here tomorrow

1. **The cleanup has not been run, so nothing has actually been reclaimed yet.** Lowering retention
   applies on each app's *next* deploy; to apply the new window now to apps that have not redeployed,
   press **Clean up disk** on the Monitoring page. I cannot: it needs a signed-in session and I do not
   enter passwords. One click.
2. **I never got the disk figures.** The command that would have measured `df -h` and listed the
   `harbora/*` images was refused by a transient tooling outage, not by anything on the server. Re-run
   it, or just open the new disk report page — that is what it is for.
3. **The one-time token feature's deepest claim is unverified locally.** See below.

## What to be careful about in D's work

EF's InMemory provider — what every non-Postgres test in this project runs against — **does not
implement `ExecuteUpdateAsync` at all.** So `SignInTokenService.RedeemAsync` has an `IsRelational()`
gate: the real atomic conditional update on Postgres, and a deliberately non-atomic read-then-write on
InMemory so the mint/redeem/banner/revoke paths can be exercised locally at all. That fallback is
**not** proof of the single-use guarantee.

The guarantee lives in one Postgres-lane test that races two connections against the real
`ExecuteUpdateAsync`. There is no Postgres on this machine, so it **skipped**. Nobody has watched it
pass. That is the one thing about this feature that is reasoned and compiled rather than observed, and
it is the thing the whole feature rests on — worth confirming in the CI lane before leaning on it.

Chosen lifetimes: token redeemable for **15 minutes** (a bearer secret in transit), resulting session
**1 hour** — the same constant as `SupportAccess.Lifetime`, pinned equal by a test, because an agent
driving the panel is the same shape of temporary revocable access as a support engineer.

## Two things that were nearly missed, both the same defect class

**The CI line ran one named js test file.** `deployment-logs.test.mjs` was named explicitly, so any
test added beside it existed and never ran — coverage on paper, zero execution. It is a glob now, and
the agent checked something worth knowing: `node --test <directory>` does **not** discover files on
Node 22, it falls through to the CJS loader and fails. The glob form was verified to pick up both
files; I re-ran it myself and got 12 passing.

**The obvious fix for the scroll bug would have done nothing.** `body` and `html` are both `h-full`
and never scroll; the real scroller is an inner `div` with `overflow-y-auto`, now `id="content-scroll"`.
Locking `body` would have looked correct and changed nothing.

## A defect in my own process doc

`CONSTRAINTS.md` gave a literal `/tmp/t.log`, and four parallel agents all followed it on a shared
`/tmp`. One noticed a sibling worktree's paths in what it thought was its own log; the others had no
reason to look. The name now carries the branch (`e3c4846`'s parent commit). If a future round adds a
shared literal path to a doc several agents read, this happens again.

## An agent that hung, and what it was not

D's suite sat for 41 minutes against a ~3-minute baseline and wrote no report, so I committed its work
as WIP to protect it (`be8401c`) and killed the processes. It was **not** a defect in its code: the
token tests run in seconds (32 unit + 9 HTTP), the build was clean with zero warnings, and the full
suite passed in about three minutes once nothing else was running. The cause was machine contention —
I was verifying three other worktrees concurrently. Worth remembering before assuming a hang is a bug:
check the load first.

The WIP rescue was right anyway. An earlier round lost a complete sub-project exactly this way.

---

# The disk measurement, taken after the deploy — and it changes the picture

Measured on the live server once the tooling let me:

```
/dev/sda1  96G  92G  4.3G  96% /

docker system df
Images          199 total,  15 active,  83.76GB,  70.16GB reclaimable (83%)
Build Cache     196 total,   0 active,   9.22GB
Local Volumes    62 total,  16 active,   1.86GB
```

**165 of the images are dangling — untagged, unreferenced — against 35 tagged.** Individual sizes
show many at 4.09 GB and 2.67 GB; the honest total is Docker's own **70.16 GB reclaimable**, not the
sum of those, for exactly the shared-layer reason the new report page states.

## What this means for the feature that just shipped

Harbora's own build images are **13 images, roughly 10 GB**, and they are already at about two per
app — so setting retention to 2 changes almost nothing here, and a **Clean up disk** run would
reclaim very little.

`DiskCleanupService` only ever considers images under the `harbora/` prefix. That is deliberate and
correct — it must never delete a customer's image — but it means the platform's own cleanup is
structurally blind to the 70 GB actually filling this disk. The new report inherits the same blind
spot, because it reuses the same candidate rule on purpose.

So the honest summary: the round delivered what was asked for, and **it is not what is filling the
disk.** The report will say "10 GB of build images, little reclaimable" on a machine that is 96% full.

## What is actually filling it

Leftover intermediate build images. The 2.67 GB and 4.09 GB dangling entries line up with repeated
Loomi and panel builds — every rebuild leaves its predecessor's intermediate layers untagged, and
nothing on this platform removes those.

Reclaiming them is `docker image prune` (dangling only, keeps anything any container references) plus
`docker builder prune` for the 9.2 GB of build cache. Neither was run: deleting ~70 GB on production
is irreversible and was outside what had been authorised. **This is the first decision waiting.**

Worth considering as a follow-up rather than a one-off shell command: the platform prunes its own
tagged images and nothing else, so a dangling-image sweep — opt-in, reported, with the same
per-server and not-examined discipline the rest of `DiskCleanupService` keeps — is the missing half
of the feature. That is a design decision, not a bug fix, so it belongs in a plan.

---

# 2026-09-26 — the prune, and what the deploy history shows

## Pruned, on the owner's instruction

```
before:  /dev/sda1 96G 92G 4.3G 96%    199 images, 165 dangling
docker image prune -f      → 69.78 GB reclaimed
docker builder prune -f    →  0.52 GB reclaimed
after:   /dev/sda1 96G 27G  70G 28%     35 images
```

All 13 running containers identical before and after (names diffed), panel healthy, `0.10.0`.

Still reclaimable: 6.9 GB of **tagged but unused** images — old `dotnet/sdk:8.0`/`9.0` bases, the
n8n image, and two stale panel rollback tags (`before-docker29`, `before-0.10.0`). Not touched:
removing them needs `prune -a`, which also removes build bases and forces re-pulls. Owner's call.

## The transport fix is proven in production

DriveUnion #40, 2026-09-11 08:07 — the first deployment after `0e8ee95`/`b5c7e28` went live — succeeded,
and so did 41, 42, 43. Loomi, xuifleet and subscriptionlink have deployed repeatedly since. The Broken
pipe is gone.

## Two defects the history exposes — not fixed, waiting for a decision

**1. `--cache-from` on the classic builder fails intermittently on Docker 29.** Loomi #9 and #12 and
DriveUnion #44 all died on the same daemon error:

```
failed to restore cached image from "sha256:…" to sha256:…: failed to create cache image: …
```

Each was followed by a retry that succeeded. The classic builder is deprecated in Docker 29 and its
cache-restore path is the flaky part — so the owner's original suspicion of cache-from was half right:
it was not what caused the Broken pipe, but it does break some builds on its own.

**2. When that error arrives as a plain `stream` line, the build is reported as successful.** For
DriveUnion #44 the daemon sent it as an error message and `DescribesBuildFailure` caught it: *"Build of
harbora/driveunion:build-44 failed at Step 19/23"*. For Loomi #12 the identical error arrived as an
ordinary progress line (log sequence 67), nothing flagged it, the pipeline went on to *"Starting
container …-loomi-12"*, and failed with `No such image: harbora/loomi:build-12`.

That is the exact case `BuildImageFromTarAsync`'s own doc comment says it closes — *"the failure then
only surfaces two steps later, as a confusing 'No such image'"* — still open for this message shape.

## Recommended next step

- After a build returns, **confirm the tag exists** before anything tries to run it; if it does not,
  fail there with the last build lines. Closes defect 2 whatever shape the daemon's error takes, rather
  than chasing message formats.
- On a cache-restore failure, **retry once without `--cache-from`**. Keeps the feature the owner wants
  to keep, and turns defect 1 from a failed deploy into a slower one.
