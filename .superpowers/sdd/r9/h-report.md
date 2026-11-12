# H report: a template database is kept, and a small plan still gets its daily backup

Branch `r9/h`, based on `3882c9f` (the brief commit, directly on agent C's merge `4f50784`). Two fixes, two
commits: `1588b6e` (template database commits) and `1342d87` (backup on a small plan). This report is the
third commit. No EF migration, no new `JobKind`, no interface change, nothing pushed. The full suite was not
run, as the brief asked.

## Runs

```
dotnet restore Harbora.slnx --ignore-failed-sources                                   -> exit 0
dotnet build Harbora.slnx --no-restore --nologo -v q -p:UseSharedCompilation=false   -> BUILD_EXIT=0, 2 Warning(s), 0 Error(s)
```

Both warnings are the baseline `NU1903` on `SSH.NET` in `Harbora.Postgres.Tests`. No compiler warning.

| When | Filter | Result |
|---|---|---|
| New test, before fix 1 | `TemplateDatabaseCommitTests` | Failed 1, Passed 2. `Expected {"lock", "provision", "commit", "dispose"}, but {"lock", "provision", "dispose"}` |
| After fix 1 | the 11 template, quota, billing and assistant-tool classes below | Passed 256, Failed 0 |
| After fix 2 | `AutomaticDatabaseBackupTests` / `DatabaseCreateHttpTests` | Passed 30 / Passed 5 |
| Fix 2 source stashed, rebuilt (old code) | `AutomaticDatabaseBackupTests` | Failed 4, Passed 26: exactly the four small-plan cases |
| Fix 2 restored, rebuilt | `AutomaticDatabaseBackupTests` | Passed 30 |
| After the guide edits | `DocumentationDriftTests`, `LearningLibraryTests`, `LearnHttpTests`, `LaunchChapterTests`, `AssistantGuideTests` | Passed 130 |
| **Final, on `1342d87`** | 16 classes (below), with the blame-hang flags | **Passed 342, Failed 0**, `Harbora.Tests.dll` lines: 2 |
| Final | `Harbora.Postgres.Tests` `TemplateDatabaseCommitPostgresTests` | Skipped 1: "No reachable Docker daemon, so the PostgreSQL lane did not run." |

The final filter covers `TemplateDatabaseCommitTests`, `AutomaticDatabaseBackupTests`, `DatabaseCreateHttpTests`,
`TemplateKindAndRequiredSecretDeploymentTests`, `TemplateVersionDeploymentTests`, `TemplateReferenceTests`,
`TemplateManifestTests`, `TelegramBotTemplateTests`, `KavenegarSmsTemplateTests`, `WorkspaceGovernanceQuotaTests`,
`ResourceCreationBillingTests`, `AppCreateToolsTests`, `AppToolsCommonTests`, `DocumentationDriftTests`,
`LearningLibraryTests` and `LearnHttpTests`. Before each run I checked that the copied `Harbora.Infrastructure.dll`
was newer than the source edit.

## 1. A database made from a template is thrown away on PostgreSQL (`1588b6e`)

**Confirmed by reading. Every line in the brief held.**
- `TemplateDeploymentService.cs:79` takes the lock with `await using`.
- `QuotaService.cs:31-57`: on a relational provider with no current transaction, the lock begins one. It
  returns `DatabaseQuotaReservation` (`:414`), whose `DisposeAsync` disposes the transaction.
- The database-only branch (`:181-189`) did these steps and then returned at `:188` without a commit:
  - `creationBilling.SaveAsync`, which runs `SaveChangesAsync` in every case (`ResourceCreationBilling.cs:82/94/147`);
  - `QueueProvisionAsync`, which runs `DatabaseJobQueue.AddAsync`. That inserts the `Job` and saves it on the
    same scoped context, inside the transaction (`DatabaseJobQueue.cs:39-40`).
- `CommitAsync` is only at `:295`, on the stack path.
- So disposing the lock rolled back:
  - the project and environment that `PrepareAsync` built unsaved;
  - the `ManagedService` and its default logical database;
  - the ledger charge and the wallet debit;
  - the job.
- `SectionControllers.cs:175-202` then redirected to `Databases/Details` with the id of a row that no longer
  existed.
- One more consequence, read and not run: right after that, it writes a `template.deploy` audit entry
  through `AuditLogger` on the same scoped context. With the transaction gone, that save commits on its
  own. So the audit trail would keep a deploy whose project does not exist.

**Changed.** One line and a comment: `await quotaReservation.CommitAsync(ct)` after the provision is queued,
before the return. The project, the database, its first hour and its job now commit together or not at all.

**Tests.**
- `tests/Harbora.Tests/TemplateDatabaseCommitTests.cs` (3, new). A quota service and an engine write into one
  event list. The database template must produce exactly `lock, provision, commit, dispose`; it failed
  before the fix, as shown above. A refused database template produces `lock, dispose` and queues nothing.
  The stack template still produces `lock, commit, provision, dispose`.
- `tests/Harbora.Postgres.Tests/TemplateDatabaseCommitPostgresTests.cs` (1, new, CI only). It builds the real
  `QuotaService`, `ProjectService`, `ResourceCreationBilling` with billing on, and a real `DatabaseJobQueue`
  on one context. It seeds a priced size and a funded wallet, deploys `{"service":"postgres"}`, disposes the
  context, and checks from a second connection:
  - the database, its logical database, the project and the Pending `ServiceProvision` job are there;
  - the charge (`-1500`) is in the ledger and the wallet balance went down by it.

  **Not run here.**

**Other callers of `AcquireCreationLockAsync`, and every return between the lock and its commit.** I read
all 30 call sites:
- `ProjectService` (3)
- `DatabaseProvisioningService`
- `AppProvisioningService`
- `AppConfigurationService` (2)
- `EnvironmentCloner`
- `PreviewEnvironmentService`
- `MailPlatformService` (3)
- `WorkspaceAccountService` (2)
- `DeploymentEngine` (2)
- `AutomaticDatabaseBackup`
- `TemplateDeploymentService`
- the controllers `Users` (2), `Tenants`, `Functions`, `DatabasesController.Replicas`,
  `DatabasesController.LogicalDatabaseBackups`, `Backups` and `AppsController.Addresses`

**No other path returns a success before its commit.** Every other early return is a refusal before
anything is meant to persist, where the rollback is the point. Two things with a different shape, read and
not run, both left as they are:

1. `DeploymentEngine.QueueDeploymentAsync` (`:107`, `:132`, `:163`). `TryCreateDeploymentRowAsync` catches the
   `IX_Deployments_ActiveDeployment` violation inside the creation transaction. Then
   `CoalesceAfterLostRaceAsync` reads in that same transaction. On PostgreSQL a failed statement aborts the
   transaction, so that read would throw `25P02` ("current transaction is aborted") instead of returning the
   winner's id. I did not establish whether the violation can happen at all while the lock is held: two
   queues in one workspace are serialised by the lock, so only a path that inserts a deployment without the
   lock could cause it.
2. `EnvironmentCloner.CloneAsync:223`, the refusal when the original's attach variables cannot be read. It
   returns with the new environment, services and logical databases still tracked as Added. It does not
   call `ChangeTracker.Clear()`, which the billing refusal at `:380` does. Today this is harmless: the only
   caller (`ProjectsController.cs:305-311`) saves nothing after a refusal.

## 2. A small plan still gets an automatic backup (`1342d87`)

**Changed.** All in `Services/AutomaticDatabaseBackup.cs`:
- **How many to keep.** `RetentionAsync` asks `CanUseBackupRetentionAsync` for 7, then 6, and so on down to
  1. It keeps the first count the plan allows. If even 1 is refused, it returns the refusal given for 1.
- **The schedule.** It now uses that count as `RetentionCount`, and so does the audit metadata (`retention`).
- **The sentence.** It names the count in words in both languages. When the count is under seven, it adds
  that this is the plan's limit. Example for a plan that keeps 3:
  "Shop DB is backed up every day and the last three backups are kept, the most the plan allows; the first
  runs in 24 hours. …" / "از «Shop DB» هر روز پشتیبان گرفته می‌شود و سه نسخهٔ آخر نگه داشته می‌شود، یعنی بیشترین
  تعدادی که پلن اجازه می‌دهد؛ …". For one: "only one backup is kept" / "فقط یک نسخه نگه داشته می‌شود".
- **Seven or more.** A plan that allows 7, 30 or unlimited reads exactly as before.
- **None.** A plan that will not keep even one still gets no schedule. Its sentence is the plan's own reason
  plus "Make room or move to a larger plan…", in both languages.

**Tests** (`AutomaticDatabaseBackupTests`, now 30):
- B's "fewer than seven gets none" case became a schedule keeping 3. It asserts both whole sentences and the
  audit `"retention":3`.
- A new theory: a plan allowing 1, 2, 6, 7 or 30 keeps 1, 2, 6, 7 or 7, with the matching words.
- A new fact: a quota that refuses every count gives no schedule, and the plan's reason in both languages.

**Guides.** These said a small plan gets no schedule, or only "seven". Each now says the plan's smaller
number is used and that the message says how many:
- `README.md` and `README.fa.md`: one clause each in B's bullet.
- `docs/tutorial/04-databases-and-brokers.md`: one sentence added. "یا کمتر از هفت نسخه نگه می‌دارد" was
  removed from the list of cases that get none.
- `docs/tutorial/07-operations.md`: the parenthesis.

## Decisions the owner might have made differently

1. **The provision job is queued inside the transaction and committed with it.** This is not "commit, then
   queue", which is what the stack path does at `:295-300`. The brief asked for "the charge, the row and the
   queued job in one transaction". `DeploymentEngine` already enqueues before it commits (`:109-111`,
   `:230-232`). With "commit, then queue", a crash between the two would leave a Provisioning database with
   no job; B's `StrandedProvisionReconciler` would re-queue it only after its 10-minute grace.

   The cost: `JobSignal.Notify()` fires before the commit, so the worker can wake, see nothing, and pick
   the job up on its next poll, at most 5 s later (`JobWorker.PollInterval`). The brief's "as the app path
   does" is only half true: the stack path keeps the charge and the rows in one transaction, but queues
   after it.
