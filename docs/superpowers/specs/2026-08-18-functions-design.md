# The runtime works. The panel cannot say so, and one button does the wrong thing.

**Date:** 2026-08-18 · **Status:** decomposition proposed; seven sub-projects, none ready for a plan
until the owner answers §8

The owner's words, which are the requirements:

> «بخش فانکشن رو کاملترش کن و کد زدن توش شبیه ide بشه راحت تر بشه نه شبیه کد زدن تو نوت پد»
> — make the Functions section more complete, and make writing code in it feel like an IDE rather
> than like typing into Notepad.

> «هنوزم خیلی باگ داره و درست کار نمیکنه و واضح نیست»
> — it is still very buggy, does not work properly, and is not clear.

Three complaints, three different fixes. **Incomplete** is §7 and §5. **A poor editor** is §6.
**Buggy and unclear** is §4 and §5 — and those two are not the same thing either: §4 is code that
does the wrong thing, §5 is code that does the right thing and tells nobody.

---

## 1. What exploring changed

Four things, and two of them reverse what was on record.

**First, and the biggest reversal: the memory note is wrong, and it was wrong in the direction that
made this feature look unfinished.** The note said *"a function app is an ordinary `App` whose source
is generated from rows, and no host image has ever been built."* The first half is exactly right. The
second half is right about history and misleading about risk, because it invites the reading that the
runtime is speculative. It is not. I took the real generator — `FunctionProject.cs` and
`FunctionSlug.cs` and `FunctionStarters.cs`, copied byte-for-byte into a scratch harness rather than
retyped — generated all three hosts, and **built and ran every one of them on this machine**. C#
`dotnet publish` succeeded on .NET 10.0.400. All three answered `/__harbora/health` with the right
function count, routed `/hello?name=…` and an explicit `/mail`, refused an unsigned
`POST /__harbora/invoke/nightly` with 401, accepted a signed one with 204, and wrote the cron
starter's own log line to stdout. §3 has the transcript. **The runtime contract is not the problem.**

**Second: nothing in the trigger machinery is missing either.** The searches that usually find a gap
here — for a `Null` implementation registered by mistake, a hosted service behind a flag, a job kind
nothing handles, a tenant filter starving a sessionless reader — all came back clean.
`FunctionEventBus` is the registered `IFunctionEventBus` (`DependencyInjection.cs:279`);
`NullFunctionEventBus` appears only in tests. `FunctionCronScheduler` is an unconditional
`AddHostedService` (`:281`). `JobKind.FunctionInvoke` has a registered handler (`:280`). Every one of
the fifteen keys in `FunctionEvents.All` has a live publisher. And every background read of
`FunctionDefinition` uses `IgnoreQueryFilters()` with a comment saying why — the trap this programme
has fallen into before was seen and avoided.

**Third: the feature does stop, and it stops in the panel, not underneath it.** In three places, of
which the first is the one a customer meets in the first five minutes: **the editor's "Run now"
button is inside another form, so it saves instead of running.** Confirmed with a spec-compliant
HTML5 parser, not by reading. §3(a).

**Fourth: "not clear" is not vagueness — it is a page that fetches the truth and renders something
else.** `AppStatus` is read on both Functions pages and displayed on neither. `EverPublished` is in
the details view model and unused. The app's domain is a live link from the moment the app is created,
before anything is deployed behind it. And the same resource appears in two places under two names,
one of which is the raw enum `InlineCode`. §5.

---

## 2. What Functions is today

Cited so the plan does not re-derive it. The Functions code is 1,741 lines across 14 source files and
879 lines of tests, all landed in four commits — `590dc14`, `855e021`, `310bce9`, `840539a`.

### The model

| Part | State | Evidence |
|---|---|---|
| A function app **is** an `App` | **Built** | `SourceType = InlineCode` appended as value 7, `Enums.cs:45-60`; created at `FunctionsController.cs:122-141` |
| Runtime and invoke secret on the app | **Built** | `App.cs:76` (`FunctionRuntime?`), `App.cs:87` (`FunctionInvokeSecret`, protected) |
| One function per row | **Built** | `FunctionDefinition` — `Domain/Functions/FunctionDefinition.cs:40-88`; slug unique per app, `HarboraDbContext.cs:655` |
| One row per platform-made call | **Built** | `FunctionInvocation` — `FunctionDefinition.cs:99-131`; index `(FunctionId, StartedAt)`, `HarboraDbContext.cs:670` |
| Schema | **One migration** | `20260815233528_FunctionsAndFeatureGrants` |
| Tenant filters | **Both filtered; every background read exempts itself** | Filters at `HarboraDbContext.cs:664` and `:675`; `IgnoreQueryFilters()` at `FunctionCronScheduler.cs:74,103`, `FunctionEventBus.cs:45`, `FunctionInvoker.cs:41,45,87,91,93`, `DeploymentPipeline.cs:1080,1115` |
| Delete cascades | **Built** | App → definitions `HarboraDbContext.cs:663`; definition → invocations `:673` |
| The entitlement | **Built** | Ships `Locked` (`PlatformFeatures.cs:51`); enforced server-side by `[RequireFeature]` (`FunctionsController.cs:29`, filter at `RequireFeatureAttribute.cs:26-64`) and again at invoke time (`FunctionInvoker.cs:51-58`) |
| Sidebar entry | **Built, under Build** | `NavigationMap.cs:120` — greyed with a lock when the workspace is not entitled |

### The screens — all four of them

