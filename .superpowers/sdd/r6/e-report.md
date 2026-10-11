# E — The chat pane (phase 3): report

Status: **DONE_WITH_CONCERNS**. Everything in the brief is built and tested. The pane has not run
against the real server, because AssistantController (agent D) is not on this branch. The concerns
are listed at the end.

Branch `r6/chat-pane`, worktree `../Harbora-r6-e`, based on `688cb40`. Commits are in the owner's name
alone, with no trailer:

- `fd224bf` Assistant pane: an end-side drawer, an entitlement-gated toggle and a streaming chat island
- `12b7796` Assistant pane: direction by word count, a three-step type scale, and a preview harness
- `addde63` Assistant pane tests: every case in a workspace of its own
- (this report)

## Test results

Final full run, after the last code commit (`addde63`), using the CONSTRAINTS commands with
`MSBUILDDISABLENODEREUSE=1`, `-p:UseSharedCompilation=false` and log `/tmp/test-r6-chat-pane.log`:

```
BUILD_EXIT=0
    28 Warning(s)      (48× NU1900 + 8× NU1903 lines — the same package-feed warnings as the
    0 Error(s)          baseline build before any change; no CS/RZ/CA warnings)
EXIT=0
Passed!  - Failed:     0, Passed:   500, Skipped:    17, Total:   517 - Harbora.NodeAgent.Tests.dll
Passed!  - Failed:     0, Passed:    15, Skipped:     0, Total:    15 - Harbora.NodeIngress.Tests.dll
Passed!  - Failed:     0, Passed:  6438, Skipped:     0, Total:  6438 - Harbora.Tests.dll
grep -c "Harbora.Tests.dll" → 2      grep -c MSB3030 → 0
```

That makes **Harbora.Tests 6438**: the baseline of 6429 plus the 9 new `AssistantPaneHttpTests`.

Node tests, `node --test src/Harbora.Web/Scripts/tests/*.test.mjs`: **104 pass, 0 fail**. 92 of them
are new and 12 were already there (deployment-logs 4, mobile-sidebar 8). By file:

| File | Tests |
|---|---|
| `assistant-stream.test.mjs` | 14 |
| `assistant-api.test.mjs` | 22 |
| `assistant-timeline.test.mjs` | 14 |
| `assistant-plan.test.mjs` | 15 |
| `assistant-format.test.mjs` | 18 |
| `assistant-drawer.test.mjs` | 9 |

The bundle built with `npm ci` (exit 0) and then `npm run build` (exit 0, `✓ built in 5.40s`), run
on its own and never alongside a dotnet command. The new async chunk is
`AssistantPanel-*.js 42.28 kB │ gzip: 14.48 kB`. `main-*.js` stays at 476 kB, and only the small
drawer controller was added to it. `wwwroot/build/manifest.json` still contains `"Scripts/main.ts"`.

The first full run caught a real test bug: **3 failures that only appeared in the full run**.
`AppAddressHttpTests` moves the shared fixture workspace onto a plan of its own, where the assistant
is Locked by default, so my three page-context tests found no drawer. They had passed when run alone.
I fixed this in `addde63`: every pane test now seeds its own workspace and states the entitlement it
needs. The run above came after that fix.

## Files

New:
- `src/Harbora.Web/Infrastructure/AssistantPageContext.cs`: `Declare(viewData, appId, serviceId,
  projectId, environmentId)` stores an `AgentPageContext`. `Attribute(viewData)` writes the whole
  `data-assistant-context="…"` attribute, or nothing at all. Razor's conditional attributes do not
  apply to `data-*` names: `data-x="@null"` renders `data-x=""`. A test caught that.
- `src/Harbora.Web/Scripts/assistantDrawer.js`: opens and closes the drawer, wires the toggle,
  Ctrl/Cmd+J and Escape, and handles localStorage (`harbora-assistant-open` and
  `harbora-assistant-conversation`). It loads the island the first time the drawer opens.
