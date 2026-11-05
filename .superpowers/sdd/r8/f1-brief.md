# F1 — Fix pass, server side

Read `.superpowers/sdd/r8/CONSTRAINTS.md` first. Two independent reviews read the integrated code and found the defects below. Each one was traced through the code by the reviewer and has not been run, so **confirm each by reading before you fix it**; if one is wrong, say so and leave it. Each fix gets a test that fails without it. The owner's request behind all of this: a person who wrote an app presses one button and it works, or they are told honestly that it did not.

**You own** `src/Harbora.Infrastructure/`, `src/Harbora.Shared/` and their tests. **F2 owns** `src/Harbora.Web/` (controllers, views, script), `src/Harbora.Cli/` and `docs/`, in parallel. Do not edit those. The fields you two share are already committed in `LaunchContracts.cs` and `SourceInspection.cs`, added but not filled in: `LaunchEnvField.Service`, `LaunchProposal.SuggestedSlug`, `LaunchQuote.RefusalIsAboutMoney`, `LaunchView.AppGone`, `LaunchView.Created`, `ServiceEvidence.ReasonFa`. You fill them; F2 draws them. If a page test in `tests/Harbora.Tests/Http/` breaks from your change, change the test and say so, but leave the view to F2.

No new migration. Everything stored already fits: `InspectionJson` and `ChoicesJson` are text. Commit per item or per small group, so a fix can be read alone.

## 1. A launch goes green when nothing was verified (most serious)

`LaunchService.Advance.cs` (around 268), `AppVerifier.cs` (around 161), `AppVerificationContracts.cs` (`HasFailures`). `HasFailures` counts only `Failed`, so `Skipped` and `Warning` both end as `Succeeded`:
- An exception in `RunAsync` returns one complete, unstored `Skipped` check, so a transient database error says "Your app is up".
- A hung daemon leaves a worker's running, restarts and logs all `Skipped`, and it says "Your bot is running".
- `/` answering 200 while the log says `ECONNREFUSED 127.0.0.1:5432` is a `Warning`, so green.

The rule: `Succeeded` needs no `Failed`, no `Warning`, **and** the check that proves it runs (`running`, for a long-running kind) to have `Passed`. Otherwise `SucceededWithProblems`. A kind that is not long-running keeps its current meaning; think it through and test it. A fault in the panel's own run is an incomplete report, so the launch stays `Verifying`, tries again, and the existing 15-minute limit ends it at `verify`. Keep the pure decision pure and tested. The page's sentence for "could not check" as opposed to "something is wrong" is F2's; it reads `Checks`.

## 2. Laravel, and PHP with a database, cannot work

`Buildpacks.cs` PHP template. The inspector proposes `php artisan migrate --force`, but the image has no unzip, git or zip extension, and `composer install ... || true` hides the failure. So there is no `vendor/`, the release task dies, and the deploy fails with the database already created and billed. Past that it has no `pdo_mysql` or `pdo_pgsql`, and the document root is not `public/`.

Fix the template: install what composer needs and the common extensions (`pdo_mysql`, `pdo_pgsql`, `zip`, and `bcmath`/`intl` or whatever a stock Laravel requires; read what Laravel asks for rather than guess), composer present, `--no-dev --no-interaction`, and a real failure fails the deployment rather than `|| true`. `DocumentRoot` becomes `public/` when `artisan` exists, with `mod_rewrite` on. A plain PHP app with no `artisan` keeps its root. `GeneratedDockerfileTests` pins every template to the byte, so update that pin knowingly and say so. P will run this for real; nothing here can be built locally.

## 3. Slugs must be ASCII

`AppProvisioningService.cs` around 521 keeps any `IsLetterOrDigit`. A folder or zip called `فروشگاه` becomes an image tag `harbora/فروشگاه:build-1`, which Docker rejects, after the database has been created and charged, and retry repeats it. Restrict the slug to `[a-z0-9-]` with a sensible fallback (`app`) when nothing survives, and keep the person's name as the display name. Check `DatabaseProvisioningService.Slugify` and container names the same way. Existing apps' stored slugs do not change. The form path benefits from this too, so check its tests. Fill `LaunchProposal.SuggestedSlug` through the same uniqueness rule creating an app uses, so launching a repo twice previews `-2`.