| Screen | Lines | What it does |
|---|---|---|
| `Views/Functions/Index.cshtml` | 76 | Cards: name, slug, runtime label, function count, and one of *never published* / *unpublished changes* / *live* |
| `Views/Functions/Create.cshtml` | 58 | Name, one of three languages, instance size |
| `Views/Functions/Details.cshtml` | 126 | The functions table, a Publish button, an "add function" link |
| `Views/Functions/EditFunction.cshtml` | 170 | One `<textarea>`, a right rail of trigger settings, Save, Run now, recent runs |

There is no fifth screen. No logs, no environment variables, no deployments, no delete, no metrics —
all of which exist for this app, on the Apps pages, unlinked from here. §5.

### What happens when somebody saves code

`FunctionsController.SaveFunction:292-345`. Normalise the name to a slug (`FunctionSlug.cs:19-37`),
apply the trigger's own field and null the others (`:308-311`), then
`FunctionAppService.Validate:56-109` — a static function over the candidate and its siblings, which
refuses a nameless function, an unusable slug, a duplicate name, two HTTP functions on one route, an
unreadable cron expression, an unknown event key and empty code, each in both languages. On failure
the tracker is cleared and the posted model is re-rendered, so the typed code survives. On success:
`NextRunAt = null` (`:332`), `MarkDirtyAsync` marks **every** function in the app unpublished
(`FunctionAppService.cs:139-147`), save, audit, and **redirect away from the editor** to Details
(`:344`).

### What happens when they deploy

`Publish` (`FunctionsController.cs:213-235`) issues the invoke secret if missing, then
`FunctionAppService.PublishAsync:126-128`, which is one line: an ordinary
`QueueDeploymentAsync(new DeploymentRequest(appId, Manual, userId))`. From there the pipeline
switches on source type (`DeploymentPipeline.cs:790-791`) into
`BuildFromInlineCodeAsync:1104-1143`, which stamps `_codeReadAt`, reads the rows unfiltered, refuses
an app with no functions, writes `FunctionProject.Generate(...)`'s files into the work directory and
hands the directory to `BuildFromSourceAsync:1150` — the same method a Git checkout reaches. The
invoke secret is injected into the container environment at `BuildEnv:1217-1218`, deliberately not as
an editable variable. On success, `MarkFunctionsPublishedAsync:1076-1086` clears the unpublished flag
for rows whose `UpdatedAt <= _codeReadAt`, and a `deployment.succeeded` event is published
(`:518`).

### What runs

`FunctionProject.Generate` (`FunctionProject.cs:48-73`) emits a complete build context per runtime —
a `Dockerfile.harbora`, the host, and one file per function — in slug order so the bytes are stable
and Docker's layer cache works. The host answers three kinds of caller:

| Door | Path | Who knocks |
|---|---|---|
| Health | `GET /__harbora/health` | the deployment's own probe; `App.HealthCheckPath` is set to it at create (`FunctionsController.cs:136`) |
| Invoke | `POST /__harbora/invoke/{slug}` + `x-harbora-invoke` | the panel: cron, events, Run now |
| Everything else | longest matching route; a lone HTTP function also answers `/` | a visitor, through Traefik |

Triggers: **HTTP** through the app's assigned domain; **cron** through `FunctionCronScheduler` —
`BackgroundService`, 25 s startup delay, 1-minute tick, advances `NextRunAt` *before* queueing
(`:135-136`), never replays missed runs; **events** through `FunctionEventBus`, matching subscribers
inside one workspace only and swallowing its own failures so a customer's handler cannot break the
deployment that raised it.

Six live publishers, covering all fifteen advertised keys: `DeploymentPipeline.cs:518` and `:659`,
`NotificationService.cs:140-148` (via `FunctionEvents.ForAlert`, which is how six alert kinds become
six event kinds without a second set of raise sites), `GitWebhookProcessor.cs:95`,
`WorkspaceAccountService.cs:109,161,215`, `BillingSuspension.cs:404,599`.

Every platform-made call is `FunctionInvoker.QueueAsync:38-83` — refuse if disabled, refuse if the
workspace lost the entitlement, refuse if the app was never published, then write the invocation row
*and its envelope* before enqueuing `JobKind.FunctionInvoke` exclusively on the function id, so two
calls of one function cannot overlap and a call due at the moment of a restart is still made.
`ExecuteAsync:85-150` posts it; `CompleteAsync:179-189` records status, duration and a truncated
error. `SettleAbandonedAsync:67-90` writes off anything still queued after 30 minutes.
`DataRetentionSweeper.cs:213` prunes completed rows after `Retention:FunctionInvocationDays`
(30 by default, `RetentionOptions.cs:67`).

### Tests

`FunctionProjectTests` (18), `FunctionTriggerTests` (19), `FunctionValidationTests` (17). They cover
the generator, the slug rules, the validator, the cron decision, event fan-out and workspace
isolation, the reaper and retention — all of it pure or in-memory, which is the right choice on a
machine with no Docker. **There is not one web-layer test.** No test renders `EditFunction.cshtml`,
posts to `SaveFunction`, or presses Run now — which is precisely why §3(a) shipped.

---

## 3. Does it work end to end? Where exactly does it stop?

The most valuable section, and the one the owner's «درست کار نمیکنه» is about.

### The runtime is proven, on this machine, today

The prior session's note has to be superseded, so here is the evidence rather than an assertion. I
built a scratch harness in `%TEMP%` around **the real generator source files, copied not retyped**,
seeded it with the same starters the panel seeds (`FunctionStarters.For`) for three functions — a
default-route HTTP one, an explicit-route HTTP one, and a cron one — and generated each runtime.