- `src/Harbora.Web/Scripts/islands/`:
  - `AssistantPanel.vue` (the island) and its parts: `AssistantPlanCard.vue`, `AssistantResultCard.vue`,
    `AssistantText.vue` and `assistantIcon.ts`. The icon helper draws lucide glyphs with `h()`, because
    `createIcons()` swaps DOM nodes that Vue still owns.
  - The logic lives in plain JS so `node --test` can import it without a build:
    - `assistantStream.js`: the SSE parser and stream reader.
    - `assistantApi.js`: HTTP calls, error wording and page context.
    - `assistantTimeline.js`: the conversation reducer.
    - `assistantPlan.js`: the plan and result card rules.
    - `assistantFormat.js`: message formatting, `safeHref` and `textDirection`.
- `src/Harbora.Web/Scripts/tests/assistant-*.test.mjs`: the six test files above.
- `src/Harbora.Web/Scripts/tests/assistant-panel.preview.html`: a browser harness, the same idea as
  `deployment-logs.preview.html`. Usage notes are in its header comment.
- `tests/Harbora.Tests/Http/AssistantPaneHttpTests.cs`

Changed:
- `Views/Shared/_Layout.cshtml`:
  - injects `FeatureView` and `IAntiforgery`;
  - a new positioning box (`relative flex min-h-0 flex-1 flex-col overflow-hidden`) wraps
    `#content-scroll`, and `#content-scroll` itself is unchanged;
  - the drawer `<aside id="assistant-panel" class="assistant-drawer">` sits beside `#content-scroll`,
    not inside it. It carries `data-csrf`, `data-lang` and the optional context, and is rendered only
    when the feature is enabled.
- `Views/Shared/Design/_Topbar.cshtml`: the toggle sits right after the command-palette trigger.
- `Views/Apps/Details.cshtml` and `Views/Databases/Details.cshtml`: one `AssistantPageContext.Declare`
  line each, and nothing else.
- `Scripts/main.ts`: imports `initAssistantDrawer` and adds an `'assistant-panel'` entry to the
  `islands` registry. That entry does `import('./islands/AssistantPanel.vue')` on first open and passes
  `csrf`, `lang`, `context`, `autofocus` and `onClose` as props. The lucide import list is unchanged:
  `Sparkles` and `Lock` were already there.
- `Scripts/app.css`: the `.assistant-drawer` rules, written as plain CSS outside `@layer` so Tailwind
  cannot purge them:
  - logical `inset-inline-end`, so the drawer is on the left in RTL and the right in LTR;
  - 420 px wide, full height under the topbar;
  - `z-index: 15`. Pages stop at z-10 and the topbar is z-20, so the account and "new resource" menus
    still open over the drawer;
  - when closed it is `visibility: hidden` as well as slid off-screen, so nothing inside can take focus;
  - `position: fixed; inset: 0; z-index: 60` below 640 px, i.e. full screen on a phone;
  - honours reduced motion.

Not touched: `src/Harbora.Infrastructure/Agent/*`, any controller, settings, and the contract shapes.

## How the toggle evaluates the entitlement

It uses the same mechanism as the sidebar's `Feature:` items: `Harbora.Web.Infrastructure.FeatureView`,
the per-request wrapper over `IFeatureGate.EvaluateAllAsync`, called as
`GetAsync(PlatformFeatures.Assistant)`. The topbar and the layout both ask it. It memoises, so that is
one query per request, shared with the sidebar.

- **Enabled**: a `<button data-assistant-toggle data-assistant-state="enabled"
  aria-controls="assistant-panel">` with the sparkles icon. `aria-expanded` follows the drawer.
- **Locked**: an `<a href="/features/assistant" data-assistant-state="locked">`, greyed, with a lock
  glyph. It has no `data-assistant-toggle`, so it cannot open anything.
- **Hidden**: nothing is drawn.
- **Signed out**: nothing is drawn. `FeatureView` answers Locked when there is no workspace, which
  would advertise the feature on an error page. The layout applies the same check before drawing the
  drawer.
- **Drawer mount**: rendered only when Enabled.

The server-side check stays D's `[RequireFeature]`. The pane sends `X-Requested-With: XMLHttpRequest`
so that filter answers with a JSON 403 rather than a redirect to HTML. The pane shows that 403 as "not
enabled for this workspace".

## Event handling

`POST /assistant/conversations/{id|new}/messages`, with body
`{ text, page: { path, appId, serviceId, projectId, environmentId } }`, sent with `X-CSRF-TOKEN`,
`Accept: text/event-stream` and `redirect: 'manual'`. The body is read with `ReadableStream.getReader()`
and a streaming `TextDecoder`.

