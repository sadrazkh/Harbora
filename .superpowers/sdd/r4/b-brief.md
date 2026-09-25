# B — `ImageRetentionCount` cannot be set on the shipped stack

## Why

`Runtime:ImageRetentionCount` (default 5) decides how many past build images survive a deploy — the
real depth of instant rollback, and the main thing that grows the disk. It lives only in
`src/Harbora.Web/appsettings.json`.

`deploy/docker-compose.yml` names the panel's environment variables explicitly and has **no
`env_file`**, so a key added to `deploy/.env` is available for *substitution* and never reaches the
process. The `RUNBOOK` does not mention the setting at all. So on the shipped stack the operator has
no way to change it, and the owner has asked to set it to keep only the latest image per app.

This is **exactly HARBORA-0065**, which was fixed for `Jobs__MaxConcurrency` in commit `0966ac5` and
left the identical hole one setting over. **Read that commit first and follow it exactly** — it settled
the pattern, including why the compose entry is a value-less key and not `${VAR:-5}`.

## Files

- `deploy/docker-compose.yml`
- `deploy/install.sh`
- `deploy/RUNBOOK.md`
- `tests/Harbora.Tests/DocumentationDriftTests.cs` — the existing test that asserts every
  operator-settable option named in the RUNBOOK reaches the container. Once the RUNBOOK documents
  this setting, that test should cover it; confirm it does and that it would have failed before your
  compose change.

## The section name

`HarboraRuntimeOptions` binds to the `Runtime` section (`appsettings.json` line 56; compose already
passes `Runtime__RootDomain`). So the variable is **`Runtime__ImageRetentionCount`**. Double
underscore is how .NET maps a nested key — getting it wrong produces a variable that is set, visible
in `docker inspect`, and silently ignored. Verify the binding path rather than assuming it.

## Requirements

1. The panel service passes `Runtime__ImageRetentionCount` from `.env`, and an **unset** `.env` falls
   through to the code's own default rather than a literal duplicated in the compose file. The
   value-less-key form does this: Compose omits the variable entirely when nothing sets it. (Docker's
   "set environment variables" page says the value-less form reads the shell only; its **precedence**
   page contradicts that and row 11 there is a worked example showing the `.env` value reaching the
   container. The precedence table is right — this was verified against a live daemon for
   `Jobs__MaxConcurrency`.)
2. `install.sh` backfills the key **only when absent**, so a value the operator already chose is never
   overwritten, and every existing line and comment in `.env` survives.
3. The RUNBOOK documents it — including the semantics below, which are not obvious from the name.

## The semantics to document

`keep` counts **rollback targets**, and the active deployment's image is protected *separately* in
`DeploymentPlanning.RetainedImageTags`. Read that method. Therefore:

| value | what survives per app | instant rollback |
|---|---|---|
| `1` | the running image only | **none** — a rollback must rebuild from source |
| `2` | running + one previous | one step |
| `5` (today's default) | running + four previous | four steps |
| `0` | retention off; images accumulate for ever | unlimited, disk grows unbounded |

The RUNBOOK must say that `1` removes instant rollback, because the name reads like "keep 1 backup"
and an operator setting it to save disk will not otherwise learn what it cost until they need a
rollback.

Also note in the RUNBOOK that lowering it does not reclaim anything on its own: the setting applies on
the next deploy, and the **"Clean up disk"** button on the Monitoring page is what applies the new
window to apps that have not deployed since. That is real behaviour of the existing
`DiskCleanupService` — confirm it by reading that file before writing the sentence.

## Scope boundaries

Do not add a per-app retention override. Do not change the shipped default of 5 — making it settable
is the deliverable; choosing a value is the owner's. Do not touch `MonitoringController`, the
Monitoring views, `_Layout.cshtml` or `app.css` — parallel agents own those.

## Tests

- A test asserts `Runtime__ImageRetentionCount` is passed through by the compose file.
- A test asserts `install.sh` backfills it only when absent.
- The existing RUNBOOK drift test covers it. State in your report how many options that test now
  checks and whether any others are still unplumbed.

## Verify

Per `.superpowers/sdd/r4/CONSTRAINTS.md`. Baseline 6224. No Docker here — you cannot bring the stack
up, so the compose assertion must work by parsing the file and the binding, and your report must say
so.
