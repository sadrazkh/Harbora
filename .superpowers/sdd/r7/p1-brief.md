# P1 — The conversation list: delete, delete all, groups, search, more (pane)

Read `pane-common.md` (binding) and `look.md` first.

## Build

1. **Move the History view out** of `AssistantPanel.vue` into `AssistantHistory.vue` (props/emits for
   what it needs: the current conversation id, `fa`, the api; emits `open(id)`, `back`, `deleted(id)`),
   with its rules in a new plain module `assistantHistory.js`.
2. **Groups.** Rows grouped by the browser's local calendar date of `updatedAt`: Today «امروز»,
   Yesterday «دیروز», Previous 7 days «۷ روز گذشته», Older «قدیمی‌تر»; empty groups not shown; order
   within a group as the server sent it. Times: today and yesterday show the time only; older show the
   date (Persian calendar in fa, as `formatDate` does now). Grouping is a pure function taking "now".
3. **Search.** A search box at the top (`type="search"`, label for screen readers, «جست‌وجو در عنوان‌ها»),
   debounced ~300 ms, sends `q` to the server; a response that arrives after a newer query was typed is
   ignored (test this). Empty result for a query: «گفت‌وگویی با این عنوان پیدا نشد» / "No conversation
   with that title"; empty list without a query keeps today's empty state.
4. **More.** When the last page had 30 rows, «نمایش بیشتر» fetches `skip = rows so far` and appends,
   dropping any id already shown (a conversation can move between pages while the person scrolls).
5. **Delete one.** A trash button at each row's end (`aria-label` «حذف این گفت‌وگو» / "Delete this
   conversation"; faint until the row is hovered or focused within, always visible with
   `(hover: none)`). Pressing it replaces that row, in place, with the confirm strip from `look.md`
   («این گفت‌وگو حذف شود؟», «حذف» / «نه»); focus moves to «نه»; Esc cancels. Only one row in confirm
   at a time. On «حذف»: the strip shows a spinner; on 204 the row goes; on 409 `busy` the strip shows the
   server's words («هنوز در حال پاسخ یا اجراست…») with «باشه» to close; on 404 the row goes (it is gone
   anyway); other errors shown in the strip with «دوباره» retry. Never remove a row before the server
   said so.
6. **Delete all** at the bottom as in `look.md`, confirmed in place the same way, with the note that what
   the assistant did is not undone and the audit log keeps it. After it: the list reloads; if `skipped > 0`,
   a line says how many were kept because they were still running.
7. **Deleting the open conversation** (alone or in delete all): the pane forgets it — a new empty
   conversation, the remembered id cleared, and any unsent words kept for that conversation (the outbox in
   `assistantSession.js`) dropped — so a reload never tries to open it again. Add `session.forget(id)` for
   this (only your function in that file).
8. The api functions in `assistantApi.js` listed in `pane-common.md`.

## Tests (node)

Grouping across midnight and the 7-day edge (with a fixed "now", in both languages); stale search
responses ignored; "more" appends and de-duplicates; the confirm state machine (one row at a time, Esc,
busy, not found, retry); forgetting the open conversation clears the remembered id and its outbox but not
another conversation's; api functions send the right method, URL and CSRF header.

Report: `.superpowers/sdd/r7/p1-report.md`.
