# B report: a new database is backed up, is never stuck "being set up", and imports as much as it says

Branch `r9/b`, based on `e84a288`. Three items, three commits: `3eba238` (daily backup), `20bf934`
(stranded Provisioning), `39f5c90` (import size). No EF migration, no new `JobKind`, nothing pushed.

## Runs on the final commit (`39f5c90`)

```
dotnet build Harbora.slnx --no-restore --nologo -v q -p:UseSharedCompilation=false   -> BUILD_EXIT=0, 2 Warning(s), 0 Error(s)
dotnet test Harbora.slnx --no-build --nologo -v q --blame-hang --blame-hang-timeout 6m --blame-hang-dump-type none   -> EXIT=0
  Harbora.Tests.dll        Passed 9513, Failed 0, Skipped 0   (baseline 9457; +56, all mine)
  Harbora.NodeAgent.Tests  Passed 500, Skipped 17
  Harbora.NodeIngress.Tests Passed 15
  Harbora.Postgres.Tests   Skipped 117
  grep -c "Harbora.Tests.dll" -> 2
node --test src/Harbora.Web/Scripts/tests/*.test.mjs   -> 537 pass, 0 fail (baseline 533; +4, mine)
cd src/Harbora.Web && npm ci && npm run build          -> exit 0; the built main bundle contains data-upload-limit
```

The two warnings are the baseline's `NU1903` on `SSH.NET` in `Harbora.Postgres.Tests`; no new compiler
warning. `node_modules` and `wwwroot/build` were deleted afterwards to give the disk back.

I also ran one mutation by hand for item 3: with `[RequestFormLimits]` removed from `Import`, the new HTTP
test fails and the page shows the size sentence instead of the action's own refusal; restored, rebuilt,
passes. That is what shows the test really detects the 128 MB default.

## 1. A database a person creates gets a daily backup (`3eba238`)

**Built.** `Services/AutomaticDatabaseBackup.cs` (new). `DatabaseProvisioningService.CreateAsync` now runs
the existing creation (renamed `CreateCoreAsync`, unchanged) and, once it has committed, audited and queued
its provision, calls `AutomaticDatabaseBackup.ScheduleAsync`. The result rides on a new optional
`DatabaseCreateResult.Backup` (`DatabaseBackupSetup`: `Scheduled`, `ScheduleId`, `FirstRunAt`, `Message`,
`MessageFa`). The form, Launch and the assistant all call `CreateAsync`, so all three get it.

- **Never fails or slows the creation.** The schedule is made after the creation's own transaction is
  disposed, in a transaction of its own under the same `AcquireCreationLockAsync`, with
  `CancellationToken.None`. Every refusal and every exception becomes a `DatabaseBackupSetup` with a sentence;
  anything it added to the change tracker is detached on failure, because Launch saves its own row with
  the same context straight after (tested with `BrittleContext`).
- **Plan.** `CanAddGovernedResourcesAsync(BackupSchedules: 1)` and `CanUseBackupRetentionAsync(7)`, the
  manual path's checks. A refusal gives no schedule and the plan's own sentence (both languages).
- **Only where a backup can run.** Engines with a `DatabaseDumpPlan` (PostgreSQL, MySQL, MariaDB, MongoDB).
  Not a read replica. Not a server the backup engine refuses. That is decided the way `BackupEngine` decides
  it: the factory's resolved engine is its `Local` instance or a `NodeWorkloadEngine`. A server that throws
  on resolve gets no schedule either.
- **First run.** `NextRunAt = now + 24h`. `BackupScheduler` treats a null `NextRunAt` as due on its next
  five-minute tick.
- **Retried launch.** A database that already has any Database/Service schedule gets no second one.
  `AdoptServicesAsync` never calls `CreateAsync` for an adopted database anyway.
- **Destination.** The workspace's default destination, or its first. If it has none, the same `Local`
  (`IsDefault`, no path, so staging dir) that `BackupsController.EnsureDefaultDestinationAsync` makes on
  the first visit.
