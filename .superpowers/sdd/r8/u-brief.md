# U — The pages: one question, one screen, one button

Read `.superpowers/sdd/r8/CONSTRAINTS.md` and `docs/superpowers/specs/2026-11-04-launch-design.md` first.

The owner, whose own panel this is: «حتی خود من حال ندارم». What they want is a form. The measure of this work is how little a person who has written an app has to know: no
Docker, no ports, no "environment", no "service kind", until they ask. Persian is the default language and every sentence must read as a person would say it.

You own `LaunchController`, its views, its small script, and the entry points. Code against `ILaunchService`
(`src/Harbora.Infrastructure/Launch/LaunchContracts.cs`); S implements it in parallel, so your HTTP tests inject a fake through `HarboraWebFactory`
(see `tests/Harbora.Tests/Http/`, and how other tests swap services). Render checks with the existing `Views/Shared/Design/_AppChecks.cshtml`, which V owns. Do not edit it,
and do not write services in the controller.

## Routes (`src/Harbora.Web/Controllers/LaunchController.cs`, `[Authorize]`, `[Route("launch")]`)

| | |
|---|---|
| `GET /launch` | The question |
| `POST /launch` | `[Authorize(Policy = Capabilities.AppsCreate)]`, antiforgery. A Git URL, or a multipart zip streamed to `Path.Combine(runtime.WorkDir, "_uploads", …)` (the pattern of `ApiV1Controller.DeployArchive`, including deleting a half-written file; `[RequestSizeLimit]` and the form limit at the same 512 MB, and check the form-body limit really is raised). Then `StartAsync`; refusals back onto the form; success redirects to `/launch/{id}` |
| `GET /launch/{id}` | One page whose content depends on status. 404 when `GetAsync` is null |
| `GET /launch/{id}/progress` | The same page's changing part as a partial, for the poll |
| `POST /launch/{id}/quote` | The cost partial for the current form, when a service or the size changes |
| `POST /launch/{id}/confirm` | `ValidateConfirmAsync`-then-`ConfirmAsync`, antiforgery, `Capabilities.AppsCreate`. Refusals back onto the confirm screen with what was typed still there. A second submit lands on the progress page, not an error |
| `POST /launch/{id}/retry` · `POST /launch/{id}/abandon` | Antiforgery, `Capabilities.AppsCreate` |

Beware: a parameter named `action` receives the method's own name; TempData turns a GUID-shaped string into a `Guid`, so `as string` is null across a redirect; `[AutoValidateAntiforgeryToken]` conventions apply.

## 1. The question (`GET /launch`)

A heading, «کدت کجاست؟» / "Where is your code?", and two cards side by side (stacked on a phone):

- **از گیت** — one field, the repository URL, placeholder `https://github.com/you/your-app`, and a button «بخوانش». Under a small `<details>` «مخزن خصوصی است؟ یا شاخه‌ی دیگری می‌خواهی؟»: token and branch. Blank branch means the default; say so.
- **از کامپیوترت** — a zip drop zone that is a real `<input type=file accept=".zip,.tar.gz,.tgz">` and works with no script. A line: «پوشه‌ی پروژه را zip کن و اینجا بینداز. `node_modules` و `.env` را خودمان کنار می‌گذاریم.»

Below them, quietly: «یک اپ آماده می‌خواهی؟ وردپرس و بقیه در [قالب‌ها] با یک کلیک نصب می‌شوند.» and «تنظیم دستی همه‌چیز: [فرم کامل]» → `/apps/create`.

## 2. Reading (`Inspecting`)

A calm line: «دارم کدت را می‌خوانم…» with the source label and a spinner, polled. It should feel like seconds; a big repository can take minutes, so after a minute the line says that it is still working and that the page can be closed.

`InspectFailed`: the service's message, the form again with what was typed, and nothing alarming. Nothing was created; say so.

## 3. The confirm screen (`AwaitingConfirmation`) — the heart of this

Three blocks, top to bottom, on one screen. Written as sentences, not as a settings page.

**این را پیدا کردم** — stack and kind in words: «یک اپ Node.js که روی وب جواب می‌دهد» / «یک ربات Python که پس‌زمینه اجرا می‌شود (آدرس وب لازم ندارد)». Every `InspectionNote` here, plainly. `RootCandidates`: a required choice of folder, the only time this block asks anything.

**این‌ها ساخته می‌شوند** — a list with a checkbox on every optional row:
- the app, with its address as it will be: `name.apps.example.com`, editable in place by editing the name;
- each detected service, ticked if `Certain`, with why beside it in small text: «چون `pg` در `package.json` هست». Unticked if not certain, saying «شاید لازم باشد»;
- «یک دیتابیس دیگر اضافه کن» as a `<details>` holding the rest of `ServiceOptions`, for what the inspector missed;
- generated secrets as one quiet line: «`JWT_SECRET` و `SESSION_SECRET` را خودم می‌سازم» (the names only, never a value).