The parser follows the WHATWG event-stream rules:
- CRLF, LF and lone CR line endings, including a CR at the end of one read whose LF opens the next;
- multi-line `data:` fields;
- exactly one leading space stripped from a value;
- comments are skipped, and frames without data are not dispatched;
- a frame cut off at end of stream is discarded rather than believed;
- UTF-8 split mid-character between reads is decoded correctly.

Events:

| Event | What the pane does |
|---|---|
| `started` | Keeps and persists the conversation id. |
| `tool` | `running` becomes `done` or `failed` on the same line (spinner, then check or cross). It is matched by tool name within the current turn; a second call of the same tool gets its own line. A state the contract does not name is shown as `unknown`, never as a check. |
| `message` | Appends a message. |
| `plan` | Shows a card, or updates it if a plan with the same id arrives again. |
| `executed` | Shows a result card. |
| `error` | Shows an error line. |
| `done` | Updates `remainingToday`, where null means unlimited. |
| anything else | Ignored. |

Failures:
- A known event whose data is not a JSON object becomes an error line rather than being dropped.
- Network failure, a dropped connection, a non-2xx status, a redirect (signed out), a 200 that is not
  an event stream, and a stream that ends without `done` all show as error lines in both languages.
- After any of those, a still-running tool line becomes "no result arrived". The spinner stops and the
  line is not shown as done.
- An aborted send is not reported as a failure.

Plan card:
- kind badges: safe «کم‌خطر», dangerous «پرخطر», destructive «حذف‌کننده»;
- titles, details and warnings;
- "Don't ask again for this" / «دیگر برای این کار نپرس», only on `safe` steps that are `rememberable`
  and not `autoApproved`. It is keyed by tool name, so two steps of one tool share one answer.
- a typed-phrase field for each destructive step. The phrase must match exactly after trimming and is
  case-sensitive, like the server. A destructive step that arrives without a phrase can never be
  confirmed. A mismatch shows an inline error and sends no request.
- Run posts `AgentApproveRequest`. The body was checked in the browser:
  `{"confirmations":{"3":"old-db"},"rememberTools":["create_database"]}`.
- Cancel posts `/reject`.
- Status headings for each plan state. A 15-second clock turns a pending plan into "expired" while it
  is on screen.

Result card: done, failed or skipped per step, with the step's title taken from the plan when known.
A step shows as done only when its `state === "done"`. Links are followed only for panel paths and
http(s) URLs; anything else (`javascript:`, `//host`, `/\host`) is shown as plain text.

Messages are formatted into a tree of plain-text nodes: paragraphs, line breaks, `inline code`, fenced
blocks and `-`, `*` or `•` lists. They are rendered with `{{ }}` only. A test asserts that no
`Assistant*.vue` or `assistant*.js|ts` file contains `v-html`, `innerHTML =` or `insertAdjacentHTML`.

## Page context

`Views/Apps/Details.cshtml` declares `appId` and `environmentId`. `Views/Databases/Details.cshtml`
declares `serviceId`. `path` is always `location.pathname`, filled in by the pane. `projectId` is
never sent: neither controller loads it, the database view model only has names, and the brief
forbids touching the controllers. The server can work it out from the app or service id. Only
well-formed GUIDs are sent; the rest become `null`, so a stray value cannot break model binding of
`Guid?` for the whole message.

## Verified in a browser, against a faked server

I served `assistant-panel.preview.html` with `npx vite --port 5183` from my worktree and checked it in
the built-in browser pane. It runs the island's real code. fetch is faked with the exact
AgentViews.cs shapes and a scripted SSE stream whose frames are split in two. Checked:

- RTL drawer on the left and LTR on the right;
- full screen at 375 px; dark mode;
- history list (dates in the Persian calendar), loading a stored conversation with its pending plan;
- streaming tool lines (running, then done or failed), the message, and the remaining count dropping
  from 287 to 286;
- the stream cut before `done`: an error line and an "interrupted" tool line;
- unavailable status: a banner and a disabled composer;
- the 403 `feature_not_enabled` case;
- a wrong phrase giving an inline error with no request, then a correct phrase and approve giving a
  result card with done, done, failed and skipped;