- **Removal.** `ManagedServiceEngine.RemoveAsync` (the only path that deletes a `ManagedService` row) now
  removes every `BackupSchedule` whose `TargetRef` is the database, in the same save. Backup rows stay.
- **Where it shows.** The schedule appears in the Backups page's schedule list and in the database page's
  "Every 24 hours · next …". It is edited or deleted there as before. The form sets `TempData["Message"]` to
  the sentence. The assistant's `create_database` appends it to its summary and adds `dailyBackup` to the
  result JSON. The database page's empty state no longer says "Set up backups" when a schedule exists.
- **Audit.** `backup.schedule_created`, target type `backup_schedule`, metadata `automatic: true`. It is
  aimed at the schedule, so the database's own trail stays `database.created` alone; an HTTP test asserts
  exactly that.
- **Tests.** `AutomaticDatabaseBackupTests` (24) and one HTTP test in `DatabaseCreateHttpTests`: through the
  booted panel's DI, a database made on the form gets the schedule and its page shows the sentence.
- **Docs.** README.md and README.fa.md (one new bullet each), `docs/tutorial/04` (new subsection),
  `docs/tutorial/07` (one note).

## 2. A database left "being set up" by a restart (`20bf934`)

**Built.** `Services/StrandedProvisionReconciler.cs` (new): the pure `StrandedProvision.Decide` and
`InReadersLanguage`, and `StrandedProvisionReconciler`, a `BackgroundService` whose overridden `StartAsync`
runs the startup pass to completion before calling `base.StartAsync`. It is registered after
`AgentPlanStartupReconciler`, above `JobStartupGateOpener`; a test asserts it sits after `JobReconciler`
and before the gate. Every read is `IgnoreQueryFilters`.

**Is `ProvisionAsync` safe twice?** Read in the code, yes, for an ordinary database. Networks and volumes are
ensured, never created blind. The old container is removed by name. The new one starts on the same data
volume, which this path never removes. The Rebuild button, the worker's resume after a graceful shutdown and
its transient-failure retries already re-run it. The one exception is a replica with `RunningImage == null`:
it wipes the replica volume and runs `pg_basebackup` again.

**Decision table** (database in Provisioning with no Pending/Running `ServiceProvision` job):

| latest provision job | becomes |
|---|---|
| none, or Succeeded | queued again |
| Failed with `JobReconciler.InterruptedByRestart`, ordinary database | queued again |
| the same, and the job before it was also interrupted | Failed: "cut off by a restart twice in a row… Press Retry" |
| the same, replica still seeding (`PrimaryManagedServiceId` set, `RunningImage` null) | Failed: copy interrupted, "Press Retry when that suits you" |
| Cancelled | Failed: "cancelled before it finished. Press Retry" |
| Failed with any other error | Failed: "stopped before it finished: {job error}. Press Retry to try again." |

- **Failing goes through the engine.** It calls `ManagedServiceEngine.FailAsync` (now `internal`), so a
  stranded database gets the row reason, the incident and the notification that any failed provision gets.
- **Without a restart.** A Pending provision cancelled from the activity page is settled Cancelled without
  running. A provision that throws before `ProvisionAsync`'s own catch fails only its job. Both leave the
  same state. The reconciler sweeps every 5 minutes and acts only on a database untouched, and with its last
  job over, for 10 minutes (`Grace`). That window covers Retry and Launch's retry, which mark the row
  Provisioning and queue a moment later.
- **One root cause fixed.** `engineFactory.ResolveAsync` moved inside `ProvisionAsync`'s try, so a revoked
  node now fails the database with its reason instead of escaping.
- **Both languages.** `ManagedService.ErrorMessage` is one column, so the English sentence is stored and the
  database page maps my sentences to Persian through `StrandedProvision.InReadersLanguage`. Everything else
  stored there shows exactly as before.
