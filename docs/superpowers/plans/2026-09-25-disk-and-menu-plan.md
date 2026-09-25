# Disk visibility, retention you can actually set, and the menu that scrolls the page behind it

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development.

**Baseline:** master `b5c7e28`, 6224 `Harbora.Tests` (6743 total across four assemblies).

## Global Constraints

- Commits carry the owner's name alone. No `Co-Authored-By` trailer.
  `git -c user.name="sadra zadeh khameneh" -c user.email="1986.aandrii@gmail.com" commit`
- `MSBUILDDISABLENODEREUSE=1` and `-p:UseSharedCompilation=false` on every build.
- Never run `npm run build` while `dotnet test` is running, and never two builds in one worktree at
  once — both make `Harbora.Web` fail and `Harbora.Tests` silently not run while green lines print.
- Capture the exit code before piping, then **confirm a `Harbora.Tests.dll` line is in the log**.
  The exit code alone has lied three times in this project.
- Zero new compiler warnings.
- No security review. Out of scope.
- No Docker and no live Postgres on this machine. Anything needing either is written for the CI
  lane and reported as unverified locally.

## What already exists — do not rebuild it

Checked before planning, because twenty-seven times in this project a capability assumed missing was
already there. This time most of it is:

- **`DiskCleanupService`** sweeps two things per server: build images of deleted apps, and each app's
  each app's superseded images past the retention window. It reuses `DeploymentPlanning.ImagesToPrune`, so the
  button and the pipeline cannot disagree. It measures freed bytes from the disk's own before/after
  rather than summing image sizes, because layers are shared.
- **A "🧹 Clean up disk" button** already sits beside the low-disk banner on Monitoring, for
  Owner/Admin.
- **`ImageRetentionCount`** (default 5) is applied after every deploy *and* by the cleanup sweep.
- **The per-app Deployments tab** already marks which releases are instant-rollback eligible, from
  the pruner's own rule.
- `ListImagesAsync` / `RemoveImageAsync` exist on every engine, and `ImageInfo` carries `SizeBytes`.

## The three real gaps

### A — Nothing says what is taking the space

Monitoring shows one number: used of total. There is no per-app, per-image or per-volume breakdown,
so "the disk is filling up" cannot be turned into "this is why". The cleanup button is the only
answer offered, and a person cannot tell before pressing it whether it will reclaim 200 MB or 20 GB.

**Build:** a read-only usage report — per server, per app: build-image count and bytes, which tag is
active, which are rollback-eligible, which a cleanup would remove right now; plus orphaned images and
volumes with their sizes; plus a stated total of what is reclaimable.

Must reuse `CleanupPlan.OrphanedBuildImages` and `DeploymentPlanning.ImagesToPrune` so the report and
the cleaner agree by construction. Must carry the same honesty `DiskCleanupResult.FreedBytes` already
states: summed image sizes overstate the real win because layers are shared — say so on the page
rather than printing a number that teaches people to distrust it.

### B — `ImageRetentionCount` cannot be set on the shipped stack

It lives only in `appsettings.json`. `deploy/docker-compose.yml` names the panel's environment
variables explicitly and has no `env_file`, so a key added to `deploy/.env` is available for
substitution and never reaches the process. The `RUNBOOK` does not mention it.

This is exactly HARBORA-0065, which was fixed for `Jobs__MaxConcurrency` and left the identical hole
one setting over. The section binds as `Runtime`, so the variable is `Runtime__ImageRetentionCount`.

**Build:** pass it through as a value-less key (an unset `.env` must fall through to the code's own
default, not to a literal duplicated in compose), backfill it in `install.sh` only when absent, and
document it in the RUNBOOK so the existing drift test that asserts every operator-settable RUNBOOK
option reaches the container starts covering it.

**The semantics to document, because they are not obvious.** `keep` counts *rollback targets*, and the
active deployment's image is protected separately (`RetainedImageTags`). So:

| value | what survives | instant rollback |
|---|---|---|
| 1 | the running image only | **none** |
| 2 | running + one previous | one step |
| 5 (today) | running + four previous | four steps |
| 0 | everything, for ever | unlimited, disk grows unbounded |

### C — Opening the menu on a phone leaves the page behind it scrolling

`_Layout.cshtml`'s slide-over toggle is three lines: it adds `is-open` to the sidebar and unhides the
backdrop. Nothing locks the scroll. So with the menu open, a swipe scrolls the site underneath it —
which is exactly "the menu and the site get mixed up". The sidebar's own `<nav class="overflow-y-auto">`
also chains its scroll to the page once it reaches its end, and there is no Escape to close.

**Build:** lock the page scroll while the slide-over is open and restore it on close (including when
a link inside the sidebar closes it), `overscroll-behavior: contain` on the sidebar nav so reaching
its end does not scroll the page, and Escape to close.

A regression test belongs in `src/Harbora.Web/Scripts/tests/`, where CI already runs one
`node --test` file — the workflow names that single file, so it must become a glob or gain the new
one, otherwise the test exists and never runs.

## Out of scope this round

Per-app retention overrides (the request is a global "each app keeps its latest", not per-app
configuration), and deleting an individual image by hand from the report — the cleanup button plus a
settable retention covers the stated need without a second destructive control.