**فقط این‌ها را از تو لازم دارم** — the `NeedsAnswer` fields, each one input labelled with the variable's name, the example value as placeholder if there is one, password-type when `IsSecret`. Blank is allowed, with a small warning that the app may not start without it. No such fields: this block does not appear at all, and that absence is the goal. `KeepExample` values and `AliasOfAttach` names live under the details below, not here.

Then the cost, from `QuoteAsync`, one sentence, updated by the `quote` POST when a checkbox or the size changes (no script: the figure stays as of the page's load, and the server still refuses honestly): «الان ۱۲٬۰۰۰ تومان از موجودی کم می‌شود (یک ساعت اجرا)، بعد ساعتی ۱۲٬۰۰۰ تومان. موجودی: ۳۰۰٬۰۰۰.» Exempt: «هزینه‌ای از این فضای کاری کم نمی‌شود.» A `Refusal`: shown in place of the sentence, with a link to `/billing`, and the button disabled (the server refuses anyway). Find how the panel already formats money and use that.

Then one big button: **راه بینداز** / **Launch**.

Everything else lives under one `<details>` «جزئیات» closed by default: name and slug, kind (web/worker in words), port (web only), start command, release command, root directory, environment (only if there is more than one), server and size (the `SizePicker` view component: read `Views/Apps/Create.cshtml` for how, with `allowNoLimit: false`), and `KeepExample` values as editable rows. Nobody has to open it.

## 4. The timeline (`Creating` onward)

`Steps` as a vertical list with an icon per state, each with its detail and link. While the deployment runs, the deploy step links to `/deployments/{id}` («لاگ زنده»). Polled every 2–3 seconds through `/launch/{id}/progress`, replacing the partial and dispatching `harbora:content-changed` so icons render; stops once `IsTerminal`. **With no script the page still works**: a `<noscript>` refresh link, or a meta refresh only while the launch is in flight.

- `Succeeded`: a large «اپت بالاست» with `PublicUrl` as the main link (a Worker: «ربات اجرا شده» and a link to its logs), then `_AppChecks`, then «برو به صفحه‌ی اپ».
- `SucceededWithProblems`: «اپ بالا آمد، ولی یک چیز درست نیست», the checks, the failed ones first. Never the green treatment.
- `Failed`: the step that stopped, its message, what exists so far named, «دوباره امتحان کن» when `CanRetry`, otherwise the message's own next step.

Do not say anything worked that the view does not say worked. `Skipped` is not green.

## 5. Entry points

- **The dashboard** (`Views/Home/Index.cshtml`): with no resources, the empty state becomes a real first-run card aimed at `/launch`, not a table of zeros: «اولین اپت را بیاور» with one line and the button.
- **The apps list**: the primary "new app" button goes to `/launch`; a secondary link keeps `/apps/create`. Do the same for its empty state.
- **The full form** (`Views/Apps/Create.cshtml`): one line at the top offering `/launch`. Otherwise untouched. `AppCreate*HttpTests` must stay green.
- **The sidebar**: `launch` at the top of the `deploy` group in `NavigationMap`, visible in both panel modes, with its label in `_Sidebar.cshtml`, for whoever may create apps.
- **A new version of an uploaded app**: `src/Harbora.Web/Controllers/AppsController.Upload.cs`, `POST /apps/{id}/upload`, `Capabilities.AppsDeploy`, the per-action access check `AppScopeCensusTests` demands, for an app whose source is `Upload`; takes the same zip and queues a deployment with `SourceArchivePath`, then redirects to the deployment. A small form on that app's overview. Today such an app can only be updated with the CLI, and someone who gave us a zip has no CLI.

## Script

`src/Harbora.Web/Scripts/launch.js`, imported from `main.ts`, only acting when its elements exist: the poll, the quote refresh (debounced, ignoring stale answers), the drop zone's filename. Keep the pure parts exportable and test them with `node --test` under `Scripts/tests/`. Every icon goes through `main.ts` (`IconCoverageTests`). Only you run `npm run build` in your worktree, and never during a `dotnet` command.

## Look

The panel's own tokens and components (`form-field`, `form-control`, `btn-primary`, `btn-secondary`, `dashboard-panel`, `text-ink*`, `bg-surface*`, `border-line`, `text-ok`, `text-danger`). Logical classes. No page scroll sideways at 375 px. No new colours. It should look like the same product, only calmer: more space, fewer boxes, one obvious next action per screen.

## Tests

`tests/Harbora.Tests/Http/LaunchHttpTests.cs` with a fake `ILaunchService`: each status renders what it should and nothing misleading (a `Skipped` check is not `text-ok`; a failed launch has no success heading); antiforgery on every POST; a capability refused; another workspace's id is 404; a refusal keeps what was typed; no-`NeedsAnswer` means that block is absent; a secret field's value is never echoed back into the page; the upload path writes the file and a failed start deletes it; Persian and English both render; the sidebar item; the dashboard's first-run card; `/apps/{id}/upload` for a non-upload app is refused.

## Worktree

`E:/Cash.Net/source/repos/sadrazkh/Harbora-r8-u`, branch `r8/u`. Report: `.superpowers/sdd/r8/u-report.md`.
