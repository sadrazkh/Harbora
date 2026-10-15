# P3 report — text, copying, Stop and a wider drawer (branch `r7/p3`, based on master `44d38d0`)

Status: **DONE**, with the concerns listed at the end.

## What was built

### 1. Richer formatting, still no markup (`assistantFormat.js`, `AssistantText.vue`, new `AssistantInline.vue`)

The tree keeps its rule: every leaf is a plain string, rendered with `{{ }}`. The new things in the tree are
structure plus two derived, whitelisted values (`start`/`level` numbers, `digits` ∈ `latin|persian|arabic-indic`)
and the flag `bold: true` on a text or code node. No `v-html`, no HTML strings (the existing source-scan test
covers the new `.vue`/`.js` files too).

- **Bold**: `**x**` and `__x__`. A marker is a run of *exactly two* characters; a lone `*`/`_` and longer runs
  (`***`, `____`) are text (`2*3`, `snake_case_name`, `*.js`). A pair needs a non-space after the opener and
  before the closer, on one line, outside code; `__` additionally may not sit inside a word
  (`snake__case__name`). Code spans are cut out first and never scanned, but a pair may *wrap* code
  (`**\`shop-db\`**` → the code node is bold). Secret handles are cut out whole (a handle is base64url and can
  contain `__`).
- **Numbered lists** `1.` / `1)` in Latin, Persian (۱) and Arabic-Indic (١) digits, nine digits at most (so a long
  number is not a list), `3.5 GB` is not a list. Block `ol { start, digits, items }`: each list keeps the number it
  starts at (a list interrupted by a code block continues as `start: 2`), and renders in the digits the model used
  (`list-style-type: persian`, checked in the browser). A blank line between two items of the same kind no longer
  splits the list (this also applies to `-` lists, which used to become two lists).
- **Headings** `#`…`######` at line start (+ optional closing `#`s) → `{ heading, level, nodes }`, drawn as a
  `<p class="pt-2 font-semibold">` (no `<h*>`), `first:pt-0`.
