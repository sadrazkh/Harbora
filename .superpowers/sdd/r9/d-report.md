# Round nine, agent D: report

Branch `r9/d`, worktree `E:/Cash.Net/source/repos/sadrazkh/Harbora-r9-d`, based on `e84a288`.

| Commit | What |
|---|---|
| `01f5d5b` | (1) A scheduled run and "Run now" are handed what a deployed container of the same app is handed |
| `2a501e2` | (2) Settled `Jobs` rows older than `Retention:JobDays` (30) are deleted, in batches |
| `a7d8cef` | (3) A build's directory is removed when its deployment ends; leftovers are swept |
| (this file) | The report |

## 1. A scheduled run gets what a deployed container gets

**Built.**
- `src/Harbora.Infrastructure/Apps/EffectiveEnvironmentBuilder.cs`: two new members beside `Compute`.
  `IncludeEnvironmentSources(this IQueryable<App>)` is the one include list for everything `Compute`
  reads (variables, config groups and their entries, buckets, email providers, attached databases with
  their logical database and read replicas, error trackers). `Decrypted(app, protector, endpoint)` is
  `Compute` plus the decrypt step, with the same "undecryptable becomes empty" rule.
- `src/Harbora.Infrastructure/Deployments/DeploymentPipeline.cs` `BuildEnv` now calls `Decrypted`
  instead of its own `ToDictionary` (three lines; behaviour unchanged, the function-host secret and
  report URL are still added after it).
