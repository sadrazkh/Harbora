# P2 report: showing the assistant's work (activity blocks, plans that stay, compact cards, the thinking line, `stopped`)

Status: **DONE_WITH_CONCERNS**. Everything in the brief is built, tested by node, built by Vite and looked at in a
browser against a faked server. The concerns are at the end: one contract gap (a stored tool line has no outcome),
two judgement calls on wording, and expected textual merge conflicts in the panel's import lines.

Branch `r7/p2`, worktree `E:/Cash.Net/source/repos/sadrazkh/Harbora-r7-p2`, based on `44d38d0`. Commits, owner's
name alone, no trailer:

- `cfcf3a7` Assistant pane: tool lines in people's words with category icons, activity blocks that fold to "3 actions done · 2.4 s", decided plans and results kept in a reopened conversation, compact finished plan cards, a thinking line that names the running tool, and the stopped event.
- `6d87401` Assistant pane: a reopened plan's lines go only when its result card is drawn, a plan refused at approval says so with its reason on its card, and a stored line is named by its label only when it is a read that ran.
- (this report)

## What I built

**`assistantActivity.js` (new, pure, node-tested).** Turns `timeline.items` into what the log draws:

- `categoryIcon(category)`: the map from `pane-common.md` (app → AppWindow … resources → Boxes), anything else →
  Wrench. Returns lucide export names; the panel maps them to glyphs. Prototype keys (`constructor`, `__proto__`)
  also give Wrench.