2. **The count comes from the check, not from the plan row.** The alternatives:
   - `GetUsageAsync`: heavy, and B's quota fakes throw on it.
   - Reading `Plan.MaxBackupRetentionCount` here: a second copy of `QuotaService`'s workspace-or-default-plan
     resolution, which could disagree with the check that refuses.

   The cost is up to seven small checks, once per new database.
3. **"A plan that allows none" cannot come from `QuotaService` today.** Zero means unlimited there, and a
   suspended workspace is refused earlier by the schedule-count check. So that path is reachable only
   through another `IQuotaService`, and is tested with a fake.
4. **Numbers in words** ("three" / "سه"), matching the "seven" / "هفت" the sentence already used. "The most
   the plan allows" is added only when the plan is the reason.

## Unverified

- **Nothing ran against PostgreSQL.** Two things here are read from the code and EF's transaction behaviour,
  not observed:
  - the rollback before the fix, and the commit after it;
  - the audit row committing on its own.

  The mechanism is proved in CI by the existing
  `QuotaReservationTests.Disposing_an_uncommitted_reservation_rolls_the_resource_back`. The new Postgres
  test is unverified until CI runs it.
- The 5-second pickup delay after a template database commits is read from `JobWorker`, not measured.
- The two `DeploymentEngine` / `EnvironmentCloner` observations above are read, not run.

## Touched outside my area

- **Agent B's work.** I changed B's tests (`AutomaticDatabaseBackupTests.cs`) and B's guide sentences in
  `README.md`, `README.fa.md`, `docs/tutorial/04` and `docs/tutorial/07`. Each edit is minimal, with no
  reflow beyond the edited lines.

## Defects found and left

- **No automatic backup for template databases.** A database made from a template still gets no daily
  backup: templates do not go through `DatabaseProvisioningService.CreateAsync`. This was B's decision 7.
  `AutomaticDatabaseBackup.ScheduleAsync` could be called after the commit in the database-only branch, but
  that is a product choice, not this fix.
- The `DeploymentEngine` lost-race read inside an aborted transaction (above).