- Cancel giving "Cancelled — nothing ran"; an expired plan;
- `<b>` in a message shown as literal text;
- Ctrl+J opening the drawer with the composer focused, Escape closing it with focus back on the
  toggle, and Ctrl+J closing it the same way.

The dev server was stopped afterwards.

Two fixes came out of that pass and are in `12b7796`:

1. **Text direction.** `dir="auto"` laid a Persian sentence that starts with a resource name
   (`shop-db ساخته شد…`) out left to right. I replaced it with `textDirection()`, which counts words of
   each script, ignores inline code, and breaks ties on the first word. This deliberately departs from
   the brief's literal `dir="auto"`. It is tested, and it falls back to `auto` for text with no
   letters.
2. **Type scale and the status line.** The type scale is now three steps: 11, 14 and 18 px, following
   the coordinator's design note. The status line had let a long model id truncate away the
   remaining-requests count; they are now two lines.

## Not verified without D's server

- End-to-end: the real endpoints, their status codes, the SSE flushing, the camelCase serialisation,
  and the real `AgentStatusView`, `AgentConversationView` and `AgentPlanResultView` payloads.
- The body of a refused approve (wrong phrase, expired, already executed). The contract does not
  define it. The pane accepts `{messageEn, messageFa}`, `{error, errorFa}`, `{errorEn, errorFa}` and
  ProblemDetails. Without any of those, it shows bilingual text for 403, 404, 409, other 4xx and 5xx.
- Whether D's approve returns 409 or 400 for a plan that is no longer pending. Either way the pane
  shows the server's text or the fallback.
- Ctrl+J in desktop Chrome and Firefox, where it is also the Downloads shortcut. `preventDefault` was
  seen to work in the desktop app's browser pane; whether a full browser lets the page take the key
  first is unverified.

## Concerns and contract observations (shapes not changed)

1. **Tool events carry no call id**, so two calls of the same tool can be told apart only by order.
   The pane pairs `running` with its outcome by tool name within a turn, which assumes the tools run
   one after another.
2. **Stored tool messages have no ok or failed flag**, so in a reloaded conversation they are drawn as
   neutral "Tool result: name" lines, never as a check.
3. **A reloaded conversation does not show the result card.** `AgentConversationView` has only
   `PendingPlan`, so an executed plan's result card does not come back after a reload; the results
   appear only as tool messages.
4. **`AgentPlanStepView.Index` base is unspecified.** The card shows each step's position in the list,
   and keys `confirmations` by the `index` value exactly as the server sent it.
5. **Only the two Details pages declare context**, as the brief says. Declaring it in the app and
   database `_Shell.cshtml` instead would cover every tab (usage, volumes, deployments), at the cost of
   editing views agents A and B work near.
6. **The preview harness is committed** (`assistant-panel.preview.html`). The coordinator said to keep
   it out of the commits if it was only a scratch harness. It is not: it mounts the real island
   against the contract's shapes, it was how the pane was checked, and it is how anyone can look at the
   pane before the server lands. The design check had flagged its own placeholder page markup; the
   type fix went into the pane itself. It can be dropped without affecting anything else.
7. **The wrapper around `#content-scroll` changes the layout's DOM**, adding one positioned box with
   `overflow-hidden`. The full suite is green with it. Content that overflowed `#content-scroll` was
   already clipped by its own `overflow-y-auto`, so nothing should have relied on the old structure.

## Polish after review

The review approved the pane and it landed on master (`a6c4b48`). This pass was done on `r6/chat-pane`
in `../Harbora-r6-e`, starting from master. It touches only the pane, its client modules and their
tests. Commit `2883708` "Assistant pane: polish after review", plus this report section.

### What changed, item by item

1. **Phone restore** (`assistantDrawer.js`). Below 640 px the drawer is full screen.
   - `restore()` no longer reopens it there. The remembered "open" is kept, so a wide screen still
     restores it.
   - A plain left click on a link inside the drawer calls `followedLink()`. On a phone that closes the
     drawer and saves "closed". On a wide screen it does nothing. Ctrl, Shift, Alt or Meta clicks,
     middle clicks and `target=_blank` links are left alone.
   - The check uses `matchMedia('(max-width: 639px)')`, the same breakpoint as the CSS.
   - `initAssistantDrawer` now takes `{ document, storage, matchMedia }` so the wiring can be tested.
