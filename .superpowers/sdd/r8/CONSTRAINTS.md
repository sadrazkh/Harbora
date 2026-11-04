# Constraints — every agent in round eight (Launch: code to running app, with no AI)

Read the design first: `docs/superpowers/specs/2026-11-04-launch-design.md`.
The shared contracts are already committed — use them exactly, do not redefine them:
`src/Harbora.Shared/Inspection/SourceInspection.cs`, `src/Harbora.Domain/Launch/AppLaunch.cs`,
`src/Harbora.Infrastructure/Launch/LaunchContracts.cs`,
`src/Harbora.Infrastructure/Verification/AppVerificationContracts.cs`,
`AppManagedService.EnvAliasesJson` + `AttachEnvAlias`, `JobKind.LaunchInspect`, and the migration
`*_AppLaunches`. **Add no further migration**: if you truly need a schema change, stop and say so in your
report. If a contract is wrong for your work, say so in your report — do not quietly change its shape;
other agents are coding against it in parallel.

**No AI.** Nothing in this round calls a model, or depends on the assistant or the `assistant` feature.

**Before building anything, check it is not already there.** Twelve times in this codebase a capability
assumed missing already existed. Search for what a thing does, not for what you would have called it.

## Your worktree

Your brief names one absolute path. Every Bash command starts with `cd <that path> &&`; every Read, Edit and
Write uses an absolute path under it. The owner's checkout (`.../Harbora`) and the other agents' worktrees
are off limits. Your **first** command prints `git log --oneline -1`: if it is not the commit your brief
names, stop and report it, do not start.

## Three ways a build here lies about itself

1. **MSBuild node reuse across worktrees.** Always `export MSBUILDDISABLENODEREUSE=1` and pass
   `-p:UseSharedCompilation=false`.
2. **The Vite/dotnet race.** `npm run build` while `dotnet build` or `dotnet test` runs in the same worktree
   makes `Harbora.Web` fail and then **`Harbora.Tests` does not run at all** while small green `Passed!`
   lines still print and the exit code is 0. Run them one after another, never together.
3. **The unreachable NuGet feed.** A plain restore fails `Harbora.NodeAgent` with `NU1301`. In a fresh worktree:
   `dotnet restore Harbora.slnx --ignore-failed-sources` once, then build with `--no-restore`.

Capture the exit code before piping, confirm a `Harbora.Tests.dll` line, and name logs after your branch:

```
export MSBUILDDISABLENODEREUSE=1
LOG=/tmp/test-$(git branch --show-current | tr '/' '-').log
dotnet build Harbora.slnx --no-restore --nologo -v q -p:UseSharedCompilation=false > "$LOG.build" 2>&1; echo "BUILD_EXIT=$?"
dotnet test Harbora.slnx --no-build --nologo -v q > "$LOG" 2>&1; echo "EXIT=$?"
grep -E "^Passed!|^Failed!|\[FAIL\]" "$LOG"
grep -c "Harbora.Tests.dll" "$LOG"   # must not be 0
```

**Several agents share this machine.** While iterating, run only your own tests
(`dotnet test tests/Harbora.Tests --no-build --filter "FullyQualifiedName~YourClass"`). Run the full suite
once, at the end, before your final commit.

Baseline: **7429** in `Harbora.Tests.dll`, NodeAgent 500 (+17 skipped), NodeIngress 15. It must not drop.
Zero new compiler warnings. Front-end node tests: `node --test src/Harbora.Web/Scripts/tests/*.test.mjs`
(baseline **500**). A fresh worktree has no `node_modules`: `cd src/Harbora.Web && npm ci` once.

## Commits

Owner's name alone. No `Co-Authored-By` trailer, no other author, no other email:

```
git -c user.name="sadra zadeh khameneh" -c user.email="1986.aandrii@gmail.com" commit -m "..."
```

A sentence or two saying what changed and why — not a bullet list. **Commit as soon as your first test run is
green, then keep refining.** Never leave finished work uncommitted. Check `git branch --show-current` before
committing. Never push.

## Code style

Match the surrounding code: naming, idioms, comment density (this codebase explains *why* in prose comments;
do the same where the why is not obvious, and no more). Persian beside English wherever a person sees text
(`IsFa ? … : …`, `MessageFa`, `TitleFa`). Persian is the default UI and RTL: use logical classes (`ms-`, `me-`,
`ps-`, `pe-`, `text-start`). The census tests (`IconCoverageTests`, `NavigationMapTests`,
`ServerStringsLocalizationTests`, `LearningCensusTests`, `DocumentationDriftTests`, …) are the rules: if one
fails, it is telling you the convention, so follow it rather than changing the test.

Background work has no session, and a workspace-filtered read there silently returns nothing and reports
success: use `IgnoreQueryFilters()` deliberately on every sessionless path, and apply the workspace yourself.

## This machine

No Docker. No Traefik. No Postgres credentials. Anything that needs one of those is written for the CI lane
(`tests/Harbora.Postgres.Tests`) and **reported as unverified locally** — never described as passing.

## Honesty

The defining defect of this codebase is a surface that reports success for work it never did. Do not write
one. In your report say exactly what you ran and what it printed; if something is unverified, say so.

## Out of scope, always

Security review, vulnerability hunting, attack scenarios. Payment gateways. Iranian-market features.

## When you finish

Write `.superpowers/sdd/r8/<task>-report.md` (what was built, files, the exact runs and their numbers, what is
unverified, and any contract you think is wrong) and commit it with `git add -f`. Then reply with the commit
range and a summary of a few lines.
