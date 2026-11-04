# F2 — Fix pass, the pages and the terminal

Read `.superpowers/sdd/r8/CONSTRAINTS.md` first. Two independent reviews read the integrated code and found the defects below. Each one was traced through the code by the reviewer and has not been run, so **confirm each by reading before you fix it**; if one is wrong, say so and leave it. Each fix gets a test that fails without it. The owner: a person who wrote an app presses one button and it works, or they are told honestly that it did not. Persian is the default language, in the voice the pages already use: «کدت کجاست؟», singular, the way a person talks.

**You own** `src/Harbora.Web/` (controllers, views, script), `src/Harbora.Cli/`, `docs/` and their tests. **F1 owns** `src/Harbora.Infrastructure/` and `src/Harbora.Shared/`, in parallel. Do not edit those. The fields you share are already committed in `LaunchContracts.cs` and `SourceInspection.cs`, added but not filled in yet: `LaunchEnvField.Service`, `LaunchProposal.SuggestedSlug`, `LaunchQuote.RefusalIsAboutMoney`, `LaunchView.AppGone`, `LaunchView.Created`, `ServiceEvidence.ReasonFa`. You draw them, against a fake that sets them. F1 fills them, and the two are merged afterwards. F1 also changes these, which you should expect:
- `Proposal.EnvFields` will now include `ProvidedByAttach` names, so filter by disposition everywhere you read that list, on the web and in the CLI.
- Slugs become ASCII.
- Launch service messages become singular.

## 1. Someone with their own database, or a service unticked

`Views/Launch/Confirm.cshtml` around 340, `LaunchController.cs` around 344. "These I connect to the database myself" is unconditional, so unticking Postgres (they use Neon) or a service left unticked as a guess still promises it, and the name is not asked, not set and not named.

In the Details fold, list the connection names of ticked services as the quiet line it is now. Give every `ProvidedByAttach` and `AliasOfAttach` name of a service that is not ticked an ordinary input under one short heading, in the spirit of «دیتابیس را جای دیگری داری؟ اینجا بنویس». Without a script, draw every such name's input with a line saying it is only needed if the database above is unticked. With a script, hide it when the service is ticked. A value given there is set as the app's own variable, which outranks the attachment, whatever is ticked. The `harbora launch` equivalent: `--no-service postgres --set DATABASE_URL=…` must not warn that nothing asked for it; check the unticked-alias warning you already print, and name the unset ones.

## 2. Blank answers are never named

`LaunchController.cs` around 344 adds only non-empty values, while `UnsetVariablesAsync` reads `choices.EnvVars`, whose doc promises "an answer left blank". So "Not set: STRIPE_SECRET_KEY" appears after a CLI launch and never after a web one. Post blanks with a null value, as the CLI does. Nothing sets them, and they appear on the timeline.

## 3. A new version silently dropped

`AppsController.Upload.cs` around 59 to 81. `DeploymentEngine.cs` around 97 coalesces onto an in-flight deployment and returns its id rather than throwing, so a person sending a fixed zip during a build lands on the old build's log, the fix never runs, and the file stays in `_uploads`. Compare the returned deployment's `SourceArchivePath` the way `LaunchService.Advance.cs` around 232 does. On a mismatch, delete the file and say in a sentence that a build is running and to send it again when it finishes. The comment at line 73 is wrong too.

## 4. The CLI says it does not recognise a frontend

`Cli/LaunchPlan.cs` around 281. `WhatItIs` has no `"Frontend"` case, so a Vite app prints "It could not tell what this is built with" and then works. Add it, and add a test that goes through every `DetectedStack` value so the next one cannot be forgotten.

## 5. A launch page says "Your app is up" after the app was deleted

`Views/Launch/_Timeline.cshtml` around 59. When `AppGone`, say the app has since been deleted, plainly, with no green and no link to it. The CLI prints the same when it reads such a launch.

## 6. A browser `.tar.gz` keeps `.env` and `node_modules`