2. **Spinner that never stops** (`assistantTimeline.js`, `AssistantPanel.vue`).
   - The `done` event now settles the turn: a tool line still `running` becomes `interrupted`, drawn
     neutral as "no result arrived" and never as a check.
   - `send()` also calls `settleTurn` in a `finally`, whatever ended the turn.
3. **Expiry is the server's word only** (`assistantPlan.js`).
   - `planIsExpired` is true only for `status === "expired"`, and `planIsActionable` only for
     `"pending"`. The 15-second client clock is gone.
   - The card shows the server's `expiresAt` as a time of day ("valid until 16:05"), as information
     only; nothing enforces it.
   - A refused approve or reject goes through `applyApproveRefusal`. The card shows the server's
     `messageEn`/`messageFa`, marks the step whose `stepIndex` it names (danger ring,
     `data-step-refused`, `aria-describedby` pointing at the message), and takes the `status` it
     reports, so `"expired"` ends the card.
   - `AssistantRequestError` now carries `stepIndex`, only if it is an integer, and `planStatus`, only
     if it is a string the contract names. A numeric ProblemDetails `status` is ignored.
   - A bare machine code in `error` ("plan_expired") is never shown to a person; the fallback wording
     is shown instead.
4. **Pane faults, cancel, abort** (`assistantApi.js`, `assistantStream.js`).
   - An exception thrown in the pane's own event handler is wrapped as `PaneFault`. Reading stops, the
     pane reports "the pane failed while showing the answer — a fault in the panel, not in the
     assistant or your connection", and the cause goes to `console.error`. The result is
     `{ completed: false, paneFault: true }`.
   - `readEventStream` now **cancels** the reader on any error, then releases it.
   - The abort signal is wired through a new `createTurnTracker()`. Starting a new conversation,
     opening another one from the list, or unmounting the island aborts the turn in flight.
   - Events that still arrive for an abandoned turn are dropped by `turn.isCurrent()`, so they cannot
     land in the conversation now on screen.
   - The "New" button and the conversation list are therefore no longer disabled while a turn runs.
   - **Closing the drawer deliberately does not abort.** The island stays mounted and the answer is
     there on reopen; throwing the turn away would waste a model call the workspace already paid for.
     The coordinator offered "pane closes" as one trigger; this is the one place I did not follow it.
5. **Composer direction** (`assistantFormat.js`). The new `firstStrongDirection(text, fallback)` sets
   the direction from the first letter typed. Before any letter it uses the panel's own direction. It
   no longer recomputes on each keystroke.
6. **A waiting card when a new message is sent** (`assistantTimeline.js`). When the `started` event
   arrives, meaning the server has received the new message, every card from before that turn that
   is still `pending` becomes `expired` with `superseded: true`.
   - Heading: "Not run — you sent a new message" / «اجرا نشد — پیام تازه‌ای فرستادید».
   - Footer: "Nothing ran. The assistant was told this plan was not carried out."
   - Done on `started` rather than on click, so a send that never reached the server leaves the card
     pending, which is the truth.
   - Run and Cancel are disabled while a turn is streaming, so they cannot race that expiry.
7. **Design and accessibility.**
   - Plan and result card headings are now tinted bands with their own icon and an `<h3>`. Pending is
     brand-tinted; pending with a delete is danger-tinted; done is ok; failed is danger; expired and
     rejected are neutral.
   - Step titles dropped from medium to normal weight, so the heading differs by band, icon and two
     weight steps.
   - Every status and safety line moved from `ink-faint` (3.49:1 light) to `ink-muted`: 5.36:1 on
     surface and 5.14:1 on surface-2 in light, 7.5:1 in dark. That covers the remaining-requests and
     model line, list dates, loading text, the "working…" line, tool results, the composer hint and
     the auto-approved note.
   - "Nothing has run yet." is now 14 px medium ink-muted.
   - The "valid until" time inside a band uses the band's own colour (5.3–5.8:1), because ink-muted on
     danger-soft is 4.39:1.
   - Icons and the placeholder stay `ink-faint`; they are not key text.
