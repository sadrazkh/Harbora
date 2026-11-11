# E: every deployment is checked after it goes live, and a bad one offers the way back

Read `.superpowers/sdd/r9/CONSTRAINTS.md` first, then `docs/product-audit/19-do-not-change-list.md`.

Worktree: `E:/Cash.Net/source/repos/sadrazkh/Harbora-r9-e`, branch `r9/e`. Report: `.superpowers/sdd/r9/e-report.md`.

## What the code does today

- The checks that tell a working app from one that merely started (every replica still running, restarts,
  each attached database answering on the app's network, the public address, the log tail) live in
  `src/Harbora.Infrastructure/Verification/AppVerifier.cs`. They run for a launch
  (`Launch/LaunchService.Advance.cs:259`) and when someone presses "Check now"
  (`src/Harbora.Web/Controllers/AppsController.Verify.cs:32`). The app page reads the latest result
  (`AppsController.cs:296`). Nothing runs them after an ordinary deployment.
- So a deployment that passes the health gate and then crashes, cannot reach its database, or fills its log
  with errors is green everywhere until a person happens to look.
- Rollback exists and is confirmed on its own page: `AppsController.cs:1370-1402`, `IRollbackPlanner`,
  `Views/Apps/ConfirmRollback.cshtml`.

## The goal

The owner asked that after a bad deploy, going back to the previous version be automatic or one press.

After every deployment that succeeds, the same checks run with nobody asking. When a check fails, the
person is told plainly where they will see it (the app page, the deployment's page, and a notification if
the notification system has a natural place for it), and one press takes them to the existing rollback
confirmation for the last version that was good, or tells them why going back is not possible, which the
planner already knows.

## Decided, with the reasons

Change one of these only with a reason in your report.

- **One press, not automatic.** A rollback re-runs the release task from the old image
  (`Deployments/DeploymentPipeline.cs:520`) and cannot undo a database migration the new version ran. Undoing
  code without undoing data, on the platform's own initiative, can leave an app worse off than the failure
  did. An opt-in switch per app is welcome only if it is small and its page states this risk in words;
  otherwise leave it out and say so.
- **A good deployment is not slowed or failed by this.** The deployment is Succeeded when the pipeline
  says so today. The checks come afterwards and never change a deployment's status. They must not hold a
  deployment worker while they wait; a durable job is the usual shape here (`Domain/Jobs/Job.cs`,
  `Jobs/JobExecutionPolicy.cs`, `IJobHandler`). You are the one agent this round who may add a `JobKind`:
  append a value, never renumber.
- **Failed means offer the way back; could-not-check does not.** Round eight drew this line for launches
  (`Launch/LaunchAdvance.cs`, `VerdictOn`): a Warning or a check that could not run is not a broken app.
  A first deployment has nothing to go back to: say what is wrong and offer nothing.
- **A result accuses only the version it checked.** A check of deployment 12 must not show a warning on
  deployment 13. See what `AppVerification` (`Domain/Launch/AppLaunch.cs:126`) stores. You are also the one
  agent who may add a migration, and only if there is no honest way without it: one migration, generated
  after a full build, never with `--no-build`; `MigrationConsistencyTests` will tell you if it is stale.
- **A launch already checks its own deployment.** Do not run the checks twice for it.
- **Cron and Compose apps.** Decide what "still running" means for each, and skip with a stated reason
  rather than report something untrue.

## What cannot be shown here

There is no Docker on this machine. Tests run over the pipeline harness and fake engine and over the HTTP
harness (`tests/Harbora.Tests/Http/AppVerifyHttpTests.cs`). Remember the frozen clock in the pipeline harness.
Say in the report what only a real server can confirm.

## Owned and not

Yours: `Verification/*`, the part of `DeploymentPipeline.cs` after a deployment succeeds, a new job handler,
`Controllers/AppsController.Verify.cs`, the verification and rollback parts of `AppsController.cs` and of
`Views/Apps/Details.cshtml`, `Views/Apps/Deployments.cshtml`, the deployment's own page.

Not yours: `Launch/*` (C); the build and checkout methods of the pipeline and `CronJobRunner.cs` (D); the
webhook block of `Views/Apps/Details.cshtml` (C); database services (B).
