# P1 report — the conversation list (delete one, delete all, day groups, search, show more)

Branch `r7/p1` (from master `44d38d0`). Status: **DONE_WITH_CONCERNS** (concerns at the end; none is a known defect).

## What was built

`AssistantHistory.vue` (new) holds the History view that used to be inline in `AssistantPanel.vue`; its rules are in
the new plain module `assistantHistory.js`, tested by `node --test`.

- **Groups.** `groupKey` / `groupConversations` / `timeLabel` take "now" as a parameter. A row's group is its local
  calendar date against today's: Today, Yesterday, Previous 7 days (2 to 7 calendar days back; yesterday has its own
  group), Older. Days are counted from the local year/month/day (not by dividing timestamps by 24 h), so the two
  daylight-saving days a year cannot put a row in the wrong group. Future or unreadable times: today / older.
  Empty groups are not drawn; order inside a group is the order the server sent. Today and yesterday show the time
  only; older rows show the date (`fa-IR`, Persian calendar, Persian digits, in Persian).
- **Search.** `type="search"` box with an sr-only label «جست‌وجو در عنوان‌ها» / "Search titles", debounced 300 ms,
  trimmed `q` sent to the server. Every keystroke invalidates what is in flight, so an answer for an older query —
  whether it arrives after the newer query was asked or only typed — is dropped (and so is its failure). The empty
  state is worded by the query the rows answer (`state.shown`), not by what is being typed: «گفت‌وگویی با این عنوان
  پیدا نشد» for a query, the old «هنوز گفت‌وگویی نیست.» without one. Esc in the box clears it first (without closing
  the drawer); with the box empty Esc is the drawer's.
- **Show more.** Offered when the last page had 30 rows; asks `skip = rows so far` for the query the rows answer;
  appends and drops ids already shown. A "show more" in flight when the query changes, or when a delete moves every
  row under it, is dropped. A failed one keeps the rows and shows its words with the button as the retry.