## 4. Combined limits are never checked

`LaunchService.Proposal.cs` around 249 and 373. The web form always posts `ServerId`, so `SettleServerAsync` skips the combined capacity sum, and each resource is validated alone. On a small server or a plan with one slot, validation passes, the first database is created and charged, and the next is refused: a charged partial launch where retrying cannot help. Check the sum (`scheduler.CheckAsync(chosen, mem*count, cpu*count)`, or whatever is right) even with a chosen server, and validate counts against the plan as a batch, so `ValidateConfirmAsync` refuses before anything exists.

## 5. Generic database names with no certain engine

`EnvVarClassifier.cs` (around line 69), `LaunchService.Proposal.cs` `AliasesFor`. `DB_HOST` with no detected database, or with two, becomes `NeedsAnswer` with `Service` and `Role` null. The `database-unclear` note tells the person to tick an engine, but `AliasesFor` takes only `AliasOfAttach`, so no alias is written, and the app reads an empty `DB_HOST`. A plain PDO app is exactly this shape. Keep the `Role` on those names, and when exactly one database engine is ticked, resolve them as aliases against it. What the person typed in that field still wins. An engine ticked by hand gets these aliases through the path that already computes them when `Aliases` is null.

## 6. Someone with their own database

`LaunchService.Proposal.cs` around 81. A `ProvidedByAttach` name has no `EnvField`, so a person who unticks Postgres (they use Neon) has nowhere to put `DATABASE_URL`, and weak evidence hides it by default. Include `ProvidedByAttach` names in `EnvFields`, with `Service` set on those and on aliases. F2 decides how they are shown and filters by disposition. Check nothing on your side assumes they are absent: `Defaults.EnvVars` must not start including them with a null value, or the "not set" line would name `DATABASE_URL` for an app that has it attached. A value given for such a name is set as the app's own variable, and the existing precedence already means it outranks the attachment. Pin that.

## 7. What exists, and a deleted app

`LaunchService.View.cs`. Fill `LaunchView.Created` from the rows: each database and the app, with its link and whether it is ready. Set `AppGone` when `launch.AppId` is set and the app no longer exists. Also fix the databases step getting `Done` while they are provisioning or already failed (around 159); derive it from the rows. `UnsetVariablesAsync` reads `choices.EnvVars`, and F2 will now post blanks with a null value, as the CLI already does: check that path names them and that nothing sets them.

## 8. Ordinary projects misread

- **`KindDetector.cs` around 44, Procfile `web:` discarded.** `web: gunicorn app:app` beside `app.py` runs `python app.py`, which is a loopback dev server at best. Keep the declared command as the start command wherever a `web:` line exists.
- **`RootLocator.cs` around 36, JS monorepos.** A root tooling `package.json` satisfies `HoldsSomething`, so `apps/web` + `apps/api` or `client/` + `server/` never get the folder question and fail at `npm start`. Stop at the root only if it can start something (`NodeStartRule.CanStart`), or if it has no app-looking children. Mind Laravel with a `resources/` tree, and a root with a real `build` and no child apps, which is an ordinary Vite project.
- **Nuxt 3.** A default project has `build` and no `start`, so it is proposed as Frontend, but an SSR build has no `index.html` and the deploy fails saying it is not a site. If `nuxt` is a dependency with no `start`, propose Node with `node .output/server/index.mjs` as the start command, which `Detect` then honours. Check the parity tests' position on this. SvelteKit's adapter decides its output, so leave it to the honest failure sentence; only make sure that sentence is what they see.
- **`Buildpacks.cs`, toolchains.**
  - `sdk:9.0` cannot build `net10.0`: read `TargetFramework` from the csproj and use its major version for both the SDK and the runtime image, defaulting to what it is today when absent or unreadable.
  - `golang:1.22` with `GOTOOLCHAIN=local` fails on `go 1.23+`: read the `go` line in `go.mod`.
  - `go build .` fails on `cmd/server/main.go`: when the root has no `main.go` and there is exactly one `cmd/*/main.go`, build that.
  - Update the byte pins knowingly. An app whose versions match today's defaults keeps a byte-identical Dockerfile.

