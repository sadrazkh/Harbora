# U — the pages: report

Branch `r8/u`, based on `377ae2c`. All of it is commits under the owner's name; nothing pushed.

## What was built

- **`LaunchController`** (`[Authorize]`, `[Route("launch")]`): `GET /launch`, `POST /launch` (a URL, or a multipart archive written under `_uploads`), `GET /launch/{id}`, `GET /launch/{id}/progress`, `POST /launch/{id}/quote`, `/root`, `/confirm`, `/retry`, `/abandon`. Antiforgery on every POST, `Capabilities.AppsCreate` on all of them but the quote. Every route naming a launch asks `GetAsync` first, so another workspace's id is 404 whatever the route goes on to do. The controller turns a form into choices against the proposal: which variables exist, which are secret and which services are on offer are the proposal's facts, not the form's.
- **Views** (`Views/Launch/`): `Index` (the question, also what a failed read draws), `Root` (which folder of a several-app repository), `Confirm` (the three blocks, the cost, the button, one closed «جزئیات»), `Details` with `_Progress`, `_Timeline`, `_Steps`, and `_Cost`. Checks through the existing `Design/_AppChecks`, not edited.
- **The folder question, a change of contract during the work.** The coordinator's `d7ebc2a` (`ILaunchService.ChooseRootAsync`) is cherry-picked here as `243c0ef`. With `Inspection.RootDirectory` null and `RootCandidates` not empty, `/launch/{id}` shows only «این مخزن چند اپ دارد. کدام را راه بیندازم؟» and one button per folder: no services, no cost, no launch button, and no «بی‌خیال» either. `POST /launch/{id}/root` calls the service; a refusal stays on the question; success goes back to `/launch/{id}`. This replaces the required choice the brief put inside the found block. Once chosen, the found block names the folder and offers «پوشه‌ی دیگری را انتخاب کن» as buttons that submit a small separate form (the `form` attribute), so nothing typed on the screen, a key least of all, is posted along. Confirm and quote before a folder is chosen do nothing: confirm lands back on the question, quote is 404. Among several apps the folder is the proposal's fact, so `RootDirectory` in the choices comes from the inspection, whatever a form posts.
- **`Scripts/launch.js`**, imported from `main.ts`, acting only when its elements exist: the poll (one request at a time, an unchanged answer is not put back, stops when terminal, reloads on arriving at the confirm screen or a failed read), the quote refresh (debounced, ignores stale answers, never sends a variable's value), the address following the name, hiding what only a web app has, the drop zone, a too-big file caught before sending. Icons `Circle` and `CircleMinus` added to `main.ts`.
- **`Infrastructure/UploadedArchive.cs`**: where a browser's archive goes, the copy that deletes a half-written file, the magic-bytes check, a file name that cannot escape the directory, the 512 MB ceiling.
- **Entry points**: dashboard first-run card (replaces the stat bar and both panels when there are no apps and no databases; no button for someone who may not create), apps list primary button and empty state, one line at the top of the full form, `launch` first in the `deploy` group with its label in `_Sidebar` and `_Topbar`.
- **`AppsController.Upload.cs`**: `POST /apps/{id}/upload`, `Capabilities.AppsDeploy`, `MayAsync`, for `AppSourceType.Upload` only; a small form on that app's overview.

Two small changes outside what I own: `AppProvisioningService.Slugify` went from private to public, one word, so the address preview reads the real rule rather than a copy (`DatabaseProvisioningService.Slugify` is already public for the same reason). `AppScopeCensusTests` gained five `Exempt` entries with reasons, which is that census's own stated way in: nothing has a placement until the services the launch calls ask for one.

Judgement calls worth a look:

- **`launch` is also pinned.** The brief says top of the `deploy` group. Every other entry in that group is pinned, so unpinned it would have been the one row inside a closed group, which is not "visible". Added to `pinnedKeys` first after the dashboard.
- **Someone without `AppsCreate`** can open `/launch` (the route table gives it no capability) and is told in a sentence, with no forms that would refuse them. Same on the confirm screen and the first-run card.
- **Blank answer means the variable is not set**, not set to an empty string. Blank name goes back to the suggested name rather than stopping anybody.
- **The placeholder on a secret field** shows the example file's value, as the brief says. It is the customer's own file.
- **A no-script recheck button.** The button is rendered `disabled` on a refused quote, and without a script unticking a service could never re-enable it, so a `<noscript>` «هزینه را دوباره حساب کن» posts the form to redraw the screen and confirms nothing. Not in the brief; without it that person is stuck.
- **Persian digits in the port** count as a number: this page is Persian by default and a Persian keyboard types ۸۰۸۰.

## Files

`src/Harbora.Web/Controllers/LaunchController.cs`, `src/Harbora.Web/Controllers/AppsController.Upload.cs`, `src/Harbora.Web/Infrastructure/UploadedArchive.cs`, `src/Harbora.Web/ViewModels/LaunchViewModels.cs`, `src/Harbora.Web/Views/Launch/*`, `src/Harbora.Web/Scripts/launch.js`, `src/Harbora.Web/Scripts/tests/launch.test.mjs`, `tests/Harbora.Tests/Http/LaunchHttpTests.cs`, `tests/Harbora.Tests/Http/LaunchPanelFixture.cs`. Edited: `Scripts/main.ts`, `Views/Home/Index.cshtml`, `Views/Apps/{Index,Create,Details}.cshtml`, `Views/Shared/Design/{_Sidebar,_Topbar}.cshtml`, `NavigationMap.cs`, `AppProvisioningService.cs`, `AppScopeCensusTests.cs`.

## What was run

@@RUNS@@

**The form limit was proved by removing it.** With `[RequestFormLimits]` deleted from `Start`, `The_form_limit_really_is_raised_past_its_128_megabyte_default` failed (`Failed: 1`); restored and rebuilt, it passed. That test posts a real 129 MB multipart body through the pipeline; a second one does the same to `/apps/{id}/upload`.

**A real browser, by way of rendered pages.** A temporary test (not committed) saved each state as the real Razor views drew it over the fake, and I opened those in the app's browser pane, with `npm run build`'s CSS and script:
- At 375 px, measured for every launch state including the folder question, plus first-run, apps and overview, with every `<details>` forced open: document, `#content-scroll` and `main` all 375 wide, nothing past the edge on any launch page. On `/apps` and `/apps/create` only elements already inside their own scrollers reported past the edge (the status filter, the image-address fold); no page scroll.
- Typing the name moved the address (`my-cool-shop.apps.example.com`), a typed slug won, choosing Worker hid the two web-only lines, and Web brought them back.
- Three ticks made one quote request, carrying the token and no `Values[...]`; a refused answer replaced the sentence and disabled the button (`fetch` stubbed).
- The poll swapped from `Deploying` to `Succeeded`, icons rendered (0 unrendered placeholders), and no further request in the next six seconds (`fetch` stubbed).
- A synthetic drop set the file on the input; an oversize file showed the message, disabled the button and blocked the submit.
- On the chosen-folder screen, the browser reports each «another folder» button as owned by `launch-other-folder`, whose action is `/launch/{id}/root` and which holds its own token.
- Screenshots of the question, the folder question, confirm, problems, failed, first-run and the upload form read as the same product. Light theme only.

## Unverified

- **Nothing here has run against the real `ILaunchService`.** Every launch test uses the fake. On `r8/u` alone `/launch` answers 500, because `ILaunchService` is not registered until S lands. That is expected, and it means the first real look at these pages is after integration.
- **Kestrel's body limit.** Every test here runs on the in-process host, so no body has gone through Kestrel: `[RequestSizeLimit]` is present, and that half of the pair is not exercised. Nor is any limit in a proxy in front of the panel.
- **Over the limit with no script** gets the panel's bare error page, not a refusal on the form, because the limit fires before the action. With a script it is caught before sending.
- A real operating-system drag (the drop was a synthetic `DragEvent`), dark theme, and anything needing Docker or Postgres.

## Contracts I think are wrong

Nothing was reshaped. In order of how much they will bite:

1. **`/deployments/{id}` is not a route.** The design and S's brief both name it; it 404s. The page is `/deployments/details/{id}`. `_Steps` builds the deploy step's link from `DeploymentId` and ignores `LinkPath` there, with a test. Any `LinkPath` or message from S using the short form elsewhere will 404, and that needs a look when S lands.
2. **`LaunchServiceOption` has no aliases.** Aliases only exist on `Defaults.Services`, which holds only the certain ones. A service ticked by hand (the "maybe" Redis, or one found in "add another") goes out with `Aliases: null`, so an app reading `REDIS_HOST` would not get it. Either the option carries them or `ConfirmAsync` works them out when null, from `Inspection.EnvVars`, where `Service` and `Role` already are.
3. **`LaunchStartRequest` has nowhere for the file's name**, yet `AppName` and `SourceLabel` are to come from the archive. The path is all it can read, so I write `_uploads/launch-{guid}/{name}.zip`: the plain file name is the person's. The cost is that the folder outlives the file, one empty folder per launch, once the pipeline or `AbandonAsync` deletes only the file. A `string? ArchiveName` would be cleaner. Also needs a check against what S actually does with the path, for example if it requires the parent to be `_uploads` itself.
4. **`LaunchView` lacks three things the page wanted.** `GitRef`: a failed read must draw the branch again, so it travels as `?branch=` on the redirect. A start time: "after a minute" is counted from when the page opened. Kind: read from `db.Apps` once a launch is terminal, to tell a bot from a web app with no address.
5. **The upload line promises more than S's brief does.** «`node_modules` و `.env` را خودمان کنار می‌گذاریم» is S's job for zip entries only, and the input also accepts `.tar.gz` and `.tgz`, which come from a browser with no client-side filter. The sentence does say zip; a browser tar.gz with a real `.env` is the gap.

## Seen in passing, not touched

- **Other routes cap at 128 MB without saying so.** No other route raises the form limit and there is no global `FormOptions`. `DatabasesController.Import` and `ImportDatabase` declare 2 GB and `BackupManagementActions.Upload` 8 GB with `[RequestSizeLimit]` alone. Removing `[RequestFormLimits]` here produced exactly that failure at 129 MB, so those imports very likely refuse at 128 MB with a bare 400. By reading and by analogy; I did not post to those routes.
- **Other ways into the 19-field form.** The top bar's «منبع جدید» menu, `Projects/Details`, the Monitoring empty state and the apps rail still go to `/apps/create`. They were not in the brief. The top bar is on every page, so it is the one I would change next; it is one row.