- **Delete one.** Trash button at each row's end (`aria-label` «حذف این گفت‌وگو», described by the row's title;
  faint, full on row hover / focus-within, always full under `(hover: none)`). It replaces the row, in place, with
  the soft-danger strip «این گفت‌وگو حذف شود؟» / «حذف» / «نه»; focus moves to «نه»; Esc cancels (stopped, so the
  drawer stays open); only one question at a time (a row's and "delete all" exclude each other). On «حذف» the strip
  shows a spinner; **the row leaves only after the server said so**: 204 → gone; 404 → gone (it is gone anyway);
  409 → the strip shows the server's words with «باشه», row stays; anything else → the words with «دوباره» (retry)
  and «بستن». While a request is out nothing else can be asked or cancelled. A page asked before the delete finished
  cannot bring the row back (`removed` set). After a delete, focus goes to the neighbouring row (or the search box).
- **Delete all.** Pinned under a hairline at the bottom of the list view: «حذف همهٔ گفت‌وگوها» in danger colour and the
  muted note «کارهای انجام‌شده برنمی‌گردند». It asks in place the same way («همهٔ گفت‌وگوها حذف شوند؟», the note that what
  the assistant did is not undone and the audit log keeps it, plus a line that it is *every* conversation, not only
  the search results, while a search is active). After it the list reloads, and if `skipped > 0` a line says how many
  were kept because they were still running.
- **Deleting the open conversation** (alone, via 404, or in delete all): `session.forget(id)` (new, in
  `assistantSession.js`) drops that conversation's outbox entry, and if it is on screen (or what a failed read holds)
  starts a new empty conversation and clears the remembered id; another conversation's remembered id / outbox is not
  touched. For delete all with nothing skipped the open one is certainly gone; with some skipped it is asked of the
  server (`GET` → 404 means gone; success or any other error means "not known to be gone", and a reload settles
  it quietly through `quietIfGone`).
- **API** (`assistantApi.js`): `conversations({ q, skip })` (no query string when both are empty), `deleteConversation(id)`,
  `deleteAllConversations()`; DELETEs carry `X-CSRF-TOKEN` through the existing `json()` helper.

## Files touched

New: `Scripts/islands/AssistantHistory.vue`, `Scripts/islands/assistantHistory.js`,
`Scripts/tests/assistant-history.test.mjs` (42 tests), `Scripts/tests/assistant-history-api-session.test.mjs` (13 tests),
`Scripts/tests/assistant-history.preview.html` (browser harness, see below).
Changed: `Scripts/islands/AssistantPanel.vue` (History block replaced by `<AssistantHistory …>`; `conversations*` refs,
`formatDate` and `Back` removed; `showHistory` / `openConversation` adjusted; new `openError` ref and
`onConversationDeleted`; imports `ChevronLeft, ChevronRight, MessageSquare` removed from the lucide list and
`AssistantHistory` added), `Scripts/islands/assistantApi.js` (the three functions and their header-comment lines
only), `Scripts/islands/assistantSession.js` (`forget` only, between `startNew` and `send`), `Scripts/app.css`
(appended section "Round seven P1": the faint-until-hover trash button).

**Touched outside my area:** nothing beyond the above. For the merge: the lucide import list at the top of
`AssistantPanel.vue` (line 4: I removed three icons — P2/P3 will edit nearby lines of the same list, resolve by union)
and the `v-if="view === 'history'"` block replacement; `assistantApi.js` header comment (I added three lines after the
`GET /assistant/conversations` line).

**Contract note (not changed):** `deleted` is a *prop* (`onDeleted`) on `AssistantHistory`, not an emit — the parent
still writes `@deleted="…"`. Reason in the component header: a delete that finishes after the person left the list
would otherwise emit from an unmounted component, which Vue drops, and the pane would keep a deleted conversation
open. `open` and `back` are emits as briefed. Also: the brief says 409 `busy` carries the server's words — the pane
uses `messageEn` / `messageFa` of the 409 body as `assistantApi.js` already extracts them; if the server sends a 409
without words, `describeFailure`'s generic fallback ("no longer possible — may already have run, been cancelled or
expired") would be shown, which is wrong for "busy". The server's `DELETE` should send `{ error: "busy", messageEn,
messageFa }` like the existing `turn_in_progress` body.

## Commands run and what they printed

(all in `E:/Cash.Net/source/repos/sadrazkh/Harbora-r7-p1`)

- `cd src/Harbora.Web && npm ci` → exit 0 (`NPMCI_EXIT=0`).
- Baseline before my change: `node --test Scripts/tests/*.test.mjs` → `# tests 230 / # pass 230 / # fail 0`.
- After: `node --test Scripts/tests/*.test.mjs` → `# tests 285 / # pass 285 / # fail 0` (230 + 42 + 13). The history tests
  also pass with `TZ=UTC`, `America/New_York`, `Asia/Tehran`, `Pacific/Auckland` (42/42 each).
- Mutation check of the rules (each mutation made a test fail, then reverted): dropping the stale-answer guard (5 tests
  failed), the deleted-ids set (1), the debounce invalidation (2), the "show more" invalidation on delete (1), de-dup
  (2), the offset (4), the 404 handling (1), `hasMore` (3).
- `npm run build` → `BUILD_EXIT=0` (`✓ built in 5.66s`, `AssistantPanel-*.js 84.11 kB`). Run while no dotnet was running
  in this worktree.
- `export MSBUILDDISABLENODEREUSE=1; dotnet restore Harbora.slnx --ignore-failed-sources` → exit 0.
- `dotnet build Harbora.slnx --nologo -v q --no-restore -p:UseSharedCompilation=false` → `BUILD_EXIT=0`, 0 errors, 14
  warnings, all `NU1900` / `NU1903` (NuGet vulnerability-feed and SSH.NET advisories — environment / pre-existing; no
  compiler warning; I changed no C#).
- `dotnet test Harbora.slnx --nologo -v q --no-build` → `EXIT=0`:
  `Passed! - Failed: 0, Passed: 500, Skipped: 17, Total: 517 - Harbora.NodeAgent.Tests.dll`,
  `Passed! - Failed: 0, Passed: 15, Total: 15 - Harbora.NodeIngress.Tests.dll`,
  `Passed! - Failed: 0, Passed: 7304, Total: 7304 - Harbora.Tests.dll` (baseline 7304, not dropped);
  `grep -c "Harbora.Tests.dll"` → 2 (present). Log: `/tmp/test-r7-p1.log`.

## Browser verification (a harness exists, and I used it)

`Scripts/tests/assistant-history.preview.html` (new; same idea as `assistant-panel.preview.html`, which I did not touch)
fakes `fetch` with a *stateful* conversation list (75 rows over today / yesterday / the week / older; honours `q`,
`skip`, DELETE one, DELETE all) and mounts the real `AssistantPanel.vue`. `npx vite --port 5184 --host 127.0.0.1` in
`src/Harbora.Web`, then `http://127.0.0.1:5184/build/Scripts/tests/assistant-history.preview.html` (`?lang=en`, `?dark`,
`?scenario=slow|busy|gone|flaky|kept|loadfail|morefail`). I drove it in the desktop app's built-in browser pane and saw:
the four day groups and times in Persian (Persian calendar for older rows); the open row's accent background; the
trash button; trash → strip with focus on «نه»; Esc closing it with focus back on the trash button and the drawer still
open; «حذف» → «در حال حذف…» → the row leaving only after the answer, focus on the next row, remembered id cleared when it
was the open conversation; typing five Persian letters → exactly one request (`?q=...`) and the filtered rows; no-match
text; Esc clearing the box without closing the drawer; "show more" 30 → 60 rows with no duplicate id; delete all →
empty state, remembered id cleared; delete all with one kept → the "1 conversation was kept…" line and the open
conversation retained; busy → the server's words and «OK», row stays; flaky → the 502 words with «Try again» →
retry removes the row; English/LTR and dark theme; 360 px wide (touch emulation, so the trash is always visible):
`scrollWidth == 360`, a very long title is cut with an ellipsis in the row and in the strip, nothing scrolls
sideways; and going «Back» while a slow delete of the open conversation was still out still made the pane forget that
conversation (the reason `deleted` is a prop). No Vue warnings and no console errors were logged in that session.
(The `gone`, `loadfail` and `morefail` scenarios exist in the harness but I did not click through them; they are
covered by the node tests.)

## Unverified

- The real server: the endpoints are being built in parallel; everything above was against the fake and the contract
  shapes of `44d38d0`. Whether the server's 409 carries `messageEn`/`messageFa` (see the contract note) is unconfirmed.
- Not run in the real panel behind ASP.NET (only the Vite harness), and only in the built-in Chromium: no Firefox /
  Safari check. The faint trash relies on `@media (hover: none)`, checked only through the pane's touch emulation.
- Screen-reader behaviour (the `aria-describedby` on the trash, the `role="status"` lines) is by construction, not
  heard through a screen reader.
- Interaction with P2's and P3's changes to `AssistantPanel.vue`: not merged here, so not run together.
- The history tests were last run in the four time zones before the final component edit; the component edit did not
  touch the modules, and the full node suite (285) was re-run after it.
