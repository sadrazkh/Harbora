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