`Views/Launch/Index.cshtml` around 77 and 83 promises otherwise, and the filter runs on zips only. Browser forms take zip only: `UploadedArchive.Accept`, the magic-bytes check, the sentence, the same on `/apps/{id}/upload`. A `.tar.gz` gets a sentence saying to send a zip. The CLI's route stays as it is, since its packer filters on the client.

## 7. Billing advice on refusals that are not about money

`Views/Launch/_Cost.cshtml` line 13, `Cli/LaunchCommand.cs` around 308. Offer billing only when `RefusalIsAboutMoney`. On any other refusal, show the sentence with no billing link, and the CLI does not say "Add credit".

## 8. What the failed page says exists

`_Timeline.cshtml` around 48 and 112. `madeServices` is a step's `Detail`, which is only ever the failure text, so the page prints it three times, and it says "what was made so far is still there" even when nothing was. Draw `LaunchView.Created` with links, or nothing at all when it is empty or null. A person must be able to see what is still running and costing money, and reach it to delete it.

## 9. The address preview

`LaunchController.cs` around 406, `Scripts/launch.js` around 174.
- Preview through `Proposal.SuggestedSlug`, since launching a repo twice previews the existing app's address when the app actually gets `-2`.
- A typed slug shows raw (`My Shop.root`): slugify the typed value the same way, and `slugFor` in `launch.js` becomes ASCII-only to match F1's rule, with its test `launch.test.mjs` around 224 changed knowingly. A wholly Persian name previews the fallback.
- The "reserved, choose another" sentence stays after typing a new name: clear it.

## 10. Smaller

- **The folder hint.** `Confirm.cshtml` line 301 says "Blank means the top level", but `LaunchService.Confirm.cs` around 57 puts the inspected folder back. Fix the sentence, or only show the field when there are no candidates and the sentence is true.
- **Buttons offered that the server refuses.** "Check now" (`Apps/Details.cshtml` around 285) and the release command's Save (around 1702) have no capability gate, while the Upload card on the same page does: gate them the way it does. The no-script recheck (`Confirm.cshtml` around 251) posts to an `AppsCreate` route for people without it: gate that too.
- **A stopped poll says nothing.** `launch.js` around 28 and 79: after the session ends or on a 404, the spinner and "it keeps going" stay for ever. Say something and offer a refresh.
- **"Could not check" is not "something is wrong".** F1 now makes `SucceededWithProblems` also mean the key check could not be run. When no check is `Failed` or `Warning`, the page says «بالا آمد، ولی نتوانستم مطمئن شوم که کار می‌کند» and names what could not be checked, instead of «یک چیز درست نیست». The CLI does the same.
- **Language.** Draw `ServiceEvidence.ReasonFa` on the Persian screen, falling back to `Reason`.

## 11. The docs

- `docs/tutorial/15-launch.md` and `docs/cli-deploy.md` follow what changed here: zip only in a browser, the release task on the app's page, your own database. Add a row for a frontend (Vite, CRA and so on, built and served, public build variables) if the "what it reads" table does not have one, since that landed after the chapter was written.
- Both READMEs carry a "Known limitations" bullet saying no launch is recorded end to end on a real server. Leave it for now. It changes when P's proof lands.
- `LaunchChapterTests` holds the chapter to the code, so keep it green, and check its flag test no longer returns early now that `LaunchCommand` is registered.

## Not in scope

Security review, vulnerability hunting and attack scenarios: out, always.

## Finishing

Worktree: `E:/Cash.Net/source/repos/sadrazkh/Harbora-r8-f2`, branch `r8/f2`. Report: `.superpowers/sdd/r8/f2-report.md`, one line per item: what changed, where, the test that pins it, and any item you found wrong or left. Build, the full `Harbora.slnx` run on your last commit with `Harbora.Tests.dll` confirmed in the output, `node --test` under `Scripts/tests`, and `npm run build`, always one after the other and never during a dotnet command. Commit with `git -c user.name="sadra zadeh khameneh" -c user.email="1986.aandrii@gmail.com" commit`, no Co-Authored-By trailer. Do not push.
