Last verified: 2026-11-08
Scope: conventions visible in the code, tests, CI and round briefs at commit `b3fd974`, plus working rules from earlier sessions.

# Working guidelines for AI assistants and new developers

Labels: [Verified], [Assumption], [Recommendation]; see [MASTER_CONTEXT.md](MASTER_CONTEXT.md). An item marked [Assumption] was learned on the development machine in an earlier session and was not re-run for this baseline.

## Standing rules from the owner

- [Verified] Commits carry the owner's name alone, with no `Co-Authored-By` trailer: `git -c user.name="sadra zadeh khameneh" -c user.email="1986.aandrii@gmail.com" commit` (`.superpowers/sdd/r8/f1-brief.md`). The `-c` flags matter: the machine's global email is a different address.
- [Assumption] Answer the owner in Persian, including reports and tables.
- [Assumption] Plan before building. After each piece of work, give a table of what shipped, what is left, its size and prerequisites, and let the owner choose.
- [Verified] In the rounds so far, security review, vulnerability hunting, payment gateways and Iranian-market features were out of scope (`.superpowers/sdd/r8/CONSTRAINTS.md`).
- [Assumption] Do not push, merge or deploy unless asked for that specific work. One production server is kept current; a second is frozen on purpose and must not be caught up without asking.
- [Assumption] Do not type passwords into the production panel. A live test through it needs the owner.

## Before changing code

- [Verified] Read `docs/product-audit/19-do-not-change-list.md`. It names 30 behaviours and the tests that guard them.
- [Verified] Comments often record a real incident at the fix site. Treat one as a reason, and check whether it is still true.
- [Verified] Never renumber an enum value that is persisted; append instead (`Domain/Common/Enums.cs`).
- [Verified] Keep the node contract in step: schema, C# and conformance tests change together, additive only within v1.
- [Verified] New migrations are forward-only, one per feature. `MigrationConsistencyTests` catches a model that drifted from its migration.

## Patterns to follow

- [Verified] **Refuse in words.** A refusal has a sentence in both languages (`ServiceRefusal`, `QuotaCheck.Deny`).
- [Verified] **Sessionless code pins the workspace by hand.** In a hosted service or a job, global filters do not protect you and may hide everything; use `IgnoreQueryFilters()` with an explicit `WorkspaceId` predicate.
- [Verified] **`AsNoTracking` when watching a row another job writes.** A tracked read keeps returning what this context first saw.
- [Verified] **A locked control is a courtesy.** Enforce server-side first.
- [Verified] **Unmeasured is not zero.** Use `_Metric.cshtml` and `MetricDisplay`.
- [Verified] **Persian voice in Launch is singular**, the way a person talks (`کن`, not `کنید`). `LaunchPersianVoiceTests` enforces it for that flow.
- [Verified] **Secrets never reach a log or a URL.** Use the redactor on anything stored from a command's output.
- [Verified] **Census tests hold process data to the code.** If you add a document, tab or notification type, expect one to fail until it is registered (`DetailTabCensusTests`, `NotificationTemplateCensusTests`, `BacklogStatusTests`, `DocumentationDriftTests`).

## Traps that cost real time

| Trap | Rule | Label |
|---|---|---|
| A front-end build during a .NET build makes thousands of tests silently not run while the summary looks green | Run them one after another. Confirm the `Harbora.Tests.dll` line appears in the output | Assumption |
| MSBuild node reuse leaks between worktrees and contaminates baselines | Set `MSBUILDDISABLENODEREUSE=1` and pass `-p:UseSharedCompilation=false` | Assumption |
| A piped build hides its own failure | Capture the exit code before piping | Assumption |
| A run with no summary line and no CPU is hung, not slow. `PipelineHarness` has a `FixedClock`, so a loop bounded by `clock.UtcNow` never ends in a test | A full `Harbora.Tests` run takes about 2.5 minutes. Rerun with `--blame-hang --blame-hang-timeout 4m --blame-hang-dump-type none` | Verified |
| Parallel branches can each pass and still hang or fail once merged | The first full run on the merged result is the one that counts | Verified |
| `dotnet ef migrations add --no-build` against a stale assembly captures the old model | Build first | Assumption |
| After a mutation test, a restored file can still run as the mutated build if its timestamp did not move | Touch the file, or rebuild from clean | Assumption |
| A route parameter named `action` receives the action's own name, and the page looks empty rather than broken | Do not name a parameter `action` | Assumption |
| `TempData` turns a GUID-shaped string into a `Guid`, so `as string` is null after a redirect | Read it as `object` | Assumption |
| A plausible digest invented for seed data passes every test and fails in production | Only use a digest read from a registry | Verified (`docs/backup-sync/ARCHITECTURE.md` section 10) |
| Raw panel screenshots hold live secrets | Only `*.annotated.png` belongs in the repo | Verified (`.gitignore`) |
| FluentAssertions prints the expression from the source file, so a stale binary shows the new test text | Confirm the build really ran | Assumption |
| An agent worktree can branch from the repository's root commit | Create the worktree yourself from the intended base | Assumption |
| Windows line endings in this repo break multi-line text patches | Patch with a script that preserves CRLF | Assumption |
| No Docker on the development machine | Container-backed tests are proven only in CI; say so rather than reporting them as passing | Verified (`DockerFactAttribute`, `PostgresFactAttribute`) |

## Verifying a change

| Change | Minimum |
|---|---|
| C# | `dotnet build`, then the affected tests, then the full `Harbora.slnx` run before merging |
| Razor view | The HTTP tests; a view compile error appears at build |
| Island or script | `node --test src/Harbora.Web/Scripts/tests/*.test.mjs` and `npm run build` |
| Shell script | `bash -n` (CI runs this) |
| `deploy/`, migrations, buildpacks | The real launch proof; unit tests cannot show these work |
| Deployment to production | Compare the panel image's build time with the commit time ([ENVIRONMENT_AND_SETUP.md](ENVIRONMENT_AND_SETUP.md)) |

## Documentation

Follow `DOCUMENTATION_RULES.md` at the repository root. Mode B is the default: update only what the change affects, in the same change.