- `src/Harbora.Infrastructure/Deployments/CronJobRunner.cs`: the environment is built by a new private
  `EnvironmentForAsync(appId)`, which loads the app by id through `IncludeEnvironmentSources` (unfiltered,
  no tracking) and calls `Decrypted`. Both entry points therefore load the same graph by construction:
  `RunAsync(Guid)` (queue, "Run now") no longer includes anything itself, and `CronRunner.TickAsync`
  no longer includes `EnvironmentVariables` (so the per-minute tick reads one row per job and the graph
  is loaded only for a job that is due). Both reads now use `IgnoreQueryFilters()`. New constructor
  dependency `IOptions<ObjectStorageOptions>` (needed for a bucket's customer endpoint).
- No `PORT`: a scheduled job never has public traffic, which is the pipeline's own rule.

**Tests.** `tests/Harbora.Tests/CronJobEnvironmentTests.cs` (3), against `FakeDockerEngine`'s
`RunRequests` (real pipeline) and `OneOffRequests` (real runner) for the same app with every attachment
kind attached: a deployed Worker and Run now are equal key for key; a deployed Web container and Run
now differ by `PORT` only; the scheduler tick and Run now hand the job the same environment. Mutation
check: with the runner's include list cut back to `EnvironmentVariables`, all three failed; restored,
rebuilt, all passed.

**Other one-off runs.** The only other one-off container of an app's own image is the release task
(`DeploymentPipeline.RunReleaseTaskAsync`), and it is already handed `BuildEnv` + `PORT`. Every other
`RunOneOffAsync` caller runs a platform helper image (backups, PITR, bucket client, grants). No other
gap of the same shape among one-off runs.

## 2. Finished jobs are not kept for ever

**Built.**
- `RetentionOptions.JobDays` (default 30; 0 keeps everything), `RetentionTables.Jobs`,
  `RetentionRule.JobsToDelete(cutoff)` and `RetentionRule.SettledJobStatuses`.
- The rule: status Succeeded, Failed or Cancelled, **and** `CreatedAt < cutoff`, **and**
  (`FinishedAt` null or `< cutoff`). Pending and Running are never candidates.
- `DataRetentionSweeper.SweepJobsAsync`: one status at a time, `ORDER BY CreatedAt LIMIT 10000`
  (`DefaultJobDeleteBatchSize`), repeated until a batch comes back short; each batch is its own
  statement and commits on its own. Wired into the existing sweep after the notification tables, with
  the same keep-forever logging and per-table failure guard (the guard was factored into
  `SweepCountedAsync` and `AgedCutoff` local functions so the batched table could use it).
- The SQL Npgsql generates (read in a test, see below), per status:
  `DELETE FROM "Jobs" AS j WHERE j."Id" IN (SELECT j0."Id" FROM "Jobs" AS j0 WHERE (j0."Status" = 2 OR
  j0."Status" = 3 OR j0."Status" = 4) AND j0."CreatedAt" < @cutoff AND (j0."FinishedAt" IS NULL OR
  j0."FinishedAt" < @cutoff1) AND j0."Status" = @status ORDER BY j0."CreatedAt" LIMIT @p)`.
- `appsettings.json` Retention comments: `JobDays 30` in `_comment_defaults`, the never-deleted list
  names "a job still Pending or Running", `_comment_when` says the job queue is batched.
- `/activity`: the subtitle said "Every background job this workspace has queued", which the sweep
  makes untrue. It now says, in both languages, that a finished job stays for N days (N from
  `Retention:JobDays`; the old sentence when retention is off). `ActivityController`,
  `ActivityPageViewModel.SettledKeptForDays`, `Views/Activity/Index.cshtml`.
- `deploy/RUNBOOK.md`: a paragraph on job retention, including that PostgreSQL does not return the
  freed space to the disk without `VACUUM (FULL) "Jobs"`, which locks the table while it runs.

**Who reads old jobs (checked before settling on 30).** Every reader asks for Pending or Running rows
only: `JobClaimQuery`/`JobWorker` claim, `QueuePosition` (both its callers), `DatabaseJobQueue.RequestCancellationAsync`,
`BillingScheduler` (both places), `BillingRunsController`, `DeploymentReconciler` (three places),
`JobReconciler`, `DeploymentPipeline.NoFreeWorkerNoteAsync`. The one reader of settled rows is
`/activity`, which only shows them; its Cancel action refuses a settled row anyway. Launch only
enqueues and cancels.

**Tests.** `RetentionRuleTests` (+4 facts/theories: settled old go and new stay per status; Pending and
Running never go; a job queued before the cutoff and finished after it stays; the rule, `SettledJobStatuses`
and `Job.IsTerminal` agree for every enum value). `DataRetentionSweeperTests` (fixture seeds a job on
each side; counts updated 16→17, 15→16; new: live jobs survive, batching with batch size 2 removes
2,2,1,2,1,1 rows per save and leaves the recent and pending rows, `JobDays = 0` keeps all and says so).
`JobRetentionSqlTests` (3): the real Npgsql provider builds the statements with an interceptor in place
of the server — three statements, each a `DELETE … LIMIT … ORDER BY "CreatedAt"`, statuses 2/3/4 only,
the batch size reaches the LIMIT. `ActivityHttpTests` (+1): the page names 30 days.
`tests/Harbora.Postgres.Tests/JobRetentionPostgresTests.cs` (1, CI only): 10,050 due rows plus live,
recent and late-finished ones; the real sweep deletes 10,053 and leaves the 5.

## 3. Build directories are removed

**Built.**
- `src/Harbora.Infrastructure/Maintenance/BuildDirectories.cs`: `BuildDirectories.For` (the path),
  `Find` (every `{slug}/{number}` directory under the work root; skips any top-level name starting
  with `_` or `.`, any child that is not a positive number, and anything that is a link), and `Remove`
  (resolves against the root with `PathGuard`, refuses the root itself and links, counts bytes with
  links skipped, clears read-only only on Windows, removes directory links inside the tree as links,
  then deletes). `BuildDirectorySweep`: lists the disk first, then reads unsettled deployments
  (`DeploymentStateMachine.Unsettled`: Queued, Building, Pushing, Deploying, HealthChecking,
  PendingApproval) joined to their app's slug, unfiltered, and removes every directory not owned by
  one; then removes an app folder left empty (non-recursive delete).
- `BuildDirectorySweeper` (hosted service): first pass ten minutes after start, then every 24 hours,
  one log line per run starting `Build directory sweep:`, never throws into the host. Registered in
  `DependencyInjection.cs` next to `DanglingImageSweeper`.
- `DeploymentPipeline`: the four `Path.Combine(_opt.WorkDir, app.Slug, deployment.Number…)` sites use
  `BuildDirectoryOf`, and a `finally` on `ExecuteAsync`'s main try calls `RemoveBuildDirectory`
  (warning on failure, never changes the deployment).
- "Clean up disk" (`MonitoringController.Cleanup`) runs the same sweep and adds a clause to its result
  sentence in both languages, plus two audit metadata fields. The sweep is taken with `[FromServices]`
  so the controller's constructor (built by hand in `MonitoringControllerBackupStalenessTests`) is
  unchanged.
- `deploy/RUNBOOK.md`: a paragraph after the dangling-image section.

**What reads a build directory later (checked).** Nothing, for any source: rollback re-releases an
image (ADR-006); `BuildCache` names an earlier image; `ComposeFile` refuses host bind mounts and only
records `env_file` as accepted, so a running stack mounts nothing from the directory; an uploaded
archive is a file in `_uploads` the build deletes after unpacking; Launch inspects in `_launch/{id}`
and deletes that itself, and hands its archive to the deployment, which is the `_uploads` case. Every
read of the directory in the pipeline is inside the build methods.

**Tests.** `BuildDirectoryTests` (8): a successful deployment's directory existed during the build
and is gone after; a failed build's is gone; the sweep removes a finished deployment's and a deleted
app's directories (and the empty app folder) and keeps a Building and a PendingApproval one, with the
byte count; `_uploads`, `_launch`, a non-number folder and a root file are untouched; a run with
nothing to do logs one line; a link inside a checkout is removed and the read-only file it points at
is untouched and still read-only; a read-only `.git` object does not stop the removal; nothing outside
the root, and not the root, can be removed. `MonitoringBuildDirectoryHttpTests` (1): Clean up disk
removes a leftover and says so. `FakeDockerEngine` gained an `OnBuild` hook. Mutation check: with the
`finally` call removed, the two pipeline tests failed; restored, rebuilt, passed.

## Decisions the owner might have made differently

1. **Age of a job is "queued and finished before the cutoff"**, not finish alone. `CreatedAt` is what
   the existing `(Status, CreatedAt)` index orders by; the `FinishedAt` term keeps a job that waited
   weeks and finished last night.
2. **30 days, every kind, billing included.** `HarboraDbContext`'s comment on the billing index says
   "Completed jobs stay as the audit trail", but no code reads a settled `BillingHour` job; the
   `BillingRuns` row (left alone, as the brief says) is the record of what an hour did. If that comment
   is a policy, `BillingHour` should be excluded from the rule.
3. **No new index, and the brief's index fact was incomplete.** `HarboraDbContext.cs:1381-1383` also
   has `(Status, CreatedAt)` (`IX_Jobs_Status_CreatedAt`, migration `20260728133857_DurableJobQueue`),
   which the brief's list at `:1385-1399` omits. The batch is shaped to use it: one status at a time,
   ordered by `CreatedAt`. A plain scan was not needed, so no migration was proposed.
4. **Batch of 10,000, no cap per night.** The first pass on production (roughly three million of the
   4.2 million rows would be past 30 days, if they are spread evenly from 2026-08-09) is roughly 300
   statements in one sweep. A cap would spread it over nights; I judged short committed statements enough.
5. **The batch size is a constant, not a setting.** Only `JobDays` is configurable.
6. **Cron env loaded by id inside the runner**, rather than the scheduler including the whole graph for
   every cron job every minute. It is one more query per run.
7. **BuildEnv shares `Decrypted`.** A three-line change in the pipeline so the decrypt step cannot drift
   between the two; the include list in `DeploymentPipeline.ExecuteAsync` itself was left as it is (it
   loads more than the environment, and is the start of the method other agents may touch).
8. **Build directories are removed after failure too**, so a failed build's checkout is not kept for
   inspection.
9. **The build-directory sweep has no setting and is its own hosted service**, not part of the
   dangling-image sweeper, so `Runtime__DanglingImageSweepHours = 0` does not also stop it.
10. **`_uploads` is not swept.** An archive whose deployment never unpacked it (refused by billing,
    cancelled while queued) stays. Launch owns part of that folder, and I left it to C's area.
11. **The Activity page sentence and the RUNBOOK paragraphs** are my wording; the RUNBOOK names
    `Retention:JobDays` and points at `appsettings.json` rather than an env var, because no compose
    file passes `Retention__*` through and `DocumentationDriftTests` would (rightly) refuse a RUNBOOK
    variable that reaches no container.

## Runs on the final code commit (`a7d8cef`)

Run from the worktree on `a7d8cef`, exactly as `CONSTRAINTS.md` gives it (`MSBUILDDISABLENODEREUSE=1`,
`-p:UseSharedCompilation=false`, restore earlier with `--ignore-failed-sources`, no npm build anywhere):

```
dotnet build Harbora.slnx --no-restore --nologo -v q -p:UseSharedCompilation=false   -> BUILD_EXIT=0
  compiler warnings (CS/EF): 0; the only warnings are the two NU1903 SSH.NET advisories the base already had
dotnet test Harbora.slnx --no-build --nologo -v q --blame-hang --blame-hang-timeout 6m --blame-hang-dump-type none   -> EXIT=0
Skipped! - Failed: 0, Passed:    0, Skipped: 118, Total:  118 - Harbora.Postgres.Tests.dll
Passed!  - Failed: 0, Passed:  500, Skipped:  17, Total:  517 - Harbora.NodeAgent.Tests.dll
Passed!  - Failed: 0, Passed:   15, Skipped:   0, Total:   15 - Harbora.NodeIngress.Tests.dll
Passed!  - Failed: 0, Passed: 9483, Skipped:   0, Total: 9483 - Harbora.Tests.dll (3 m 39 s)
grep -c "Harbora.Tests.dll" -> 2
```

Against the baseline: `Harbora.Tests.dll` 9457 → 9483 (+26: cron environment 3, retention rule 7 cases,
sweeper 3, SQL shape 3, activity 1, build directories 8, Clean up disk 1); Postgres skipped 117 → 118
(the new `JobRetentionPostgresTests`); NodeAgent and NodeIngress unchanged. Front-end tests were not run:
nothing under `Scripts/` changed. The report commit adds only this file.

## Unverified

- **Anything against a real PostgreSQL.** `JobRetentionPostgresTests` is skipped here (no Docker); it
  runs only in CI. The statement shape is verified locally; how PostgreSQL plans it on 4.2 million rows
  (that each batch is an index range scan on `IX_Jobs_Status_CreatedAt`, how long the first pass takes,
  its WAL volume) is reasoned, not measured.
- **The Linux link path.** On Linux a directory symlink inside a checkout is removed with `File.Delete`
  (unlink); here on Windows the test exercised the Windows path (a junction or symlink, removed with
  `RemoveDirectory`). CI on Linux runs the same test.
- **A real Docker build** reading its context before the directory is removed: proven only against
  `FakeDockerEngine`.
- **Production effect**: nothing was deployed.

## Touched outside my area

- `src/Harbora.Infrastructure/Apps/EffectiveEnvironmentBuilder.cs`: two members appended.
- `src/Harbora.Infrastructure/Deployments/DeploymentPipeline.cs`: `BuildEnv` (3 lines), the four
  build-directory computations, a `finally` after `ExecuteAsync`'s last `catch` plus two small private
  methods right after `ExecuteAsync`. Not in the success path after cutover (E's).
- `src/Harbora.Infrastructure/DependencyInjection.cs`: two registrations after `DanglingImageSweeper`.
- `src/Harbora.Web/Controllers/ActivityController.cs`, `ViewModels/ViewModels.cs` (`ActivityPageViewModel`),
  `Views/Activity/Index.cshtml`: the retention sentence.
- `src/Harbora.Web/Controllers/MonitoringController.cs`: Clean up disk runs the build-directory sweep.
- `deploy/RUNBOOK.md`: two paragraphs, added, nothing reflowed.
- Tests: `Fakes/FakeDockerEngine.cs` (`OnBuild` hook), constructor updates in
  `Billing/BillingGateTests.cs` and `DeadlineSettlementTests.cs`, `Http/ActivityHttpTests.cs` (+1).

## Defects found and left

1. `harbora env pull` (`ApiV1Controller.Env`, `src/Harbora.Web/Controllers/Api/ApiV1Controller.cs:225-237`)
   does not include `ErrorTrackingProviders`, so it never returns `SENTRY_DSN` although a deploy injects
   it. The same shape of defect as item 1; replacing its include list with `.IncludeEnvironmentSources()`
   fixes it.
2. A scheduled run mounts none of the app's volumes (`Binds: []`) and has no memory or CPU limit, unlike
   a deployed container. Not environment, so not changed.
3. Why the `Jobs` table grows by about 48,000 rows a day on production (4.2 million between 2026-08-09
   and 2026-11-05) was not looked into; retention bounds it at about 1.4 million at that rate.
4. `DataRetentionSweeper`'s class comment still says "Nine tables"; it sweeps seventeen.
5. `DiskUsageReport` (the disk report page) does not count build directories, so it cannot say what the
   sweep would free.