**C#** — `dotnet publish Host.csproj -c Release`, the exact command in the generated Dockerfile,
succeeded on .NET 10.0.400 with no warnings. Running `dotnet Host.dll`:

```
GET  /__harbora/health           200  {"status":"ok","functions":3,"runtime":"csharp"}
GET  /hello?name=sadra           200  {"hello":"sadra","method":"GET"}
GET  /mail                       200  {"hello":"world","method":"GET"}
GET  /nightly                    404  {"error":"No function is routed here."}   ← cron fn, correctly not public
POST /__harbora/invoke/nightly   401  (no secret header)
POST /__harbora/invoke/nightly   204  (with the header)
stdout: [hello] http 200 2ms · [send-email] http 200 0ms · [nightly] Ran at 2026-08-18T04:12:32Z · [nightly] cron 204 1ms
```

**JavaScript** (node 22.13) and **Python** (3.13) behaved identically — health 200 with
`"runtime":"javascript"` / `"runtime":"python"`, both routes 200, unsigned invoke 401, signed invoke
204.

What this removes: the whole class of "the generated code does not compile", "the dispatcher does not
route", "the secret header does not match", "the wrapper breaks the user's file". What it does **not**
remove: `docker build` has still never run against these Dockerfiles on a real server. That is now a
narrow question — are `mcr.microsoft.com/dotnet/sdk:10.0`, `node:22-alpine` and `python:3.12-alpine`
pullable, and is there disk — and it is a server step, named as one in §9.

### (a) "Run now" cannot run anything. It saves.

**This is the defect.** `EditFunction.cshtml:20` opens a `<form action="/functions/{app}/save">`
that closes at `:152`. At `:146`, inside it, a second `<form action="/functions/{app}/{fn}/run">`
opens. HTML5 forbids nested forms: the parser sees a form element pointer that is already set and
**discards the inner start tag entirely**.

Parsed with AngleSharp 1.1.2 over the view's own markup (lines 20–152, Razor control flow stripped,
tag structure untouched):

```
forms parsed into the DOM: 1
  form action = /functions/X/save
button 'Save'    -> submits form with action: /functions/X/save
button 'Run now' -> submits form with action: /functions/X/save
textarea owner form action: /functions/X/save
```

So pressing **Run now** posts the editor's contents to `SaveFunction`, which validates, saves, marks
the app unpublished, and answers *"Saved. Press Publish to make it live."* (`FunctionsController.cs:341-343`).
No invocation is queued. No row appears. The recent-runs table is unchanged. The user is bounced to
the Details page having asked for one thing and been told about another.

The comment two lines above it, at `:145`, is the finest detail in the whole finding:

> *Its own form: nesting it inside the save form above would make one button submit the other.*

It names the exact failure and then commits it. This is the shape this programme keeps meeting — a
control that reports success for work it never did — and here it is not a check that lies, it is a
button that lies. It has been there since the feature's first commit, `590dc14`, and no test touches
the view.

### (b) The panel loses its route to every function app on every update

`FunctionInvoker.ResolveAddressAsync:162-177` reaches a local app at `http://{app.Slug}:{port}` when
`PrivateAddressState == Registered`. That name resolves only if the **panel container is joined to
that app's environment network**. That membership is created in exactly one place:

```csharp
// DeploymentPipeline.cs:375-379 — local-server branch only
foreach (var name in networks)
{
    await docker.ConnectNetworkAsync(_opt.ProxyContainerName, name, ct);
    await docker.ConnectNetworkAsync(_opt.PanelContainerName, name, ct);
}
```

`deploy/docker-compose.yml:204-205` declares `harbora-panel` on the `harbora` network **only**, and
says so at `:197-198` ("it joins tenant networks too"). An imperative `docker network connect` lives
on the container, not on the compose definition — and the documented upgrade recreates the container:
`deploy/RUNBOOK.md:243` is `git pull && cd deploy && docker compose up -d --build`.

**So after any panel update, every function app deployed before it is unreachable from the panel until
it is deployed again.** Cron and event calls all land in the catch at `FunctionInvoker.cs:140-148` and
record *"Could not reach the function app."* — once per schedule, for ever, with a message that reads
like the customer's container is down.

Why nothing else notices: there are only three callers that address a container by name from the
panel, and two of them are safe. `DeploymentPipeline.cs:1353` is the health probe, which runs *in the
same deploy, moments after the attach above*. `:1731` addresses the proxy, which compose does declare.
`FunctionInvoker.cs:169` is the only one that reads this path **between** deploys. Functions is
therefore the first feature to depend on a membership the platform has never had to keep.

This is unverifiable from here — this machine has no Docker and no credential for the live server —
so it is stated as a derivation from four cited facts rather than as a measurement, and confirming it
is the first task of the sub-project that owns it (§7 F2).

### (c) A rollback leaves the editor calling stale code "live"

`MarkFunctionsPublishedAsync:1076-1086` returns immediately unless `_codeReadAt` is set, and
`_codeReadAt` is set only inside `BuildFromInlineCodeAsync:1114`. A rollback **never rebuilds** — it
re-releases a prior image, deliberately (`DeploymentPipeline.cs:253-268`, ADR-006). So a rollback
leaves the flags exactly as they were: clean. The container is running the previous image's code; the
editor shows the current rows; the chip says **live** (`Details.cshtml:106`).

What that chip actually means is "not edited since the last successful publish". After a rollback that
sentence is true and useless.

### (d) The publish gate an operator will meet first

