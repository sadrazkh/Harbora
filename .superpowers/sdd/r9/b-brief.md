# B: a new database is backed up, is never stuck "being set up", and imports as much as it says

Read `.superpowers/sdd/r9/CONSTRAINTS.md` first.

Worktree: `E:/Cash.Net/source/repos/sadrazkh/Harbora-r9-b`, branch `r9/b`. Report: `.superpowers/sdd/r9/b-report.md`.

Three separate items in one area. Commit each on its own.

## 1. A database a person creates gets a daily backup

What the code does today:

- A backup schedule exists only if someone makes one by hand: `src/Harbora.Web/Controllers/BackupsController.cs:196-232`
  and `DatabasesController.LogicalDatabaseBackups.cs:128`. That manual path checks the plan first
  (`quota.CanAddGovernedResourcesAsync(..., new GovernanceQuotaDelta(BackupSchedules: 1))`,
  `quota.CanUseBackupRetentionAsync`, `Plan.MaxBackupSchedules`) and needs a `BackupDestination` of the
  workspace.
- Every way of creating a database a person asks for goes through
  `src/Harbora.Infrastructure/Services/DatabaseProvisioningService.cs` `CreateAsync` (line 129): the form,
  Launch (`Launch/LaunchService.Confirm.cs:189`) and the assistant's tool (`Agent/Tools/CreateDatabaseTool.cs:139`).
  Check whether templates do too (`Templates/TemplateDeploymentService.cs`).
- `BackupSchedule` defaults to every 24 hours, keep 7 (`src/Harbora.Domain/Backups/Backup.cs:34-56`).

So a database made by Launch has no backup until its owner goes and sets one up, and most never will.

The goal: a new database has a daily backup keeping seven, with nobody doing anything, visible where
schedules are already shown, and removable there. Daily and seven were proposed to the owner and accepted.

What must hold:

- Creating the database never fails and is never slowed because a schedule could not be made. When none
  is made, the reason is a sentence the person can see, not silence.
- The plan's limit on schedules and on retention is respected through the same checks the manual path
  uses. No room in the plan means no schedule and a sentence, never a refusal to create the database.
- Only where a backup can really run. The legacy engine refuses a database on a server other than the
  panel's own host (`Backups/BackupEngine.cs:1555-1566`), and only some engines can be dumped
  (`Backups/DatabaseDumpPlan.cs`). A schedule that can only ever fail is worse than none.
- Not for a read replica (`ManagedService.PrimaryManagedServiceId`).
- The first run must not fire while the database is still being set up and produce a failed backup and an
  alert. Read `Backups/BackupScheduler.cs` for how a schedule with no last run is treated.
- A launch that is retried adopts what an earlier attempt made (`LaunchService.Confirm.cs` `AdoptServicesAsync`):
  no second schedule for the same database.
- Deleting the database leaves no schedule behind; check what the removal path does with schedules today.
- Where the destination comes from when the workspace has none is yours to settle by reading how the
  backup pages get their local destination.

Launch's own pages and services belong to agent C. If the launch timeline should mention the backup, put
the fact on a result type you own and say so in the report; do not edit Launch files.

## 2. A database left "being set up" by a restart

What the code does today:

- Status becomes Provisioning at `DatabaseProvisioningService.cs:151` and `Services/ManagedServiceEngine.cs:92`,
  Running at `:244`, Failed at `:339`.
- After a hard stop, `Jobs/JobReconciler.cs:44-54` marks the interrupted job Failed and leaves the meaning
  to "each target's own reconciler". Deployments have one (`Deployments/DeploymentReconciler.cs`).
  A search found none for managed services.
- A database in that state makes every deployment in its environment wait ten minutes holding a worker
  (`DeploymentPipeline.cs`, `WaitForServicesBesideItAsync`), and a launch gives up at twenty.

The goal: a database is never left in Provisioning with nothing working on it. After a restart it is
either set going again or marked Failed with a sentence, in both languages, that says what happened and
what to press. Which of the two is right depends on whether `ProvisionAsync` is safe to run twice: read
what it does when a container or a volume from the first attempt already exists. Never remove a volume
that may hold data. Seeding a read replica is also Provisioning and takes long by nature
(`ReadReplicaSeedPlan`): it needs its own answer.

It runs with the other startup reconcilers, before the worker starts claiming (`DependencyInjection.cs:129-169`,
`JobStartupGate`). Consider whether the same state can arise with no restart, and cover it if so.

Agent F may add readiness waits in `ManagedServiceEngine`; keep your edits there small.

## 3. An import as large as the page says

`ImportDatabase` (`DatabasesController.LogicalDatabaseBackups.cs:183-185`) and `Import`
(`DatabasesController.SelfServeBackup.cs:142-144`) carry `[RequestSizeLimit(MaxImportBytes + 1 MB)]` and no
`[RequestFormLimits(MultipartBodyLengthLimit = ...)]`, so ASP.NET's form default of 128 MB refuses first.
`AppsController.Upload.cs:22-23` shows both attributes together. Check `BackupManagementActions.cs:92` for
the same gap.

The goal: an import up to the limit the code and the page already state goes through, and one over it is
refused in a sentence in both languages rather than a bare 400 or 413. A truly large upload cannot be
exercised cheaply here: test what can be tested and say what is unverified.

## Owned and not

Yours: `Services/DatabaseProvisioningService.cs`, `Services/ManagedServiceEngine.cs` (small edits), a new
reconciler and its one registration line, `Controllers/DatabasesController*.cs`, `Views/Databases/*`, the
schedule-related parts of the backup pages.

Not yours: `Launch/*` and `Views/Launch/*` (C), `Deployments/DeploymentPipeline.cs` (D and E),
`Maintenance/*` (D), `deploy/*` (F).
