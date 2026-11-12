# H: two small fixes before the release

Read `.superpowers/sdd/r9/CONSTRAINTS.md` first. One thing differs for you: **do not run the full suite**.
Build, run the tests named below and the ones you add, and stop; the coordinator runs the full suite on the
release branch. Your base is `r9/release` (master plus agents A, B, C and D), not the briefs commit, so the
first-command check in the constraints does not apply: your first command should show a merge commit whose
subject starts with `Round nine, agent C`.

Worktree: `E:/Cash.Net/source/repos/sadrazkh/Harbora-r9-h`, branch `r9/h`. Report: `.superpowers/sdd/r9/h-report.md`.

Two fixes, each in its own commit.

## 1. A database made from a template is thrown away on PostgreSQL

Read by the coordinator, not run:

- `src/Harbora.Infrastructure/Templates/TemplateDeploymentService.cs:79` takes the creation lock with
  `await using var quotaReservation = await quota.AcquireCreationLockAsync(...)`.
- On PostgreSQL that lock is a real transaction (`src/Harbora.Infrastructure/Tenancy/QuotaService.cs:31-57`,
  and `DatabaseQuotaReservation` near line 414, whose `DisposeAsync` disposes the transaction).
- The branch for a database-only template saves the charge, queues the provision and returns at about
  line 188. `quotaReservation.CommitAsync(ct)` is only at about line 295, on the path that also creates an
  app. So for a managed-database template the transaction is disposed uncommitted: the project, the
  database row, its charge and its job roll back, and the page is handed ids of rows that do not exist.
- The tests use EF InMemory, where the lock is `NoopQuotaReservation`, so none of them can see this.

Confirm it by reading; if it is wrong, say so and change nothing. If it is right, make that path commit,
keeping the charge, the row and the queued job in one transaction as the app path does. Then look for the
same shape elsewhere: every caller of `AcquireCreationLockAsync` and every return between taking the lock
and committing it. Report each one you find; fix the ones that are plainly the same defect.

A test that fails without the fix: the in-memory database cannot show a rollback, so assert the commit
itself, with a quota service whose reservation records whether it was committed. If a test in
`tests/Harbora.Postgres.Tests` is cheap to add in the manner of the ones there, add it too; it runs only
in CI, so report it as unverified here.

## 2. A small plan still gets an automatic backup

Agent B gave every new database a daily backup keeping seven (`src/Harbora.Infrastructure/Services/AutomaticDatabaseBackup.cs`).
When the plan allows fewer than seven backups to be kept, it makes no schedule at all and says so. The owner
asked for the other choice: make the schedule with the largest number the plan allows, and say that number
in the sentence, in both languages. A plan that allows none still gets no schedule and a sentence.

B's tests for this are beside the class; the case that asserted "no schedule" for a small plan changes
with the behaviour. If a guide B edited states the old behaviour (`README.md`, `README.fa.md`,
`docs/tutorial/04-databases-and-brokers.md`, `docs/tutorial/07-operations.md`), correct the sentence.

## Finishing

Build with the constraints' flags; zero new compiler warnings. Run the test classes you touched or added and
the existing ones for these two files. Commit each fix on its own with the owner's identity, never push.
The report says what you confirmed by reading, what you changed, the exact test runs and their numbers,
other places with the same shape as fix 1, and what is unverified.