- **Tests and docs.** `StrandedProvisionReconcilerTests` (17), plus a subsection in `docs/tutorial/04`.
  `JobReconciler`'s message became the public const `InterruptedByRestart`.

## 3. An import as large as the page says (`39f5c90`)

**The cause, confirmed in code and by the mutation run above.** `[ValidateAntiForgeryToken]` reads the whole
form before the action, and that read used the form reader's 128 MB default. The resulting failure became a
bare 400.

**Built.**
- **The limit attributes.** `Import`, `ImportDatabase` and `BackupsController.Upload` now carry
  `[RequestSizeLimit(stated + 1 MB)]`, `[RequestFormLimits(MultipartBodyLengthLimit = stated + 1 MB)]` and the
  new `[RefuseOversizedUpload(stated, action)]`. They live in `Harbora.Web/Infrastructure/UploadLimit.cs`.
  Stated is 2 GB for the imports and 8 GB for the backup upload; the upload's body limit was exactly 8 GB and
  is now 8 GB + 1 MB.
- **The refusal filter.** It is an authorization filter at order 950: after the limit filters (900), before
  antiforgery (1000). A body whose declared length is over the ceiling is refused without being read.
  Otherwise it reads the form itself, which antiforgery was about to do anyway, and the result is cached. A
  limit `InvalidDataException`, or a 413 `BadHttpRequestException`, becomes a redirect to the page the person
  came from, with `UploadLimit.TooLarge` in their language in TempData. It saves TempData itself, because an
  authorization short-circuit skips the filter that would.
- **In the page.** All three forms state "Up to 2 GB / تا ۲ گیگابایت" (8 GB for the backup upload). A new
  `Scripts/uploadLimit.js` (one import and one call in `main.ts`) refuses a file over `data-max-bytes` before
  anything is sent, using the same server-made sentence. The in-action size checks now use the same sentence.
- **Tests.** `UploadLimitTests` (13): the limits agree with the stated size for all three actions, and every
  refusal path is covered. `DatabaseImportSizeHttpTests` (1): a real 129 MB generated body reaches the action.
  It is refused there only for its wrong typed name, so nothing is stored. Plus `upload-limit.test.mjs` (4).
- **Docs.** A subsection in `docs/tutorial/04`.

## Unverified

- **Nothing ran against PostgreSQL, Docker or Traefik.** Under InMemory the advisory lock is a no-op. Not seen
  for real: the schedule's second creation lock in a fresh transaction, the advisory lock contention, and a
  re-provision over a real existing container and volume. The safety argument for re-running
  `ProvisionAsync` is read from the code.
- **No real 2 GB upload was sent.** TestServer does not enforce `MaxRequestBodySize`. So these were only
  unit-tested with fakes: Kestrel's real 413 on an over-long body, and what a browser shows when the server
  answers a large upload early. The same goes for the behaviour behind Traefik.
- **The browser script was not run in a browser.** It ran under node with fakes, and the bundle builds.
- **Hosted-service start order** in the real host is asserted from the registration order only. .NET starts
  hosted services one after another by default.
- **Templates.** Template-made databases get no automatic backup. Templates do not go through `CreateAsync`,
  and I left them out (see decisions).

## Decisions the owner might have made differently

1. **Engines.** Only engines with their own consistent dump get the automatic schedule. Redis, Meilisearch,
   RabbitMQ and NATS do not: their contents are usually rebuilt, not restored. On Starter's two schedules, a
   Redis beside a Postgres would take the second slot. They still get a sentence and can be scheduled by
   hand. Technically they could be backed up by volume copy.
2. **Retention.** A plan whose retention cap is under 7 gets no schedule and a sentence; I did not clamp to the
   cap. That matches "the same checks the manual path uses"; every seeded plan allows 7 or more.
3. **First run a day after creation,** not as soon as the database is Running. That avoids touching
   `BackupScheduler`.
