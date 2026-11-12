# Constraints for every agent in round nine (easier, safer deploys)

The owner asked, on 2026-11-11, for what makes deploying a project easy, safe and simple for them and for
their customers, and chose twelve items from a list. Yours is in your brief. Sales, billing and payment are
not part of this round.

**Before building anything, check it is not already there.** Many times in this codebase a capability
assumed missing already existed. Search for what a thing does, not for what you would have called it.

Your brief states facts the coordinator read in the code, with file and line. Lines drift: confirm each one
by reading before you rely on it, and if one is wrong say so in your report.

## Your worktree

Your brief names one absolute path. Every Bash command starts with `cd <that path> &&`; every file tool uses
an absolute path under it. The owner's checkout (`.../Harbora`) and the other agents' worktrees are off
limits: never edit or run git there, and never create a worktree. Your **first** command prints
`git log --oneline -1`: if its subject does not start with `Round nine briefs`, stop and report it.

## Four ways a build here lies about itself

1. **MSBuild node reuse across worktrees.** Always `export MSBUILDDISABLENODEREUSE=1` and pass
   `-p:UseSharedCompilation=false`.
2. **The Vite/dotnet race.** `npm run build` while `dotnet build` or `dotnet test` runs in the same worktree
   makes `Harbora.Web` fail, and then `Harbora.Tests` does not run at all while small green `Passed!` lines
   still print and the exit code is 0. Run them one after another, never together.
3. **The unreachable NuGet feed.** A plain restore fails `Harbora.NodeAgent` with `NU1301`. In a fresh
   worktree: `dotnet restore Harbora.slnx --ignore-failed-sources` once, then build with `--no-restore`.
4. **A hung test looks like a slow one.** The pipeline test harness has a frozen clock, so a production loop
   bounded by `clock.UtcNow` never ends in a test. A full `Harbora.Tests` run takes about three minutes; no
   summary line after ten means a hang. The blame flags below name the test.

Capture the exit code before piping, confirm a `Harbora.Tests.dll` line, and name logs after your branch:

```
export MSBUILDDISABLENODEREUSE=1
LOG=/tmp/test-$(git branch --show-current | tr '/' '-').log
dotnet build Harbora.slnx --no-restore --nologo -v q -p:UseSharedCompilation=false > "$LOG.build" 2>&1; echo "BUILD_EXIT=$?"
dotnet test Harbora.slnx --no-build --nologo -v q --blame-hang --blame-hang-timeout 6m --blame-hang-dump-type none > "$LOG" 2>&1; echo "EXIT=$?"
grep -E "^Passed!|^Failed!|\[FAIL\]|aborted" "$LOG"
grep -c "Harbora.Tests.dll" "$LOG"   # must not be 0
```

**Seven agents share this machine, and its disk has about 25 GB free.** While iterating, run only your own
tests (`dotnet test tests/Harbora.Tests --no-build --filter "FullyQualifiedName~YourClass"`). Run the full
suite once, at the end, on your final commit. If it fails only in timing-sensitive tests you did not touch,
run those tests alone once before investigating: another agent's build may have starved them. Do not make
extra copies of the tree or keep large scratch output.

Baseline on your base commit: **9457** in `Harbora.Tests.dll`, NodeAgent 500 (+17 skipped), NodeIngress 15,
Postgres 117 skipped here. It must not drop. Zero new compiler warnings. Front-end tests are run from the
repository root as `node --test src/Harbora.Web/Scripts/tests/*.test.mjs` (baseline **533**); a fresh worktree
has no `node_modules`, so `cd src/Harbora.Web && npm ci` once, and only if you touch `Scripts/` or need
`npm run build`.

## Parallel work

- **No EF migration and no new `JobKind`.** Agent E is the one exception; its brief says how. If you
  believe you need a schema change, stop and describe it in your report instead.
- **Stay in your area.** Your brief lists what you own and what belongs to another agent. If your item
  cannot work without a change in someone else's file, make the smallest change that does it and name it
  in your report so the merge is easy.
- **A defect outside your item** goes in your report. Fix it only if your item cannot work without it.
- **The project's status documents are the coordinator's.** Do not create or edit files named in capitals
  under `docs/` (`KNOWN_ISSUES_AND_TECH_DEBT.md`, `IMPLEMENTATION_STATUS.md` and the like). User-facing
  guides (`README.md`, `README.fa.md`, `docs/cli-deploy.md`, `docs/tutorial/*`, `deploy/RUNBOOK.md`) are
  yours where your change makes a sentence in them untrue or adds something a person must be told. Add
  your own section; do not reflow text another agent may also be editing.

## Commits

Owner's name alone. No `Co-Authored-By` trailer, no other author, no other email:

```
git -c user.name="sadra zadeh khameneh" -c user.email="1986.aandrii@gmail.com" commit -m "..."
```

A sentence or two saying what changed and why, not a bullet list. Commit as soon as your first test run is
green, then keep refining; never leave finished work uncommitted. Check `git branch --show-current` before
every commit. **Never push** (agent F is the one exception; its brief says exactly what it may push).

## Code style

Match the surrounding code: naming, idioms, comment density. This codebase explains why in prose comments,
often naming the incident at the fix site; do the same where the why is not obvious, and no more. Persian
beside English wherever a person sees text (`isFa ? ... : ...`, `MessageFa`, `TitleFa`), in the singular
voice a person talks in. Persian is the default UI and it is RTL: use logical classes (`ms-`, `me-`, `ps-`,
`pe-`, `text-start`). A refusal is a sentence in both languages, never a bare status code. The census tests
(`IconCoverageTests`, `NavigationMapTests`, `ServerStringsLocalizationTests`, `LearningCensusTests`,
`DocumentationDriftTests`, `LaunchChapterTests` and the rest) are the rules: if one fails it is telling
you the convention, so follow it rather than change the test.

Background work has no session, and a workspace-filtered read there silently returns nothing and reports
success: use `IgnoreQueryFilters()` deliberately on every sessionless path and apply the workspace yourself.
Never renumber a persisted enum value. Read `docs/product-audit/19-do-not-change-list.md` before touching
the pipeline, tenancy, the CLI's deploy precedence or the installer.

A secret (a token, a password, a variable marked secret) never reaches a log line, a URL, an error sentence
or an unencrypted column. `ISecretProtector` is how one is stored.

## This machine

No Docker. No Traefik. No Postgres credentials. Anything that needs one of those is written for CI and
**reported as unverified locally**, never described as passing.

## Honesty

The defining defect of this codebase is a surface that reports success for work it never did. Do not write
one. In your report say exactly what you ran and what it printed; if something is unverified, say so.

## Out of scope, always

Security review, vulnerability hunting, attack scenarios. Billing and payment. Iranian-market features.

## When you finish

Write `.superpowers/sdd/r9/<your letter>-report.md` and commit it with `git add -f`. It says: what was built
and where; each decision you made that the owner might have made differently, with your reason; the exact
runs on your final commit and their numbers; what is unverified; anything in another agent's area you
touched; defects you found and left. Then reply with the commit range and a summary of a few lines.