## 9. Zips

- **Executable bits are lost.** `SourceArchive.cs` around 162 uses `File.Create`, and the mode is only read to detect links. A GitHub zip with `ENTRYPOINT ["./entrypoint.sh"]` gets permission denied, while the same repo by URL works. Apply `File.SetUnixFileMode` from `ExternalAttributes` where the platform supports it, and only when the entry carries a mode.
- **The wrapper folder becomes the app's permanent root.** `my-shop-main` is stored as `BuildContextPath`, so the next `harbora deploy` has no wrapper, a zip of `repo-1.3` has a different one, and both fail in `AppRootDirectory`. Strip a sole top-level folder when extracting a zip, so nothing downstream ever sees it. `LaunchWithRealInspectorTests.A_zip_that_wraps_everything_in_one_folder_builds_from_that_folder` changes accordingly: the root is then null. A sole folder and nothing else, only. Files beside it mean it stays.
- **The refusal in Persian.** `LaunchService.cs` around 331: English includes "expands to more than 2048 MB", while Persian always says it is empty or not a valid zip, so a person repeats a hopeless step. Give `SourceArchive`'s refusals a Persian text and use it.

## 10. Smaller

- **`Deploying` with no deployment id has no deadline** (`LaunchService.Advance.cs` around 186): an unexpected exception retries every 3 seconds for ever while the page says "Getting it running". Add a limit in the pure decision, such as 20 minutes in the status, and fail at `deploy`.
- **Duplicate on retry after a crash:** `CreateAsync` commits and charges before the id reaches the launch row. On retry, before creating, look for a service or app in this workspace with the name this launch would use, created after this launch started, and adopt it rather than making `{name}-2` and charging again. If that takes more than about twenty lines, stop, and say so in your report rather than build it.
- **Money refusals:** `LaunchQuote.RefusalIsAboutMoney` is true only for too little balance. `DefaultSizeKeyAsync` (`LaunchService.Proposal.cs` around 158) returns the platform default unchecked and may be unpriced: it should skip an unpriced tier the way the picker does, or the proposal should say so.
- **A withdrawn engine disappears:** a certain Postgres is in `Defaults.Services` but not in `ServiceOptions` when the operator withdrew it, and the launch goes ahead with no database and no sentence. Drop it from `Defaults.Services` and add an `InspectionNote` in both languages naming what was needed and not offered. Notes are already drawn everywhere.
- **`ServiceEvidence.ReasonFa`:** `ServiceDetector.cs` reasons are English on a Persian screen. Give each one its Persian. Service messages in this flow should say `کن` rather than `کنید`, matching the pages F2 owns: "you" in the singular, the way a person talks. Apply that to `LaunchService.*`, inspection notes and `AppCheck` text.

## Not in scope

Security review, vulnerability hunting and attack scenarios: out, always. `RetryAsync` losing typed answers is accepted and already named on the page.

## Finishing

P is running a real launch on CI and fixes bugs it finds on its own branch. If you and P land on the same thing, the merge will show it. V is still landing a route check in `AppVerifier` and a provisioning wait in `DeploymentPipeline`: keep your changes there small, and do not touch the `url` check.

Worktree: `E:/Cash.Net/source/repos/sadrazkh/Harbora-r8-f1`, branch `r8/f1`. Report: `.superpowers/sdd/r8/f1-report.md`, one line per item: what changed, where, the test that pins it, and any item you found wrong or left. Full `Harbora.slnx` run on your last commit with `Harbora.Tests.dll` confirmed in the output. Commit with `git -c user.name="sadra zadeh khameneh" -c user.email="1986.aandrii@gmail.com" commit`, no Co-Authored-By trailer. Do not push.