`PlatformFeatures.Functions` ships `FeatureState.Locked` (`PlatformFeatures.cs:51`), and `DbSeeder`
grants every feature `Enabled` **only** on the provider's own default plan (`DbSeeder.cs:237-256`) —
the comment there is accurate. New customer workspaces get the cheapest non-default plan
(`WorkspaceAccountService.cs:76-82`, with a comment explaining exactly why they must not inherit the
provider's). So on a stock install the owner's own workspace has Functions and a customer workspace
does not, by design.

Worth stating plainly because it is the cheapest possible explanation for «کار نمیکنه»: **if the
owner tested from a customer workspace, every Functions URL redirects to the locked page.** One edge
does exist — `WorkspaceAccountService.cs:79-82` yields `null` when no enabled non-default plan exists,
and `DbSeeder.cs:264-269` then adopts that workspace onto the *default* plan at the next boot, which
switches every feature on. Narrow, but it is the one path by which entitlement resolves the opposite
of what was intended.

### Three smaller ones, found while tracing

- **`ExecuteAsync`'s catch is too narrow.** `FunctionInvoker.cs:140` catches only
  `HttpRequestException` and `TaskCanceledException`. Anything else — a malformed URL, a
  `DbUpdateException` from `CompleteAsync` — escapes; the job is marked failed and the invocation row
  is left with `CompletedAt == null`, reading as *queued* for half an hour before the reaper
  mislabels it.
- **The reaper judges by the clock, not by the job.** `SettleAbandonedAsync:74-76` selects on
  `StartedAt < now - 30min` and never looks at the `Job` row. Because invocations of one function are
  serialised, a genuine backlog longer than thirty minutes is written off as *"The panel restarted
  before this call was made."* — and when the job finally runs, `ExecuteAsync:89` returns immediately
  because `CompletedAt` is now set. The call is dropped and the recorded reason is false.
- **A never-occurring schedule spins.** `CronSchedule.NextOccurrence` can return null (e.g.
  `0 0 30 2 *`, which `TryParse` accepts). `FunctionCronScheduler.cs:126` assigns it straight into the
  nullable `NextRunAt`, so the row re-enters the "first sight" branch and writes to the database every
  minute for ever. The unparseable case is handled carefully three lines above (`:111-121`); this one
  was not anticipated.

---

## 4. What "not clear" means, screen by screen

Walked as a customer, in order.

**`/functions` — the list.** `AppStatus` is selected into the row (`FunctionsController.cs:73`,
`FunctionViewModels.cs:8`) and rendered nowhere. A crashed function app and a healthy one are
identical cards. `RootDomain` is fetched with its own database round-trip (`:68`) and never used by
the view. The three chips — *never published* / *unpublished changes* / *live* — are about the code,
so the page answers "have you pressed Publish?" and never "is it running?".

**`/functions/{app}` — the app page.** `Status` and `EverPublished` are both in the view model
(`FunctionViewModels.cs:32-34`) and neither is rendered. The domain is shown as a live external link
(`Details.cshtml:23-26`) from the moment the app is created — before any deployment exists — so the
first thing on the page worth clicking is, for a new app, guaranteed to fail. There is no deploy
state, no last-deployed time, no link to the deployment that is live, no logs, no environment
variables, no delete.

**The per-function chip** (`Details.cshtml:96-107`) resolves in precedence order: *off*, then
*unpublished*, then *live*. A disabled function with unpublished edits shows only *off* — the state
the person is least likely to be surprised by hides the one they are. And *live* is computed without
reference to whether the app has ever been published or has since been rolled back (§3c).

**The same resource, two identities.** A function app is an ordinary `App`, so it also appears at
`/apps`. There, `Views/Apps/Index.cshtml:22-31` has no arm for `InlineCode` and falls through to
`_ => source.ToString()`, so the label is the literal enum name **`InlineCode`** — untranslated, in a
list where every other source has a written label — with the fallback `box` icon (`:33-41`).
`Views/Apps/_Shell.cshtml:28` prints the enum too. And there is **no link in either direction**:
nothing on the app's page says "this app's code is edited under Functions", and nothing under
Functions leads to the app.

**Which is where the documentation sends people.** `docs/functions.md:106-113` tells the reader to put
secrets in the app's Environment Variables. Reaching that page means leaving Functions, opening Apps,
and recognising your own function app by the word `InlineCode`.

**The history table shows platform-made calls only.** Rows are written in `QueueAsync:68-77` and
nowhere else, so an HTTP function serving a thousand requests has an empty *Recent runs* table. The
rule is written down (`docs/functions.md:100`) — but the panel says nothing, so the honest reading of
that empty table, from inside the panel, is "it has never run".

**The editor leaves after every save.** `SaveFunction:344` redirects to Details. And on a *failed*
save the view is re-rendered without `Runs` (`:324-325`), so the recent-runs table silently
disappears (`EditFunction.cshtml:42`) — a validation error also removes evidence.

**"Ran — the result is in this function's history."** (`FunctionsController.cs:391`) `QueueAsync`
queues; the worker picks it up later; the redirect renders the page before any of that. Even with
§3(a) fixed, this sentence claims completed work. The message on the failure branch is better than
the message on the success branch.

**A schedule saved a moment ago shows no next run.** `SaveFunction:332` clears `NextRunAt` on purpose,
and the scheduler fills it in on its next tick — up to a minute later, and up to 85 s after a restart.
`Details.cshtml:85-88` renders nothing in the meantime, so the state right after saving a cron
function is indistinguishable from a schedule that will never fire.

**A revoked entitlement stops the schedules in silence.** `FunctionInvoker.cs:51-58` returns without
writing a row — only an `Information` log. The customer sees a history that simply ends.

**And the code claims a feature that does not exist.** `FunctionStarters.cs:104`: *"The file extension
shown beside the editor, and used by the syntax highlighter."* There is no syntax highlighter anywhere
in this panel. §6.

---

## 5. What is genuinely missing, before it is decomposed

1. **A button that runs the function**, rather than one that saves it (§3a).
2. **A route from the panel to the container that survives an update** (§3b).
3. **Any statement of what is running.** Status, the live deployment, the last publish, and an honest
   answer after a rollback.
4. **Any link between the two halves of one resource.** Logs, environment variables, deployments,
   metrics and delete all exist for a function app and are all unreachable from Functions.
5. **A record of HTTP traffic**, or a sentence saying there will not be one.
6. **An editor.** One 22-row textarea, no line numbers, no highlighting, tab moves focus out of the
   field, and nothing protects unsaved work.
7. **A build error a person can act on.** The promise (`docs/functions.md:73-75`) is that a mistake is
   a compile error at publish time. What arrives is a line number **in a file the author has never
   seen**: for C#, the generator prepends a comment, a namespace and eight usings, so the author's
   line 1 is generated line 13 — a fixed offset of 12, measured from the generator's own output.
   JavaScript and Python are offset by 1.
8. **A way to try a request.** No body, no headers, no query, no method. A webhook handler cannot be
   exercised from the panel at all, and "Run now" — once it runs — sends an empty envelope.
9. **Any history of the code.** `FunctionDefinition.Code` is overwritten in place. There is no diff
   against what is published, no previous version, no restore. Rolling the *app* back rolls back the
   image and not the rows (§3c).
10. **Shared code.** One function is one file with no imports of its neighbours; two functions cannot
    share a helper. This is a limit of the model, not of the editor, and it is the most likely reason
    a real customer outgrows the feature.
11. **A test that renders any of this.** Sixty tests, zero of them through the web layer.

---

## 6. The editor

The owner's «شبیه ide … نه شبیه نوت پد» is the one complaint with a named solution, so it gets its own
section.

### What is there now

`EditFunction.cshtml:38-39`, in full:

```html
<textarea name="Code" rows="22" spellcheck="false" dir="ltr" required
          class="w-full resize-y rounded-xl border border-line bg-canvas p-4 font-mono text-[13px] leading-relaxed text-ink focus:border-accent focus:outline-none">@Model.Form.Code</textarea>
```

No `id`, no `data-*`, no JavaScript attached. The only script on the page (`:154-170`) shows and hides
the trigger's fields. Above it, a strip showing `{slug}.{extension}` and the runtime label
(`:28-33`) — the natural home for a toolbar. The comment at `:34-37` explains the choice: *"a syntax
highlighter is a megabyte of download for a page most customers open twice."* That was a reasonable
call and the measurements below say it was also a pessimistic one.

There is **no syntax highlighting anywhere in this panel** — no monaco, codemirror, prism, highlight.js
or shiki, in `src/`, in `wwwroot/`, or as a CDN tag. Every code surface is a plain `<pre>` or a
`font-mono` textarea. The nearest sibling is the volume file editor (`Views/AppData/Edit.cshtml:26-31`),
whose own comment states the house rule and is worth keeping in view:

> *Deliberately a plain textarea. A syntax-highlighting editor here would be a second parser between
> somebody and their own file, and the failure mode of that is a saved file that is not what was on
> the screen.*

That rule survives everything below. Whatever goes in must own the document and hand back exactly the
bytes that were on screen: no reformatting, no auto-fixing, no whitespace rewriting, no tab/space
conversion.

### What it would fit into — this is not a rewrite

The panel already ships Vite 6 and a hand-written island registry (`Scripts/main.ts:12-63`): a mount
point is an element with `id="key"` or `data-island="key"`, and props arrive as `data-*` attributes
read off `el.dataset`. Four islands exist. **One of them, `Terminal.vue`, already imports
`@xterm/xterm` and `@xterm/addon-fit`** — so a third-party editor-class component with its own CSS is
established practice here, and the panel already pays for one on every page.

Which is the second half of the point: `main.ts` imports all four islands **statically**, so xterm is
in the entry chunk of every page in the panel. A fifth island registered as
`() => import('./islands/CodeEditor.vue')` becomes a chunk Vite emits and loads **only on the editor
page** — and because a function app has exactly one runtime, only that runtime's grammar need be
fetched. This is an edit to one registry entry plus a new `.vue` file. It is not a rewrite, and it is
also the moment to move xterm behind the same treatment.

Constraints that are real: `npm ci` against a committed `package-lock.json` in CI
(`.github/workflows/ci.yml:181-193`), so a new dependency must land in the lockfile; nothing is
vendored; no runtime CDN is used anywhere and the service worker caches only local build assets
(`wwwroot/sw.js`).

### What "like an IDE" should mean here, ranked

Ranked by what *this* feature needs — a person writing ten to fifty lines, two or three times, in a
language they already know — not by what an IDE has.

| # | What | Why it ranks here | Cost |
|---|---|---|---|
| **1** | **Not losing work** | The only failure that is total. No dirty tracking, no `beforeunload`, no draft; Cancel is a plain link that discards silently; and a successful save *navigates away from the editor*. Every other item on this list is an improvement; this one is a loss | ~40 lines of JS + one controller change. **0 kB** |
| **2** | **A build error that names the author's line** | The feature's stated contract is "a mistake is a compile error at publish". Today the compiler names a line in a generated file, offset by 12 for C#. Subtracting a known constant and linking the deploy log's error to the editor is the difference between the promise and the experience | Server-side; no bundle cost |
| **3** | **Line numbers, real Tab, bracket matching, indent-on-input** | Tab currently moves focus out of the textarea — the single most Notepad-like thing about it. Line numbers are what makes item 2 usable | included below |
| **4** | **Syntax highlighting for the app's one language** | What the owner literally asked for, and what makes the page *look* like a place to write code. Ranked below 3 because a mis-typed brace costs more than an uncoloured keyword | measured below |
| **5** | **Run against what is on screen, with a request you compose** | Method, path, query, headers, body. Today there is no way to exercise a webhook handler at all, and "Run now" sends an empty envelope against *published* code. Needs a decision (§8 Q4) because it changes what "run" means | Medium; server work, not editor work |
| **6** | **More than one file** | Two functions cannot share a helper. This is the model's limit, and lifting it changes the generator and the schema | Large; §7 F7 |
| **7** | **Autocomplete / go-to-definition** | Wants a language server per runtime, running somewhere, per session. Out of proportion to a fifty-line function | Out of reach; say so |

### Measured costs

Built with the panel's own Vite 6 and measured on this machine, not estimated.

| Option | raw | gzip |
|---|---|---|
| **The panel's entry chunk today** (4 islands incl. xterm) | 466.2 kB | **125.6 kB** |
| Prism core + C#/JS/Python grammars — highlight-only overlay | 27.3 kB | **10.0 kB** |
| CodeMirror 6, trimmed, **no** language | 271.4 kB | 86.5 kB |
| CodeMirror 6, trimmed, **+ C#** (legacy stream mode) | 300.9 kB | **96.3 kB** |
| CodeMirror 6, trimmed, **+ Python** | 349.7 kB | **116.6 kB** |
| CodeMirror 6, trimmed, **+ JavaScript** | 388.5 kB | **129.8 kB** |
| CodeMirror 6, `basicSetup`, all three languages at once | 571.3 kB | 195.6 kB |
| Monaco | not measured — it needs web workers and a separate asset pipeline the panel has no plumbing for | — |

"Trimmed" is line numbers, active-line highlight, history, bracket matching, indent-on-input, the
default highlight style and a tab keymap — no autocomplete, no linting, no search panel. That
configuration delivers items 3 and 4 together.

Three facts follow, and they change the original judgement:

- The "megabyte of download" in the comment at `EditFunction.cshtml:36` is **96–130 kB gzipped**, and
  only on that page, if the island is a lazy chunk carrying one grammar.
- **C# is the cheapest of the three**, because CodeMirror 6 has no first-class C# grammar and it is
  driven by the legacy stream mode in `@codemirror/legacy-modes`. Highlighting is therefore good but
  not Lezer-quality for C# — worth knowing before promising.
- Prism at 10 kB gzipped buys item 4 alone, painted under a transparent textarea. It is genuinely
  cheap and genuinely fiddly: scroll sync, font-metric drift and an RTL page make the two layers
  disagree, and when they disagree the caret is in the wrong place — which is the failure the volume
  editor's comment warns about, arriving by a different door.

---

## 7. Decomposition

Seven sub-projects, each independently mergeable, each worth shipping alone.

| | Sub-project | What it delivers | Schema |
|---|---|---|---|
| **F1** | **The button does what it says** | Run now runs; the message describes what happened; the first web-layer tests | none |
| **F2** | **The panel keeps its way in** | The network membership survives an update; the invoker cannot leave a row uncompleted; the reaper consults the job | none |
| **F3** | **The pages say what is running** | Status, live deployment, never-published, rolled-back, HTTP-not-recorded, the address link that waits | none |
| **F4** | **One app, one place** | Cross-links both ways, a written source label, logs / env / delete reachable from Functions | none |
| **F5** | **The editor stops being Notepad** | Draft safety, staying on the page, an island with line numbers, indentation and highlighting, build errors on the author's line | none |
| **F6** | **A publish proves itself on a server** | The first real `docker build` of each runtime, written up as the server step it is | none |
| **F7** | **More complete** | A request you compose; code history and a diff against what is published; shared code | two tables |

### The order, and why

**F1 first, and it is not close.** It is the smallest change in the list — moving one `</form>` — and
it is the one the owner has almost certainly hit. Everything else is a feature that is missing; this
is a feature that is present, visible, and wrong. It also carries the first test that renders the
editor, which is the gap that let it ship.

**F2 second**, because it is the only item that takes the whole feature down *after* it has been
working, on a routine operation, with an error message that blames the customer's container. Its first
task is confirmation on a real server (§3b is a derivation, not a measurement), and its first commit
should be the confirmation, not the fix.

**F3 third.** It is pure surfacing and touches no behaviour, so it can go at any time — but it is
placed here because after F1 and F2 the honest question becomes "is it working now?", and today no
page answers that. The rolled-back case (§3c) belongs here rather than in F1 because it is a *meaning*
bug in a chip, not a broken control.

**F4 and F5 can go in either order**, and are the two halves of «کاملترش کن». F4 is a handful of links
and one switch arm — the highest value-to-effort item in the list, and the one that stops the
documentation sending people on a hunt. F5 is the owner's headline request and the largest UI change;
it wants §8 Q2 answered first.

**F6 whenever a server is available.** It is not blocked by anything and blocks nothing, but until it
has run, "it works" means "it works everywhere I could test", and this document should not be read as
saying more than that.

**F7 last**, because every item in it is a genuine addition rather than a repair, two of the three
need schema, and one of them (§8 Q4) changes what a word on the screen means.

### F1 — the button does what it says

Move the Run-now form out of the save form — the aside is inside it, so either the form moves below
`</form>` or the button gains `form="…"` / `formaction`. Then correct the two messages: `RunNow`'s
success text (`FunctionsController.cs:391`) must describe a call that has been *queued*, in the same
register the failure branch already uses.

**The test is the point of this sub-project.** Assert on the parsed DOM — that the Run-now button's
owner form posts to `…/run` — not on the presence of a string in the markup, because the markup was
always right and the parse was always wrong. Then a controller test that pressing it produces a
`FunctionInvocation` row.

**Not in F1:** running the editor's buffer instead of published code. That is §8 Q4 and F7.

### F2 — the panel keeps its way in

First, **confirm it on the server**: `docker inspect harbora-panel` for its networks, before and after
a `docker compose up -d --build`. If the memberships survive, this sub-project is one paragraph in the
runbook instead. If they do not, §8 Q1 chooses the fix.

Then two corrections that stand on their own: widen `FunctionInvoker.ExecuteAsync`'s catch
(`:140`) so no path can leave a row with a null `CompletedAt`, and make `SettleAbandonedAsync`
(`:67-90`) consult the `Job` row rather than only the clock, so a real backlog is not written off with
a false reason. Both want a test that asserts on the row that was written, not on the method
returning.

**Also here, cheaply:** `FunctionCronScheduler.cs:126` handling a null `NextOccurrence` the way `:111-121`
already handles an unparseable expression, and `JobExecutionPolicy` gaining an explicit
`JobKind.FunctionInvoke` arm instead of falling to the one-hour default.

**Not in F2:** changing how apps are addressed generally. If Q1 goes to a declarative membership, that
is a compose and networking change for the platform, and it should be sized as one.

### F3 — the pages say what is running

The status the controller already fetches, rendered. A "never deployed" state that suppresses the
address link until there is something behind it. A line on the app page naming the live deployment and
when it went out. A chip whose *live* means live: after a rollback, the rows and the image disagree,
and the page has to say so rather than pick the flattering reading. And one sentence under the recent-
runs table explaining that HTTP calls are not listed there — or §8 Q5 says they should be.

**Not in F3:** a metrics chart for functions. `MetricsChart.vue` exists and the app's own Usage page
already renders it; F4's link is the cheaper answer.

### F4 — one app, one place

An arm for `InlineCode` in `Views/Apps/Index.cshtml:22-41` with a written bilingual label and an icon
that is not the fallback. A banner on the app's page linking to its Functions editor. On the function
app page, links to logs, environment variables, deployments and delete — the four things
`docs/functions.md` assumes are at hand and none of which is.

**Not in F4:** duplicating those pages under `/functions`. The point is that a function app *is* an
app; the fix is a link, not a second implementation.

### F5 — the editor stops being Notepad

**In two halves, and the first half ships alone.**

*The first half costs nothing and removes the only total failure:* dirty tracking, a `beforeunload`
guard, a Cancel that asks, and — the part that matters most — `SaveFunction` returning to the editor
instead of redirecting to Details, so saving is not also leaving. Editing three functions in a row
currently means three round trips through a list page.

*The second half is the island*, per §8 Q2: a new `CodeEditor.vue` registered as a dynamic import in
`main.ts`, receiving `data-runtime` and the code, owning the document and writing it back to a hidden
input on submit, with a lazily fetched grammar. Alongside it, the server-side half of the promise:
subtract the generator's known header offset (12 for C#, 1 for the others — pinned by a test against
`FunctionProject.Generate`'s own output, so it cannot drift) and show the failed build's compiler
errors against the author's own line numbers.

**Not in F5:** autocomplete, go-to-definition, or a language server. Named in §6 as out of reach so
that nobody has to rediscover why.

### F6 — a publish proves itself on a server

One publish per runtime on a real host, recorded: the build log, the health response, an HTTP call
through the domain, and a cron function firing on its own schedule. The generator, the routing and
the secret are already proven (§3); what this proves is `docker build`, the base images and the
pipeline's plumbing.

**Not in F6:** a CI job that runs Docker. The nightly live-host lane exists for this class of check
and this belongs to it, not to the pull-request lane.

### F7 — more complete

Three additions, each independently arguable:

- **A request you compose** — method, path, query, headers, body — posted through the invoke door,
  with the response shown. This is what turns the editor into a place you can iterate in.
- **Code history**: a `FunctionRevision` row per save, a diff against what is published, and a
  restore. It also gives §3(c) an answer with teeth — after a rollback the panel can say *which*
  revision is running.
- **Shared code**: a per-app file that every function may import. Generator work, one table, and a
  new class of build error to explain.

**Not in F7:** package installs. `docs/functions.md:121-123` refuses them for stated reasons, and
nothing here changes them.

---

## 8. The decisions that are the owner's

Five, kept short. Each changes what gets built, not merely how.

**Q1 — How does the panel keep its route to a function app?** (F2)
*(a) Re-attach on boot* — a startup step that joins the panel to every environment network with a
workload on it. Smallest, self-healing, uses machinery that already exists at
`DeploymentPipeline.cs:375-379`. Cost: it is a boot-time loop over networks, which grows with the
fleet, and it fixes the symptom on a schedule rather than the shape.
*(b) Attach lazily, in `ResolveAddressAsync`* — connect on demand when a call is about to be made.
Cost: an invocation path that mutates Docker state, and the first call after an update still pays for
it.
*(c) Declare the memberships in compose.* Cost: tenant networks are created dynamically; compose
cannot know their names, so this means a generated compose fragment — a new moving part in the deploy.
*(d) Stop using container names for function apps* — publish a host port and address it like a remote
node, which `ResolveAddressAsync:174-176` already does. Cost: a port per function app, and it gives up
the isolation the private name buys.

**Q2 — Which editor?** (F5)
*(a) Textarea plus line numbers and a real Tab, hand-written.* **0 kB**, delivers item 3 of §6's
ranking and none of item 4. Cost: it will still not look like an IDE, which is what was asked for.
*(b) Prism overlay.* **10 kB gzipped**, delivers colour and nothing else. Cost: two layers that must
agree about scroll position and font metrics on an RTL page, and when they do not, the caret is
visibly wrong — the exact failure `Views/AppData/Edit.cshtml:26-28` warns against.
*(c) CodeMirror 6, trimmed, lazily loaded, one grammar per page.* **96–130 kB gzipped on that page
only**; delivers items 3 and 4 together and leaves room for item 2's error markers. Cost: a new
dependency in the lockfile, a new island, and C# highlighting comes from a legacy stream mode rather
than a real grammar.
*(d) Monaco.* The full IDE feel. Cost: web workers and an asset pipeline the panel does not have, for
a page most customers open twice — and it is the one option that would genuinely deserve the
"megabyte" in the current comment.

**Q3 — Does the code get a history?** (F7)
*(a) No.* Nothing to build. Cost: a save is irreversible, and after a rollback nothing can say which
version is running.
*(b) A revision row per save, with a diff and a restore.* One table, and it answers §3(c) properly.
Cost: unbounded growth on a table holding customer source, so it needs its own retention rule and a
say in backups.
*(c) Keep only the last published revision*, purely so "what changed since publish" can be shown.
Cost: half the value for most of the schema.

**Q4 — Does "Run now" run the editor's buffer or the published code?** (F1/F7)
*(a) Published code, as today.* Honest, already documented (`docs/functions.md:96-98`), and the
existing copy explains it. Cost: it is not what "run" means in an IDE, and it cannot be used to
iterate.
*(b) The buffer* — save-then-publish-then-call. Cost: pressing "run" would rebuild an image and
re-release the app, which is minutes, not seconds, and it is a deployment nobody asked for.
*(c) Both, as two buttons:* "Run the published version" and "Save, publish and run". Cost: two
controls where the person expected one, and the second is a deploy with an innocuous label.

**Q5 — Are HTTP invocations recorded?** (F3/F7)
*(a) No — say so on the page.* One sentence. Cost: the busiest functions have the emptiest history.
*(b) Yes — the host reports each call back through the invoke door.* Complete history. Cost: a write
per request on the hot path, a table that grows with traffic rather than with schedules, and a
customer's HTTP volume becoming the panel's database load.
*(c) Counters only* — calls, failures and last-seen per function, rolled up. Cost: it answers "is it
still firing?" without answering "what happened at 03:14".

---

## 9. Testing

Each sub-project states its own; five rules apply across all of them.

- **Assert on the parsed DOM, not on the markup.** F1's defect is invisible to any assertion over the
  Razor output — the `<form>` tag *is* in the file. The test has to ask a parser which form the button
  belongs to. This is a new idiom for this repo and it is the whole reason F1 exists.
- **Assert on the row that was written, not on the method returning.** A fake invoker that returns
  success passes on both sides of every fix in F2. The assertions are: an invocation exists after
  Run now · a failed execute leaves `CompletedAt` set · a backlogged call is not settled as abandoned.
- **Pin the generated wrapper's offset to the generator, not to a constant in the view.** F5's error
  mapping is a subtraction, and the number is 12 today because `CSharpFunctionFile` writes twelve
  lines before the user's. A test that asserts the offset by counting the generator's own output
  cannot drift; a test that asserts `12` will be quietly wrong the first time a using is added.
- **Assert on `data-` attributes and route fragments, not on visible text.** The panel renders
  **Persian by default**, and every string in these views has an `isFa` branch.
  `Views/Apps/Details.cshtml:260-262`'s `data-spec-size` / `data-spec-port` / `data-spec-container`
  are the established model, and the Functions views carry no such hook today.
- **Cover what already ships before extending it.** There is no web-layer test of Functions at all.
  F1's first commit should be the test that fails, and F3/F4/F5 should each add the rendering test
  they need before they change a view.

Behaviours worth naming now: pressing Run now queues an invocation and does not save · a save that
fails validation keeps the code *and* the recent-runs table · a function app that has never deployed
does not offer its address as a link · a rolled-back app does not describe its rows as live · a
generated C# error at file line 17 is reported to the author as line 5 · leaving the editor with
unsaved changes asks first · a function app appears in the Apps list with a written label · an
invocation whose execute threw an unexpected exception is completed, not left queued.

---

## 10. What this is not

**Security review**, which is out of scope by the owner's standing instruction — what is being settled
here is whether a button does what it says, whether the panel can still reach the container it
deployed, and whether the page describes the thing that is running.

Also not: scale-to-zero or per-request billing, refused with reasons in
`2026-08-16-feature-entitlements-and-functions-design.md:149-152` and unchanged · package installs,
refused at `docs/functions.md:121-123` · a fourth runtime · running untrusted code, which
`docs/functions.md:124-126` is explicit about · rotating the invoke secret, deliberately not offered
(`FunctionAppService.cs:33-38`) · a second sidebar entry or a duplicate app-management surface under
Functions, which F4 exists specifically to avoid · a language server, priced out in §6 · and the
entitlement model itself, which was settled on 2026-08-16 and is working — the one edge worth watching
is recorded in §3(d) and belongs to whoever owns entitlements, not to this phase.