4. **After the commit, not atomic with it.** If the panel dies between the two, the database has no schedule
   and nobody is told. Launch's adoption will not add one later either. I chose isolation, which the brief
   asked for, over consistency.
5. **The destination is the workspace's default even if it is S3 or SFTP,** like the database page's own
   export and back-up-now.
6. **Deleting a database deletes every schedule that targets it,** manual ones too. `BackupSchedule`'s doc
   comment wants a schedule for a deleted *logical* database kept visible; I read that as being about logical
   databases and left them alone.
7. **Templates left out.** That covers the database template and the stack template.
8. **Stranded rules.**
   - Two consecutive restart interruptions stop the automatic retries.
   - An interrupted replica seed is failed for the person rather than re-copied.
   - The sweep runs every 5 minutes with a 10-minute grace.
   - A stranded failure raises the same critical incident and notification as any failed provision.
9. **Persian of a stored reason** comes from matching my English sentences on the database page only. Launch,
   the attention panel and the assistant still show the English, as they do for every engine error.
10. **Persian voice.** My server strings use the singular voice, as the constraints ask, though nearby
    database strings are plural. The tutorial paragraphs keep the tutorial's own plural.
11. **The backup upload (8 GB)** got the same fix and refusal, since the brief pointed at it.

## Touched outside my area

- `Jobs/JobReconciler.cs`: the message became a public const (no behaviour change).
- `Agent/Tools/CreateDatabaseTool.cs`: the summary sentence and the `dailyBackup` JSON field.
- `Infrastructure/DependencyInjection.cs`: two registrations (`AutomaticDatabaseBackup`,
  `StrandedProvisionReconciler`).
- `Scripts/main.ts`: one import and one call.
- `Views/Backups/_QuickActions.cshtml` (upload form) and `Controllers/BackupManagementActions.cs` (`Upload`
  attributes, the const made `internal`).
- `ManagedServiceEngine.cs`, small edits only: schedule cleanup in `RemoveAsync`, `ResolveAsync` moved into the
  try, `FailAsync` made `internal`. Agent F may also edit `ProvisionAsync`; the move is three lines near its
  top.
- `tests/Harbora.Tests/Http/DatabaseCreateHttpTests.cs`: one added test.
- **For agent C (Launch).** The backup sentence is on `DatabaseCreateResult.Backup.Message/MessageFa`; Launch
  ignores it today. `LaunchService.View.cs:186-187` puts `ErrorMessage` in English inside its Persian
  sentence; `StrandedProvision.InReadersLanguage(..., isFa: true)` would give my sentences in Persian there.
  I edited no Launch file.

## Defects found and left

1. **The database-only template path never commits its creation.** In
   `Templates/TemplateDeploymentService.cs:180-188` it saves, queues the provision and returns before
   `quotaReservation.CommitAsync` (which only the stack path reaches, at :295). On PostgreSQL the transaction
   is then disposed uncommitted, so the database, its charge and its job would roll back. Read, not run.
2. **A schedule for a deleted logical database fails every run.** It is kept on purpose, but
   `BackupEngine.ResolveDatabaseCredsAsync` throws for it (around `BackupEngine.cs:1192`), so it fails and
   alerts daily.
3. **On a v1 node, a "database" backup is a quiesced volume snapshot, not a logical dump**
   (`TryBackupNodeAsync`). The automatic schedule inherits that.

## Brief facts that differed

- **Line numbers drifted by a few lines** (`BackupsController.CreateSchedule` is 192-231;
  `RequireCapableHost` is at 1575); the substance held.
- **Not every Provisioning database holds deployments.**
  `DeploymentPipeline.WaitForServicesBesideItAsync` already excludes replicas
  (`PrimaryManagedServiceId == null`), so only a stranded ordinary database made deployments wait.
- **The import page did not state 2 GB.** Only the code did; the page states it now.
