# Constraints — every agent in round seven (assistant polish: deleting conversations, process display, chat features)

Read the design first: `docs/superpowers/specs/2026-10-11-harbora-assistant-agent-design.md`.
The shared contracts are already on master — use them exactly, do not redefine them:
`src/Harbora.Infrastructure/Agent/{AgentChatContracts,AgentToolContracts,AgentViews,AgentSettingKeys}.cs`,
`src/Harbora.Application/Abstractions/ServiceRefusal.cs`, `PlatformFeatures.Assistant`,
`DeploymentTrigger.Assistant`. If a contract is genuinely wrong for your work, say so in your report —
do not quietly change its shape; other agents are coding against it in parallel.

## Two ways a build here lies about itself

1. **MSBuild node reuse across worktrees.** Agents build in sibling `Harbora-*` directories and reused
   node servers cross-contaminate them with no error. Always `export MSBUILDDISABLENODEREUSE=1` and pass
   `-p:UseSharedCompilation=false`.
2. **The Vite/dotnet race, and any concurrent build in one worktree.** `npm run build` while
   `dotnet test` runs in the same worktree makes `Harbora.Web` fail with `MSB3030`, and then
   **`Harbora.Tests` does not run at all** while small green `Passed!` lines still print and the exit
   code is 0.

So capture the exit code before piping, confirm a `Harbora.Tests.dll` line is present, and write the log
to a name with your branch in it (`/tmp` is shared by every worktree):

```
export MSBUILDDISABLENODEREUSE=1
LOG=/tmp/test-$(git branch --show-current | tr '/' '-').log
dotnet build Harbora.slnx --nologo -v q -p:UseSharedCompilation=false > "$LOG.build" 2>&1; echo "BUILD_EXIT=$?"
dotnet test Harbora.slnx --nologo -v q --no-build > "$LOG" 2>&1; echo "EXIT=$?"
grep -E "^Passed!|^Failed!|\[FAIL\]" "$LOG"
grep -c "Harbora.Tests.dll" "$LOG"   # must not be 0
```

**Five agents share this machine.** While iterating, run only your own tests
(`dotnet test tests/Harbora.Tests --no-build --filter "FullyQualifiedName~YourClass"`). Run the full suite
once, at the end, before your final commit.

Baseline: **7304** in `Harbora.Tests.dll`. It must not drop. Zero new compiler warnings.

## Commits

Owner's name alone. No `Co-Authored-By` trailer, no other author, no other email:

```
git -c user.name="sadra zadeh khameneh" -c user.email="1986.aandrii@gmail.com" commit -m "..."
```

Commit messages: a sentence or two saying what changed and why — not a bullet list.
**Commit as soon as your first test run is green, then keep refining.** Never leave finished work
uncommitted. Work only in your own worktree and branch; never touch another worktree, never push.

## Code style

Match the surrounding code: its naming, its idioms, and its comment density (this codebase explains
*why* in prose comments; do the same where the why is not obvious, and no more). Persian strings beside
English ones wherever the user sees text (`IsFa ? … : …`, `ReasonFa`, `MessageFa`, `t(en, fa)` in Vue).

## This machine

No Docker. No Traefik. No Postgres credentials. Anything that needs one of those is written for the CI
lane (`tests/Harbora.Postgres.Tests`) and **reported as unverified locally** — never described as passing.

## Honesty

The defining defect of this codebase is a surface that reports success for work it never did. Do not
write one. In your report, say exactly what you ran and what it printed; if something is unverified,
say so.

## Out of scope, always

Security review, vulnerability hunting, attack scenarios. Payment gateways. Iranian-market features.

## Round seven additions

- Round seven's contracts are on master in commit `44d38d0` (`AgentViews.cs`, `AgentCoreModels.cs`,
  `AgentSettingKeys.cs`, `AgentToolLabels.cs`). Use them as they are; if one is wrong for your work, say so
  in your report rather than reshaping it — four other agents code against the same shapes.
- **Restore:** a private NuGet feed in this machine's global config is unreachable, and a plain restore
  fails `Harbora.NodeAgent` with `NU1301`. In a fresh worktree run
  `dotnet restore Harbora.slnx --ignore-failed-sources` once, then build with `--no-restore`.
- **Front end:** a fresh worktree has no `node_modules`. `cd src/Harbora.Web && npm ci` once. Node tests:
  `node --test src/Harbora.Web/Scripts/tests/*.test.mjs` (baseline **230** passing). `npm run build` must
  exit 0 — never while `dotnet test` or `dotnet build` runs in the same worktree.
- The design the owner approved is the mockup described in `.superpowers/sdd/r7/look.md`. The pane's own
  type scale is 11/14/18 px (`text-[11px]`, `text-sm`, `text-lg`) and its tokens are the Tailwind ones
  already used in `AssistantPanel.vue` (`text-ink`, `text-ink-muted`, `bg-surface-2`, `border-line`,
  `bg-accent-soft`, `text-ok`, `text-danger`, …). Persian is the default UI and RTL; use logical classes
  (`ms-`, `me-`, `ps-`, `pe-`, `text-start`) so both directions work.
- Report to `.superpowers/sdd/r7/<task>-report.md` and commit it with `git add -f`.
