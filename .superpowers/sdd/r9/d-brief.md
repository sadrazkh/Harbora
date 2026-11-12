# D: a scheduled job gets its database, and the panel stops keeping what nobody needs

Read `.superpowers/sdd/r9/CONSTRAINTS.md` first.

Worktree: `E:/Cash.Net/source/repos/sadrazkh/Harbora-r9-d`, branch `r9/d`. Report: `.superpowers/sdd/r9/d-report.md`.

Three separate changes. Commit each on its own.

## 1. A scheduled run gets what a deployed container gets

`src/Harbora.Infrastructure/Deployments/CronJobRunner.cs:44` loads the app with `EnvironmentVariables` only,
and `:148` builds the run's environment from those rows alone. The pipeline builds a container's
environment through `Apps.EffectiveEnvironmentBuilder.Compute` (`Deployments/DeploymentPipeline.cs:1555-1596`),
which adds config groups, attached databases, storage buckets, and email and error-tracking providers.

So a cron job attached to a database never receives `DATABASE_URL`, although its page shows it.

The goal: a scheduled run and "Run now" receive exactly what a deployed container of that app would,
except `PORT`, which only an app that takes traffic gets (`ServicePlan.HasPublicTraffic`). Both entry
points must load the same graph: the queue path at `:44` and the scheduler path (`Deployments/CronRunner.cs`),
which hands over an app it loaded itself. Assert on what `FakeDockerEngine` received, the way
`tests/Harbora.Tests/DeploymentPipelinePortTests.cs` does. If another one-off run has the same gap, say so;
fix it only if it is the same shape.

## 2. Finished jobs are not kept for ever

`Jobs` is in no retention rule: `Maintenance/DataRetentionSweeper.cs` (`RetentionTables`, lines 19-50),
`Maintenance/RetentionOptions.cs`. On production it held 4.2 million rows and 942 MB of a 1085 MB database
on 2026-11-05.

The goal: settled jobs (Succeeded, Failed, Cancelled) older than a configurable number of days, 30 by
default, are deleted by the existing sweeper. What must hold:

- A Pending or Running job is never deleted, whatever its age.
- The first pass on a table that size must not be one enormous statement. Read how the sweeper treats a
  first pass (`appsettings.json`, `Retention._comment_when`) and how `AppLogLines` is swept, and batch if
  that is what it takes.
- No index helps a delete by age today (`HarboraDbContext.cs:1385-1399`: `(TargetId, Status)`,
  `(WorkspaceId, CreatedAt)`, a filtered unique `(Kind, TargetId)`), and this round adds no migration. If a
  plain scan is not good enough, say so in the report rather than add one.
- Check who reads old jobs before settling on 30: the activity page, `Deployments/QueuePosition.cs`,
  `Billing/BillingScheduler.cs` (it looks only for live jobs), the reconcilers.
- The retention tests and the comments in `appsettings.json` state the defaults; they are the convention.

Leave `BillingRuns`, `Deployments` and `AppLaunches` alone: the first is billing, the others are product
decisions nobody has made.

## 3. Build directories are removed

A build works in `{Runtime:WorkDir}/{slug}/{deployment number}` (`DeploymentPipeline.cs:1045`, and the same
shape near lines 1280, 1321 and 1422). The only deletes run before a checkout, and a deployment number is
never reused, so nothing is ever removed. They sit on the persistent volume `harbora_builds`
(`deploy/docker-compose.yml`).

The goal: a build's directory is gone once nothing needs it, after success and after failure, and what is
already there on an old install is cleaned up too. Before deleting, find everything that reads the
directory later, for each kind of source: a Compose stack may read or mount files from it; a rollback; an
uploaded archive under `_uploads`; the build cache (`Deployments/BuildCache.cs`); Launch, which inspects in
the work dir and may retry (`Launch/LaunchService*.cs`). A sweep must never touch a directory that an
in-flight deployment or launch still owns, and never anything outside the work dir
(`src/Harbora.Shared/PathGuard.cs`). `Maintenance/DanglingImageSweeper.cs` and the "Clean up disk" action
are the natural neighbours for it.

If you add an option an operator can set, `deploy/RUNBOOK.md` documents those and
`DocumentationDriftTests` holds the two together.

## Owned and not

Yours: `Deployments/CronJobRunner.cs`, `Deployments/CronRunner.cs`, `Maintenance/*`, the `Retention` section
of `appsettings.json`, the build and checkout methods of `DeploymentPipeline.cs`.

Not yours: the success path of `DeploymentPipeline.cs` after cutover (E is adding a check there),
`Launch/*` (C: if Launch's own directories need a change, make the smallest one and name it),
`Services/*` (B), `deploy/launch-proof.sh` (F).