- `toolLineText / toolLineTitle / toolLineMark`: a live line reads `<label>…` while running, `<label>` when done,
  the server's summary when failed (it says what failed; `<label> failed` when there is none), `<label> — no result
  arrived` when interrupted. With no label (an older server) it falls back to the summary, and only then to the
  identifier, exactly as before. The tooltip carries what the line does not show (the summary behind a label, or
  the identifier). The state mark is a spinner / check / cross / question mark for live lines and nothing for a
  stored line; a running line the thinking line is naming gets no spinner.
- `activityRows(items)`: each run of consecutive `tool` / `tool-history` items becomes
  `{ type: 'activity', key, block }` with `block = { key, items, running, done, failed, unsettled, ran, noted, totalMs }`
  (`totalMs` only when every line has a duration; null otherwise). Text, plans, results, errors, stopped lines and
  the person's messages each end a block. The block's key is its first line's key. A plan that ran with every step
  done and its result directly under it become one `{ type: 'ran', plan, result }` row; so does a result with no
  card (an `executed` event) whose steps are all done.
- `blockView(block, { answering, last, choice, fa })` → `{ tone, working, heading, open }`:
  working (a line runs, or the turn is still being answered and the block is the last row) → open, «در حال کار…»
  with a spinner; failed → open, danger, «۱ کار ناموفق، ۲ انجام شد» / "1 action failed, 2 done"; done → collapsed,
  checks icon in the success colour, «۳ کار انجام شد · ۲٫۴ ثانیه» / "3 actions done · 2.4 s". Digits and the decimal
  separator come from `Intl.NumberFormat('fa-IR' | 'en-GB')`. The person's toggle (`choice`) wins over all of
  these, and is kept by the panel per block key, so it survives the next events of the turn and the end of it.
  Three tones the brief did not name, for honesty (see "Decisions"): `unsettled` (live lines that never got an
  outcome: "1 done, 1 with no result", collapsed), `ran` (stored reads: «۲ کار اجرا شد · ۱٫۱ ثانیه», neutral
  icon and colour, collapsed) and `noted` (stored lines that are not all reads that ran: «۲ کار», collapsed).
- `ranCompactly`, `ranHeading` («برنامه اجرا شد · ۲ گام» / "Plan ran · 2 steps"), `resultLinks`, and
  `planCardLine` (rejected → «لغو شد — هیچ کاری اجرا نشد», expired → «منقضی شد — هیچ کاری اجرا نشد», superseded
  expired → the existing «اجرا نشد — پیام تازه‌ای فرستادید»; every other status → null, drawn in full).
- `thinkingShows`, `trackThinking`, `thinkingLine(state, now)`: the line shows while `isThinking` (unchanged) or while
  a tool of the streamed turn is running. Its label is the running tool's (`«در حال ${labelFa}…»` /
  `"${labelEn}…"`; `Running <name>…` with no label) else «در حال فکر کردن…» / "Thinking…"; from 5 s on, the whole
  seconds since the line appeared. `trackThinking` keeps `state.thinkingSince` (set when the line appears, kept while
  it stays — a tool starting and finishing under it is the same wait — cleared when it goes).

**`assistantTimeline.js` (reducer).**
- `tool` items keep `labelEn, labelFa, category, durationMs` from their events; an outcome event updates them in
  place and never erases what the running event said; a duration that is not a non-negative finite number is null.
- `tool-history` items keep `label, category, durationMs, planId` from `toolLabel/toolCategory/durationMs/planId`.
- `loadConversation` takes `view.plans`: each becomes a `plan` item (server status, so not actionable unless
  pending), placed right after the message `afterMessageId` names — at the start when null, at the end when the id
  is not among the messages — followed by its `result` item (keyed `result-<planId>`, `resulted: true`) when the
  result is not null. Placement happens after the anchor whether or not the anchor was drawn (a text-less assistant
  message, or the plan's own hidden proposal line). A plan id that is also the pending plan is drawn once, as the
  pending card, last, as before. A plan with status `failed` and no result gets `notRun: true` (its heading becomes
  "Not run — a step was refused when it was checked again", not "already ran and stopped").
- Line hiding (after the coordinator's S2 facts, see below): a plan's tool-history lines are hidden only when its
  result card is drawn. A refused plan's lines written after its card (its execution's, which hold the reason) go
  onto the card as `notes`; its proposal lines stay as lines. Rejected / expired / executing plans keep all lines.
- `stopped`: closes the live bubble without marking it cut, settles this turn's running tool lines as
  `interrupted`, and adds `{ kind: 'stopped', messageEn, messageFa }`. The `done` after it settles the turn and changes
  nothing the stop decided (tested, including the session's extra `settleTurn`).
- `createTimeline` gains `thinkingSince: null`. `isThinking` is unchanged (its doc now points at `thinkingLine`).

**`assistantStream.js`.** `ASSISTANT_EVENTS` gains `stopped` (before `done`).

**`AssistantPanel.vue`, the log region (mine).** The log iterates `rows`: activity blocks (bordered section; header
is a `<button>` with `aria-expanded` / `aria-controls` → the `<ul>`; `focus-visible` ring; chevron rotates with
`motion-safe:transition-transform`), each line with its category icon, words (`break-words`, so nothing scrolls
sideways at 360 px), its own duration in 11px muted, and its state mark; the compact `ran` card; the stopped line
(SquareStop icon, the server's words, fallback "You stopped this answer." / «این پاسخ را متوقف کردید.»); and the
existing user / assistant / plan / result / error items unchanged apart from `item` → `row.item`. The thinking line
is the dots bubble, then the activity's icon (category glyph, or Sparkles for "Thinking…") and label, and the seconds
in 11px muted after 5 s. The whole row is `aria-hidden`; the existing sr-only status line now carries the label (no
seconds). The tick is a 1 s `setInterval` started by a `watch` on `thinking` when the line appears and stopped when it
goes and on unmount (own `onUnmounted`, the shared one untouched). The tool-line helpers (`toolText`, `toolIcon`,
`toolIconClass`) delegate to the module. Every existing `data-*` hook is kept (`data-tool-state` on live lines,
`data-tool-history` on stored ones, `data-assistant-thinking`, `data-assistant-thinking-status`, …); new ones:
`data-assistant-activity`, `data-activity-tone`, `data-activity-toggle`, `data-activity-heading`, `data-tool-line`,
`data-tool-category`, `data-tool-duration`, `data-assistant-stopped`, `data-thinking-label`, `data-thinking-seconds`.

**`AssistantResultCard.vue`.** New `compact` prop: success header «برنامه اجرا شد · ۲ گام» with a «جزئیات» / "Details"
button (`aria-expanded`, controls the step list, id from Vue's `useId`), the result's links as accent chips under the
header, always visible, and the steps behind Details (per-step links are not repeated there). Without `compact` the
card is as before. Hooks kept (`data-assistant-result`, `data-result-status`, `data-result-heading`,
`data-step-state`); new `data-result-compact`, `data-result-details`, `data-result-links`.

**`AssistantPlanCard.vue`.** Rejected / expired cards are one muted line with their icon (Ban / Clock), their words
and «جزئیات»; Details shows the full steps and footer. A server refusal on such a card stays in view above the fold.
Pending, executing and failed cards are unchanged. A refused-at-approval plan shows its `notes` (the reason its
execution wrote) in a danger-tinted list under its steps — no `role="alert"`, so a re-read does not re-announce it.
Hooks kept (`data-assistant-plan`, `data-plan-status`, `data-superseded`, `data-plan-heading`, `data-plan-error`, …);
new `data-plan-compact`, `data-plan-details`, `data-plan-notes`.

**`Scripts/tests/assistant-activity.preview.html` (new).** A browser harness for all of the above, faked with the
`44d38d0` shapes and S2's real labels: a reopened conversation with an executed plan (compact), a cancelled one, a
failed one with its result, an expired one anchored outside the view, a refused one anchored on its own proposal
line; a live turn with a failing read; `?scenario=stop`, `?scenario=ok`, `?scenario=old` (no labels anywhere),
`?lang=en`, `?dark`. Kept separate from `assistant-panel.preview.html` so it cannot collide with P1 / P3.

## The coordinator's S2 facts, and how each is handled

1. **`result` can be null on a `failed` plan (refused at re-check), its reason only in its execution lines.** Lines
   are hidden only for plans whose result card is drawn (`reported` set). For a failed plan with no result, the lines
   with its `planId` that come after its card (its execution's) are put on the card as `notes`, shown in view (not
   behind a click, not inside a folded block); its proposal lines stay as ordinary lines. The card is `notRun`, so its
   heading says nothing ran. If its anchor is not in the view (card goes last), its lines simply stay as lines.
   Tests: "a plan without a result keeps the lines it wrote, and a refused one has its reason on its card", "a refused
   plan whose card goes last … keeps its lines as lines".
2. **`afterMessageId` can be the hidden proposal line.** Placement runs after every message whether or not it is
   drawn, so the card goes where the hidden line was. Test: "a card anchored on its own proposal line, hidden because
   its result is drawn, goes where that line was".
3. **`plans` is `[]`; only reads have `durationMs`.** `[]`, null and a missing field all draw the messages as before
   (tested). A missing duration is unknown: a block containing one shows no total (tested). From S2's report I also
   took that proposal / refused / execution lines carry the *write* tool's label ("Deleting a database") with no
   duration — so a stored line is named by its label only when it has a duration (a read that ran); any other stored
   line is drawn in its own words ("Proposed as step 1: delete old-db"), and a block of such lines is only counted
   («۲ کار»), never said to have run or succeeded.

## Files touched

Mine (per `pane-common.md`): `islands/assistantActivity.js` (new), `islands/assistantTimeline.js`,
`islands/assistantStream.js`, `islands/AssistantPlanCard.vue`, `islands/AssistantResultCard.vue`, the log region and
tool-line helpers of `islands/AssistantPanel.vue`, `tests/assistant-activity.test.mjs` (new),
`tests/assistant-timeline.test.mjs`, `tests/assistant-stream.test.mjs`, `tests/assistant-activity.preview.html` (new).

**Touched outside my area** (smallest change each; the merge should look here):
- `AssistantPanel.vue` line 2: `watch` added to the `vue` import.
- `AssistantPanel.vue` lucide import: 14 icons appended on two new lines inside the existing statement (AppWindow,
  Boxes, CheckCheck, ChevronDown, Database, Folder, Gauge, Globe, HardDrive, KeyRound, LayoutGrid, ListChecks, Rocket,
  SquareStop). P1 and P3 will add icons here too — expect a textual conflict; resolve by keeping every name once (a
  name imported twice is a build error, which is why I did not use a second import statement).
- `AssistantPanel.vue` `const thinking = computed(() => isThinking(timeline))` → `thinkingShows(timeline)` (one line,
  next to `answering`, which I did not touch); `isThinking` dropped from the timeline import.
- `tests/assistant-format.test.mjs` (a test file of P3's module): the test "a saved tool message is drawn as a status
  line in its own direction, not as a monospace code block" sliced the markup from `item.kind === 'tool-history'` to
  `<AssistantPlanCard` and required `textDirection(item.text)`. Saved and live lines are now one line in the block, so
  it now slices the `data-tool-line` `<li>`, still forbids `<pre>`, `<details>`, `font-mono` and `dir="ltr"`, checks
  saved lines are drawn there, and requires `textDirection(toolText(item))`. Same intent, new markup.

## Existing tests changed on purpose

- `assistant-stream.test.mjs` "parseAssistantFrame returns the parsed JSON for every event the contract names": the
  pinned `ASSISTANT_EVENTS` list now includes `stopped` (the contract added it).
- `assistant-format.test.mjs`: as above.
- No existing timeline or session test was changed. (I first changed `isThinking` itself to show for a running tool,
  saw it break a session test and a timeline test that pin "a running tool has its own spinner", and instead kept
  `isThinking` and added `thinkingShows` beside it.)

## Commands run and what they printed

All from the worktree.

```
cd src/Harbora.Web && npm ci                                       -> NPM_EXIT=0
node --test src/Harbora.Web/Scripts/tests/*.test.mjs  (baseline)    -> tests 230, pass 230, fail 0
dotnet restore Harbora.slnx --ignore-failed-sources                 -> RESTORE_EXIT=0
node --test src/Harbora.Web/Scripts/tests/*.test.mjs  (final)       -> tests 280, pass 280, fail 0
cd src/Harbora.Web && npm run build   (final, nothing else running) -> BUILD_EXIT=0, "✓ built in 10.88s"
```

Node: 280 = 230 + 50 new (assistant-activity 32, assistant-timeline 47 → 64, assistant-stream 15 → 16).

A mutation check of the new rules (scratch script, each mutation applied, the activity and timeline tests run, the
file restored; `git status` clean of them afterwards): 14 of 14 mutations made tests fail — failure block open,
working block open, the person's choice winning, compact only when all steps done, total only when every duration is
known, stored lines not counted as done, the 5 s threshold, no second spinner, the clock keeping its start, hiding
only with a result, refused = not run, unknown anchor at the end, stop not marking the bubble cut, duration kept from
the outcome event.

Full dotnet suite once at the end, after the last code change and with no npm build running, recipe from CONSTRAINTS
(`MSBUILDDISABLENODEREUSE=1`, `-p:UseSharedCompilation=false`, `--no-restore` on the build after the restore above):

```
dotnet build Harbora.slnx --nologo -v q --no-restore -p:UseSharedCompilation=false > /tmp/test-r7-p2.log.build
BUILD_EXIT=0      14 Warning(s), 0 Error(s) — 24× NU1900 + 4× NU1903 lines (package feed / SSH.NET advisory), no CS warnings
dotnet test Harbora.slnx --nologo -v q --no-build > /tmp/test-r7-p2.log
EXIT=0
Passed!  - Failed: 0, Passed:  500, Skipped: 17, Total:  517 - Harbora.NodeAgent.Tests.dll (net10.0)
Passed!  - Failed: 0, Passed:   15, Skipped:  0, Total:   15 - Harbora.NodeIngress.Tests.dll (net10.0)
Passed!  - Failed: 0, Passed: 7304, Skipped:  0, Total: 7304 - Harbora.Tests.dll (net10.0)
grep -c "Harbora.Tests.dll" -> 2      grep -c MSB3030 -> 0
```

`Harbora.Tests.dll` 7304 = the baseline (this task adds no .NET tests).

**In a browser** (the desktop app's browser pane, `npx vite --port 5187` started from this worktree — `preview_start`
reads the session's launch.json in the primary checkout, which I must not touch; the server was stopped afterwards):
the harness in Persian and English, light and dark: collapsed history blocks («۲ کار اجرا شد · ۱٫۱ ثانیه»), opening a
block and the compact card's Details (`aria-expanded` and `aria-controls` targets checked by script), the cancelled /
expired lines, the failed plan in full, the refused plan with its reason on the card; a live turn — «در حال کار…» with
the running line not spinning and the thinking line «در حال خواندن لاگ‌های استقرار… ۸ ثانیه», then the failed block
staying open «۱ کار ناموفق، ۱ انجام شد»; `?scenario=stop` — "1 done, 1 with no result" and the stopped line; the
screen-reader status read "Reading the deployment's logs…" while the tool ran; at 360 px with every block and
Details open, `document.scrollWidth` = 360 and no element in the log past the edges.

## Decisions worth a second look

- **Stored blocks never say "done".** The approved mockup's «۳ کار انجام شد» with the success check is drawn for live
  lines the server said were done. A reopened conversation's lines have no outcome in the contract, so their block
  says «… کار اجرا شد» (reads that ran) in a neutral colour, or only the count when it holds proposals / refused /
  execution lines. A just-finished block therefore changes from green "done" to neutral "ran" when the conversation
  is re-read. That is the honest reading of the data, not a bug; it disappears if the contract gains a state.
- **Stored reads show their label; the stored summary is in the tooltip.** The brief asks for labels; a stored read
  that failed ("App not found") therefore shows "Reading an app's details" with the failure only on hover. Live
  failures are unaffected (they show the summary, in danger colour, in an open block).
- **Superseded plans keep "Not run — you sent a new message"** as their one line rather than the brief's «منقضی شد —
  هیچ کاری اجرا نشد», because it says why it expired seconds after it appeared; a plain expired plan uses the brief's
  words verbatim.
- **Compact also for an `executed` event's result** (a plan that ran with no card): it is "a finished plan" in
  `look.md`; only when every step is done.
- **The thinking line shows while a tool runs** (dots + the tool's label), as brief item 5 says; the block header still
  has its own spinner per item 2.

## Unverified

- Against the real server: S1's stop endpoint / `stopped` event and S2's fields were checked only through fakes with the
  `44d38d0` shapes and S2's report (I read S2's branch and report; I did not run its code with the pane).
- P3's Stop button is not on this branch; the `stopped` path was exercised by the reducer tests and the harness's fake
  stream only.
- Screen readers were not run; the announcements are reasoned from the existing live-region design.
- `assistant-panel.preview.html` (shared) was left as it was; it still sends unlabelled events, i.e. shows the
  older-server fallback.

## Concerns

1. **Contract gap:** `AgentMessageView` carries no tool outcome, so a reopened conversation cannot say which stored reads
   failed, and the pane has to infer "a read that ran" from `durationMs` being present (true of S2's server, as its
   report states, but an inference). A `toolState` on stored tool messages in a later round would let stored blocks
   read "done"/"failed" like live ones.
2. **Merge:** the lucide and `vue` import lines in `AssistantPanel.vue` will conflict textually with P1 / P3; keep every
   name once.
3. `useId()` ids (`assistant-result-steps-v-0`, …) are unique per Vue app; the pane is one app per page, so they are
   unique on the page.

## Fix pass (after the coordinator's review)

Commit `e918790`, on top of `9b432ea`. Where it disagrees with the sections above, this section is current. The
earlier "Decisions": stored blocks never saying "done", stored words in the tooltip, and concern 1 (the contract
gap) are replaced by what follows.

**toolState (new field, coded against fakes).** `loadConversation` gives every `tool-history` item a `state`:
"done" or "failed" from the stored message's `toolState`; null for null, for a missing field and for any other
value (a typo or a new word never reads as "done"). In `assistantActivity.js`, `lineOutcome(item)` classifies
every line. A stored line with an outcome counts, colours and marks exactly like a live one: a check, a cross, a
heading of "3 actions done", and a failure opens its block in danger colour. The inference from earlier is now
only a fallback, for a stored line with no outcome:
- with a duration, it was a read that ran on a server from before toolState ("ran", neutral, no check);
- otherwise it is "noted".

**The server's words are always visible (the Important finding).**
- Each line shows the label as its main text, live or stored, with "<label>…" while running.
- The new `toolLineDetail` adds a visible second line in 11px, muted or danger for a failed line. It shows the
  live summary or the stored text whenever there is one and it differs from the main text.
- A failed line always has words: its summary, or "It failed." / «ناموفق بود.».
- An interrupted line says "No result arrived." / «نتیجه‌ای نرسید.».
- An unnamed state shows its summary, or "The outcome is not known.".
- Lines with no label, and stored lines with no outcome (proposals, refused calls, old rows), show their own words
  as the main text, with no second line.
- The tooltip is now only the tool's identifier.
- In `AssistantPanel.vue` the line is a two-span column with a `data-tool-detail` hook. The main text is in danger
  colour only when a failed line has no second line. `data-tool-state` is now on stored lines too.

**Minors.**
1. A choice is now `{ open, tone }`, made by `toggledChoice(view)`. `blockView` ignores a choice made under a tone
   other than "failed" once the block has failed, so a failure arriving after a fold opens the block again. A fold
   made after the failure holds. Tested in "a failure arriving after the person folded the block opens it again".
   The browser check matched: folded while «در حال کار…» (`aria-expanded` false), then reopened when the failure
   came («۱ کار ناموفق، ۱ انجام شد», true).
2. Notes are execution lines only. A line becomes a note when its card was placed after an anchor and the line
   comes after that anchor, or, for a card drawn first (`afterMessageId` null), when the line has a stored outcome
   (a proposal never has one). Tested in "a refused plan drawn first (no anchor) keeps its proposal lines as lines:
   only execution lines are notes".
3. `runningTool` stops its scan at the first non-tool item. Tested in "a running tool with text after it is not
   what the thinking line names".
4. `stopped` and `settleTurn` now share `interruptRunning(state)`.
5. Headings are built by `countsHeading`, which counts every kind of line in the order failed, done, no result,
   ran, other. The first count carries the noun:
   - "1 action failed, 1 done, 1 other" / «۱ کار ناموفق، ۱ انجام شد، ۱ مورد دیگر»;
   - "1 action ran, 1 other";
   - "1 action done, 1 with no result" / «۱ کار انجام شد، ۱ بی‌نتیجه» (the English was "1 done, …" before).

   A time is added only to "done" and "ran" headings with every duration known, so a "noted" heading never has an
   empty time. Unchanged from the brief: «۳ کار انجام شد · ۲٫۴ ثانیه» and «۱ کار ناموفق، ۲ انجام شد».

The superseded-plan wording («اجرا نشد — پیام تازه‌ای فرستادید») is kept as asked.

**Tests changed in this pass** (all in files this task added or owns):
- Rewritten for the new rules:
  - "a labelled line reads … whatever its outcome";
  - "a saved read that ran reads its label…", now "a saved line with an outcome reads its label, with the stored
    text visible under it", plus a new test for lines without an outcome;
  - the tooltip test;
  - "the person's choice wins…" and "the choice survives…" (choices are objects now);
  - the mixed "noted" and "unsettled" heading tests;
  - the stored-conversation test at the end of assistant-activity.test.mjs, now with toolState (a failure open
    and in danger colour, labels with visible words, a check and a cross) and a second half for a server without
    toolState;
  - the stored tool message shape in assistant-timeline.test.mjs (it now has `state`).
- New:
  - the visible second line, live and stored;
  - the older-server fallback;
  - stored outcomes counting like live ones;
  - toolState mapping, including unknown values;
  - failure reopening a fold;
  - proposal lines kept out of notes;
  - runningTool stopping at text.

**Commands and output (fix pass).**
```
node --test src/Harbora.Web/Scripts/tests/*.test.mjs    -> NODE_EXIT=0; tests 288, pass 288, fail 0
                                                           (assistant-activity 38, assistant-timeline 66)
cd src/Harbora.Web && npm run build                     -> BUILD_EXIT=0, "✓ built in 5.76s", no warnings or errors
```

Mutation checks, run with a scratch script that applies each mutation, runs the activity and timeline tests and
restores the file (`git status` showed only my intended changes afterwards):
- 10 of 10 new-rule mutations failed tests: failure reopens a fold, runningTool stops at a non-tool item, a failed
  line always has words, stored words visible, no label without an outcome, stored outcomes count, mixed headings
  count noted lines, stored marks, notes are execution lines only, toolState is only done or failed.
- 7 of 7 earlier rules re-checked against the rewritten code also failed tests: failed open, working open, the
  choice wins, done only when all lines are done, total only when every line is timed, the 5 s threshold, stop
  interrupts running lines.

In the browser (harness with toolState fixtures, Vite started from this worktree and stopped afterwards):
- A reopened conversation reads «۲ کار انجام شد · ۱٫۱ ثانیه» with checks and each stored text under its label.
- «۱ کار ناموفق، ۱ انجام شد» is open, with the failed read's words in danger colour.
- The proposal block reads «۱ کار».
- At 360 px with everything opened: `scrollWidth` 360, and no element in the log past either edge.

The full dotnet suite was not run again in this pass. The pass changed only front-end files, and the last full
run is the one above at 7304. The browser check of the toolState path used fakes only: S2's `toolState` was not on
any branch I ran.