8. **Tests.**
   - Drawer wiring, against a fake document: mounts once and focuses only when the person asked;
     restoring open on page load does not take focus; Escape with focus inside closes and returns
     focus to the toggle; Escape with focus outside does nothing; Ctrl+J closing from inside returns
     focus; a failed chunk shows the failure, then a retry shows "loading" again and mounts; the phone
     cases above.
   - HTTP: `A_signed_out_page_draws_neither_the_toggle_nor_the_drawer` loads anonymous `/error/404`,
     asserts the topbar rendered, and asserts there is no `data-assistant-state`, no toggle and no
     drawer.
   - Logic tests for items 1–6: settle-on-done, settle is idempotent and leaves earlier turns alone,
     superseding (and what it leaves alone), the refusal body (stepIndex, status, ProblemDetails,
     machine codes), applying the refusal to the card, pane fault versus lost connection with the
     reader cancelled, the turn tracker, a quiet abort mid-stream, first-strong direction, and
     status-only expiry.
   - I mutation-checked four of the new tests by breaking the code and watching them fail, then put
     the code back:
     - removing `reader.cancel` fails the cancel test;
     - removing the phone checks fails the two phone tests;
     - removing the topbar's sign-in check fails the signed-out HTTP test (1 failed, after a rebuild).

The preview harness has two new scenarios, `?scenario=expired` (a 409 refusal carrying
`status: "expired"`) and `?scenario=refuse` (a 400 refusal with `stepIndex: 3`), in place of the old
clock-based one. I checked in the browser pane, against the harness with Vite on port 5183, stopped
afterwards:
- the refused step 4 is highlighted with the server's message under it and Run is still offered;
- the expired refusal ends the card with the server's message;
- the composer stays `rtl` through «وصل shop-db to shop-app please now»;
- a pending card becomes "Not run — you sent a new message" once the reply starts;
- at 375 px a drawer stored open is not restored, and clicking a result link closes it and stores `0`;
- "New" during a stream leaves no stale tool lines or messages and stops the working line.

### Commands and output

```
node --test src/Harbora.Web/Scripts/tests/*.test.mjs
# tests 132   # pass 132   # fail 0
  (assistant-api 29, assistant-drawer 19, assistant-format 20, assistant-plan 15,
   assistant-stream 15, assistant-timeline 22 — 120 of mine, up from 92; plus the 12 existing)

dotnet test tests/Harbora.Tests --no-build --filter "FullyQualifiedName~AssistantPaneHttpTests"
Passed!  - Failed: 0, Passed: 10, Skipped: 0, Total: 10 - Harbora.Tests.dll

cd src/Harbora.Web && npm run build          (alone; no dotnet command running in this worktree)
NPM_EXIT=0
wwwroot/build/assets/AssistantPanel-BGHumOCQ.js   46.66 kB │ gzip: 15.68 kB
wwwroot/build/assets/main-CmDqKNDM.js            476.83 kB │ gzip: 129.56 kB
✓ built in 7.16s        manifest names "Scripts/main.ts"

Full suite (MSBUILDDISABLENODEREUSE=1, -p:UseSharedCompilation=false, log /tmp/test-r6-chat-pane.log):
BUILD_EXIT=0   28 Warning(s) (48× NU1900 + 8× NU1903 lines, the feed warnings only)   0 Error(s)
EXIT=0
Passed!  - Failed: 0, Passed:   500, Skipped: 17, Total:  517 - Harbora.NodeAgent.Tests.dll
Passed!  - Failed: 0, Passed:    15, Skipped:  0, Total:   15 - Harbora.NodeIngress.Tests.dll
Passed!  - Failed: 0, Passed:  6700, Skipped:  0, Total: 6700 - Harbora.Tests.dll
dll=2 msb3030=0
```

This pass added one test to Harbora.Tests (the signed-out case). I did not run master on its own to
confirm its count, but 6700 minus that one test matches master's 6690 plus my original 9.

### Still unverified, and why

- **The real server.** AssistantController is still not on master. The refusal shape
  `{error, messageEn, messageFa, status?, stepIndex?}` is coded from the coordinator's description and
  checked only against the harness.
- **Whether D's server expires a waiting plan when a new message arrives.** The pane marks the card on
  `started`, which assumes the server has done the expiry by the time it sends that event. If it
  expires the plan later in the turn, the card says so a moment early; Run is disabled during the turn
  in any case.
- **Ctrl+J in desktop Chrome and Firefox** is still unverified, as before.
