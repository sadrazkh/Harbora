# Constraints — every agent this round

## Two ways a build here lies about itself

1. **MSBuild node reuse across worktrees.** Agents build in sibling `Harbora-*` directories and
   reused node servers cross-contaminate them with no error. Always:
   `export MSBUILDDISABLENODEREUSE=1` and pass `-p:UseSharedCompilation=false`.
2. **The Vite/dotnet race, and any concurrent build.** `npm run build` while `dotnet test` is
   running — or two builds in the same worktree at once — makes `Harbora.Web` fail with `MSB3030`,
   and then **`Harbora.Tests` does not run at all** while three small green `Passed!` lines still
   print and the exit code is 0. This has happened three times in this project.

So: capture the exit code before piping, and then **confirm a `Harbora.Tests.dll` line is present**:

```
export MSBUILDDISABLENODEREUSE=1
dotnet test Harbora.slnx --nologo -v q -p:UseSharedCompilation=false > /tmp/t.log 2>&1; echo "EXIT=$?"
grep -E "^Passed!|^Failed!|\[FAIL\]" /tmp/t.log
grep -c "Harbora.Tests.dll" /tmp/t.log   # must not be 0
```

Baseline: **6224** in `Harbora.Tests.dll`. It must not drop.

## Commits

Owner's name alone. No `Co-Authored-By` trailer, no other author:

```
git -c user.name="sadra zadeh khameneh" -c user.email="1986.aandrii@gmail.com" commit -m "..."
```

**Commit as soon as your first test run is green, then keep refining.** An agent in an earlier round
lost a complete implementation — migration, controller, view, two test files — by leaving everything
uncommitted when it stopped. Committing early is how work survives.

## This machine

No Docker. No Traefik. No Postgres credentials. Anything that needs one of those is written for the
CI lane and **reported as unverified locally** — never described as passing.

## Out of scope, always

Security review, vulnerability hunting, attack scenarios. Payment gateways. Iranian-market features.
Zero new compiler warnings.
