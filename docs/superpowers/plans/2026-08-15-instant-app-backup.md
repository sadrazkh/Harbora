# Instant App Backup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Somebody on an app's page can back it up now — volumes, environment variables and the image reference — and can see, before restoring, whether that backup can still be restored.

**Architecture:** The application backup target already exists and is wired. Task 1 establishes exactly what it captures and settles two decisions the spec names; Task 2 puts the control on the app's page; Task 3 makes a restore honest about what it can and cannot do.

**Tech Stack:** .NET 10, the Backup module under `src/Modules/Backup/`, EF Core, ASP.NET MVC, Razor, xUnit, FluentAssertions.

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-08-15-instant-app-backup-design.md`. Read it before Task 1, including its correction section — this plan has already been wrong once by assuming instead of checking.
- **Zero NEW build warnings.** Exactly **2 pre-existing NU1903** on SSH.NET in `Harbora.Postgres.Tests` — leave them. **Security is out of scope by the owner's standing instruction; the secret-handling requirements below are ordinary data-handling decisions the spec asks for, so implement them without writing attack-scenario analysis.**
- **Baseline that must not drop:** build 0 errors; **4,341 passing, 0 failing**.
- **A migration only if Task 1 proves one is needed.** The spec's expectation is none. If you add one, generate it with a **fresh build — never `--no-build`**, which captures a stale model; `MigrationConsistencyTests` diffs the snapshot.
- **Never renumber an existing enum value.** `BackupTargetType.Application = 0` is a persisted wire value.
- **Bilingual.** Every user-visible string through `@T["…"]` or the `isFa` ternary.
- **The panel renders Persian by default in tests.** Assert on `data-` attributes, route fragments and staged content — never on a sentence in either language.
- **Test names read as sentences.**
- **Narrative commit messages** — read `git log --oneline -10`. End each with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Shared tree.** Never `git stash`, `git stash pop`, `git checkout -- .`, `git reset --hard` or `git clean`. Stage by explicit path; never `git add -A`.
- **`main.ts` carries a hand-maintained lucide icon list** and `IconCoverageTests` fails on a missing icon. Seven agents have hit this.
- **Environmental trap.** `MSB3491 "Access to the path … denied"` on `obj/` with a green suite means leftover MSBuild processes hold locks. Run `dotnet build-server shutdown`, then rebuild.

---

## Task 1: Find out what a backup already holds, then decide the two open questions

**This is a verification task before it is a build task.** Do not write the control or the restore
guard until it is answered.

**Files:**
- Read: `src/Modules/Backup/Harbora.Modules.Backup.Contracts/BackupEnums.cs`, `src/Modules/Backup/Harbora.Modules.Backup.Infrastructure/BackupTargetResolver.cs`, and the application target it delegates to (find it — `AcquireAsync` at `BackupTargetResolver.cs:107` calls `applications.StageAsync`)
- Test: `tests/Harbora.Tests/` — a new file for what an application backup captures

**What is already established, and must not be re-derived:**
- `BackupTargetType.Application = 0` exists and is routed by both `Validate` and `AcquireAsync`.
- The stager `Include`s `a.Volumes` and `a.EnvironmentVariables`, writes volume data one directory per volume under a `volumes/` directory, and refuses a volume whose name the daemon would reject rather than skipping it.

**What you are establishing:**

1. **Is the image reference captured?** Volumes and variables demonstrably are. If the image reference is absent, adding it is this sub-project's real content — the owner chose all three so a restore has something to restore onto. Read `Deployment.ImageTag` and how the app's current deployment is found (`App.ActiveDeploymentId`).
2. **What does the artefact do with a secret value?** `EnvironmentVariable.IsSecret` exists, so capturing variables captures secrets on the ordinary path, not an edge case. Find the module's existing encryption path and establish whether staged variables travel inside it.
3. **What happens on a restore into a different workspace?** Find out whether the panel can express that today before deciding anything about it.

- [ ] **Step 1: Write tests that state what is captured**

Assert against **what was staged** — the files and content the stager produced — not against the code path having executed. A test that asserts `StageAsync` returned success proves nothing about what is in the archive.

Cover: an app's volumes appear; its environment variables appear; a secret variable's value does not appear in plaintext.

- [ ] **Step 2: Run them.** Some will pass (volumes, variables) and some will fail (whatever is missing). **That is the point** — the failures are the finding. Record which passed and which failed in your report before changing any production code.

- [ ] **Step 3: Add the image reference if it is missing.** If it is already there, say so and skip.

- [ ] **Step 4: Settle the secret rule and write it into the code as a comment, not only into the report.** The next person to read the stager should find the decision beside the code that implements it.

- [ ] **Step 5: Run the covering tests, then the full suite. Commit.**

**Report, explicitly:** which of the three the backup captured before you touched it, which you added, and what rule you settled for secrets and for cross-workspace restore.

---

## Task 2: The control on the app's own page

**Files:**
- Modify: `src/Harbora.Web/Views/Apps/Details.cshtml`, `src/Harbora.Web/Controllers/AppsController.cs` (or a new `AppsController.Backups.cs` partial, following `AppsController.Addresses.cs` and `AppsController.Tabs.cs`)
- Modify: `src/Harbora.Web/ViewModels/AppTabViewModel.cs` (`AppOverviewViewModel` lives here, **not** in a file of its own — four agents have looked)
- Test: `tests/Harbora.Tests/Http/InstantAppBackupHttpTests.cs` (create)

**Interfaces:** consumes whatever Task 1 established about what is captured.

**Where it goes.** Overview, beside the address and specifics blocks that B1, B2 and B3 built. Same
`rounded-xl border border-line bg-surface` card shape the file uses throughout.

**Three things to get right:**

1. **It says what it would capture before it does anything.** "Back up now" on an app with no volumes should not produce an archive of nothing presented as a success. State the contents; then offer the action.
2. **A Cron or ReleaseTask app has no running container and may have no volumes.** Say what a backup would contain for it rather than hiding the control or offering an empty one — the rule B3 settled for health and uptime.
3. **The action is a POST with an anti-forgery token**, and it lands its outcome somewhere that renders `TempData` — the seam that produced two Criticals in sub-project A. `Views/Apps/_Shell.cshtml` renders the banner for every tab; confirm your redirect target is under it.

- [ ] **Step 1: Write the failing HTTP tests.** Follow `tests/Harbora.Tests/Http/AppAddressHttpTests.cs` for the fixture shape — `[Collection(HarboraHttpCollection.Name)]`, `(HarboraHttpFixture fixture)`, `Panel.GivenUser` / `Panel.SignedInAs`, and `client.AntiforgeryTokenFrom` for the POST (that is the real helper name; earlier plans guessed wrong).

Cover: the control appears on an app's Overview and names what it would capture; pressing it creates a backup for **this** app; an app in another workspace 404s rather than backing up.

- [ ] **Step 2: Run and watch them fail.**

- [ ] **Step 3: Implement the action and the card.** Reuse the backup module's existing creation path — do not write a second way to make a backup. Check what capability policy the backup screens already require and apply the same one.

- [ ] **Step 4: Run the covering tests, then the full suite. Commit.**

---

## Task 3: A restore that admits what it cannot do

**Files:**
- Modify: the restore surface Task 1 identified
- Test: extend `tests/Harbora.Tests/Http/InstantAppBackupHttpTests.cs`

**The rule, and the vocabulary it must borrow.** A backup naming an image nobody can pull restores
into nothing. Sub-project F already named this exact fact for deployments: it marks which rows can be
rolled back to instantly and which need a redeploy from source, deriving the answer from the pruner's
own rule so the two cannot disagree — `DeploymentPlanning.RollbackEligibleDeploymentIds` and
`RetainedImageTags`. **Read that first and reuse its language.** One product with two names for one
idea is worse than either name.

**The cross-workspace rule** is whatever Task 1 settled. Implement that, and let the test name it.

- [ ] **Step 1: Write the failing tests.** A restore whose image no longer resolves is reported **before** it is attempted, in F's vocabulary; a restore into another workspace follows the settled rule.

- [ ] **Step 2: Run and watch them fail.**

- [ ] **Step 3: Implement.** The check happens where somebody can still act on it — on the screen, not after the button.

- [ ] **Step 4: Run the covering tests, then the full suite. Commit.**

---

## What this plan is not

Volume browsing, upload or external access (sub-project D) · scheduling, which backup policies
already do · changes to the module's storage or encryption beyond stating what already happens ·
a new target type — `Application` exists, is wired, and its enum value is persisted.