- **Tables**: header row, `|---|` delimiter row of the *same width*, rows while lines have a `|`. Outer pipes
  and `:---:` colons optional. A `|` inside a code span does not split; `\|` is a literal pipe. Ragged rows are
  padded; a longer row *widens* the table instead of dropping a cell the model wrote. No delimiter / wrong width /
  nothing in it → stays text. Rendered as a real `<table>` in an `overflow-x-auto` wrapper (checked at 360 px: the
  wrapper scrolls, the log and the page do not). Cells are inline nodes with a direction per cell
  (`cellDir`: cell with no letters inherits the table's side). The table's own side (column order) is decided by
  its header row (`blockText`), falling back to all cells when the header has no letters — because the body of a
  Persian table is often identifiers.
- Links stay text. Steady direction while live keeps working for the new blocks (`blockText` covers them; a test
  streams a heading + list + table character by character and asserts no block flips once decided).

### 2. Copy (`assistantCopy.js` new, `AssistantText.vue`)

- Message copy button (lucide `Copy`, «کپی پاسخ» / "Copy answer") under every non-`live` message, drawn inside
  `AssistantText.vue` (the log markup is untouched); each code block has its own button in its corner
  («کپی کد» / "Copy code"). After copying the icon becomes a check, a polite `role=status` region says «کپی شد» /
  "Copied", and everything goes back after 1.5 s; failure says «کپی نشد» / "Couldn't copy" (red icon).
- What is copied is decided in `assistantFormat.js` (`messageCopyText`: the text as written, CRLF → LF, trimmed;
  `codeCopyText`: the code verbatim, no fences/tag, not trimmed). Secret handles stay handles.
- `copyText`: `navigator.clipboard.writeText`, else a hidden-textarea + `execCommand('copy')` fallback that removes
  the element and restores focus; never rejects. `createCopyFeedback` is the state machine (reactive via a `wrap`,
  like `assistantSecrets.js`).

### 3. Stop (`AssistantPanel.vue` composer, `assistantSession.js`, `assistantApi.js`)

- While `answering`, Send is replaced (v-if/v-else, separate keyed elements) by «توقف» / "Stop" (lucide `Square`,
  danger outline, `aria-label` «توقف پاسخ» / "Stop the answer"); a spinner and `disabled` while the request is in
  flight. It is `type="button"`; Enter in the composer still only calls `send()`, which refuses while answering.
  The lock button stays. After the request the focus goes to the composer (the button is about to vanish).
- `api.stop(id)` → `POST /assistant/conversations/{id}/stop` (AgentStopView `{ stopped }`), errors carry the
  server's words like every other call.
- `session.stop()`: one request however often it is pressed; `{ stopped: false }` silent; an error → the composer's
  notice (`timeline.notice`, kind `stop-failed`) and `{ stopped: false, error }`; the next press works again
  (also when the api call throws synchronously); nothing running / no conversation id → no request. When this tab
  only *follows* the answer (`resuming`, no stream here) the conversation is re-read after 400 ms
  (`rereadSoon`) instead of at the next 2.5 s poll — also on `stopped: false`, since then the turn is already over
  and the re-read just shows it. When streaming here nothing is re-read: the stream carries `stopped`/`done`
  (P2 draws them; I handle no stream event).

### 4. Wider drawer (`assistantDrawer.js`, `app.css`, header button in `AssistantPanel.vue`)

- Header button (lucide `MoveHorizontal`) *before* History and New, `aria-pressed`, hidden below 640 px
  (`hidden sm:block`). `aria-label` stays «پهن‌تر» / "Wider" while `aria-pressed` says whether it is on (changing
  the label *and* the pressed state is an ARIA anti-pattern); the **tooltip** flips between «پهن‌تر» / «باریک‌تر»
  ("Wider" / "Narrower").
- The width belongs to the drawer: `createAssistantDrawer` gained `isWide/setWide/toggleWide`, paints `is-wide` on
  `#assistant-panel`, remembers it with `prefs.isWide/setWide` (`harbora-assistant-wide`, failure-safe like the
  other prefs), and `initAssistantDrawer` listens for `harbora:assistant-toggle-wide` and announces
  `harbora:assistant-width` `{ wide }` on every change and once after the island mounted. The island only dispatches
  and listens (no `main.ts` change, no new props).
- `app.css`: `@media (min-width: 640px) { .assistant-drawer.is-wide { width: min(40rem, 100%); } }` (100% = the box
  beside the menu); `width .2s ease` joins the slide transitions; the existing `prefers-reduced-motion` rule
  already turns all of them off. `_Layout.cshtml` did not need to change (so no HTTP-test updates).

## Files touched

All under `src/Harbora.Web/Scripts/`:

- new: `islands/assistantCopy.js`, `islands/AssistantInline.vue`, `tests/assistant-format-rich.test.mjs`,
  `tests/assistant-copy.test.mjs`, `tests/assistant-stop.test.mjs`, `tests/assistant-drawer-wide.test.mjs`
- mine: `islands/assistantFormat.js`, `islands/AssistantText.vue`, `assistantDrawer.js`, `app.css` (drawer rules)
- shared, my functions/regions only: `islands/AssistantPanel.vue` (header widen button, composer Send/Stop, a small
  "stopping an answer and widening the drawer" script block after `onComposerKey`, two listener lines in
  `onMounted`/`onUnmounted`, one extra `import … from 'lucide'` line of my own so the other panes' icon-list edits
  do not collide, and the `createAssistantPrefs` import line gained `EVENT_TOGGLE_WIDE, EVENT_WIDTH`),
  `islands/assistantApi.js` (`stop` after `reject`), `islands/assistantSession.js` (`STOP_REREAD_MS`, `stopping`,
  `stop()` before `pause()`).

**Touched outside my area:** nothing beyond the above. I did not touch `main.ts`, `_Layout.cshtml`, the timeline,
the log markup, History, or the shared preview harness. No existing test was edited (the existing format test
still passes unchanged: new keys appear only when used, `bold` only when `true`).

For the merge: the `AssistantPanel.vue` hunks are in the header (before the History button), the composer
(Send button), after `onComposerKey`, and the lifecycle hooks/imports. The Send icon no longer spins
(`answering ? LoaderCircle : ArrowUp` became `ArrowUp`, because that branch is now Stop).

## Commands run and what they printed

- `cd src/Harbora.Web && npm ci` → exit 0.
- Baseline `node --test src/Harbora.Web/Scripts/tests/*.test.mjs` → `# tests 230 / # pass 230 / # fail 0`.
- Final `node --test src/Harbora.Web/Scripts/tests/*.test.mjs` (exit 0) → `# tests 309 / # pass 309 / # fail 0`
  (230 + 79 new: format 39, copy 11, drawer-wide 12, stop 17).
- `cd src/Harbora.Web && npm run build` (after the last source change) → `BUILD_EXIT=0`, `✓ built in 5.06s`.
- Then, once, with nothing else running in the worktree:
  `export MSBUILDDISABLENODEREUSE=1; dotnet restore Harbora.slnx --ignore-failed-sources` → `RESTORE_EXIT=0`;
  `dotnet build Harbora.slnx --nologo -v q --no-restore -p:UseSharedCompilation=false` → `BUILD_EXIT=0`
  (`14 Warning(s)`, `0 Error(s)`; every warning is NuGet NU1900/NU1903 from the unreachable private feed or an
  existing package advisory; `grep -c "warning CS"` → 0);
  `dotnet test Harbora.slnx --nologo -v q --no-build` → `EXIT=0`:
  `Passed! - Failed: 0, Passed: 500, Skipped: 17, Total: 517 - Harbora.NodeAgent.Tests.dll`,
  `Passed! - Failed: 0, Passed: 15, Total: 15 - Harbora.NodeIngress.Tests.dll`,
  `Passed! - Failed: 0, Passed: 7304, Skipped: 0, Total: 7304, Duration: 1 m 39 s - Harbora.Tests.dll`
  (`grep -c Harbora.Tests.dll` → 2, so it did run; 7304 = the baseline, not lower).

## Verified in a browser (not only by node tests)

With an untracked copy of `assistant-panel.preview.html` (rich-text conversation + a `/stop` endpoint; deleted
afterwards, not committed) served by `npx vite --port 5183`, in the Browser pane:

- Persian/RTL and English/LTR, light and dark: heading, bold words, bold code, `2*3` / `snake_case_name` / `*.log`
  left alone, `<b>…</b>` shown as characters, Persian-digit list rendered ۱. ۲. ۳. (`start=1`, `persian`), table,
  code block with its corner copy button, message copy button under the text. No Vue warnings in the dev console.
- Copy: with a stubbed `navigator.clipboard` the code block copied exactly
  `docker ps\nexport DATABASE_URL={{secret:…}}` (handle stays a handle) and the message copied as written; the
  status region read «کپی شد» and was empty again after 1.7 s. With `navigator.clipboard` removed, a *real click*
  fired the `copy` event with the code through the textarea fallback and left no stray textarea; a synthetic
  `.click()` (no user gesture) made `execCommand` refuse and the pane honestly said «کپی نشد».
- Stop: sending a message turned Send into «Stop» (danger outline, square icon); pressing it showed the spinner and
  disabled state, posted exactly `/assistant/conversations/<id>/stop`, and the button went back to Send with the
  focus in the composer.
- Wide: button toggles `is-wide`; the drawer measured 640 px in a 731 px viewport, `aria-pressed` true, tooltip
  «باریک‌تر», stored `harbora-assistant-wide=1`; after a reload the drawer came back wide and the freshly mounted
  island's button was already pressed.
- 360 px: `documentElement.scrollWidth` 360 (no sideways page scroll), drawer 360, widen button `display:none`, a
  table with an 80-character cell scrolled inside its wrapper (scrollWidth 911 > clientWidth 326) while the log
  stayed 360.

## Unverified / concerns

- **Server side of Stop is not here** (built in parallel): everything is tested against the `AgentStopView` shape
  with fakes and a scripted preview; no real `…/stop` endpoint was called, and the `stopped` event is drawn by P2.
- **Race, accepted:** pressing Stop in the very first moment of a turn (before the server has registered it) gets
  `{ stopped: false }`, which is silent by the brief, so nothing visibly happens and the turn runs on. In the
  normal path the conversation exists before the message is sent, so the window is tiny; on a legacy server
  without the create endpoint there is no conversation id until `started`, and Stop is a no-op until then.
- Not checked in a real browser: Safari/Firefox rendering of `list-style-type: persian`/`arabic-indic` (Chromium
  renders them); the stylesheet's width transition on a page load that restores an open *and* wide drawer
  (it may animate 420 px → 40 rem while sliding in; cosmetic, off under reduced motion).
- Behaviour changes worth a glance in review: a blank line inside a `-` list no longer splits it into two lists;
  `#`…`######` lines are headings; indented continuation lines of a list item are still a separate paragraph
  (not folded into the item).
- The shared preview harness (`assistant-panel.preview.html`) has no `/stop` handler, so Stop there answers 404 and
  shows as a composer notice; I left it alone to avoid a three-way merge on its scenario list.
