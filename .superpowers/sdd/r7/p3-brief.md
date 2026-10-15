# P3 — Text, copying, Stop, and a wider drawer (pane)

Read `pane-common.md` (binding) and `look.md` first.

## Build

1. **Richer formatting, still no markup.** Extend `assistantFormat.js`'s tree (keep its rule: leaves are
   strings, rendered with `{{ }}` only) and `AssistantText.vue`:
   - inline `**bold**` (and `__bold__`); a lone `*` or `_` stays a character (`2*3`, `snake_case_name`,
     file globs) — bold only when the pair encloses non-space text on one line, never inside `code`;
   - numbered lists (`1.` / `1)` and Persian digits `۱.`), kept in their own numbering, alongside the
     existing `-` lists;
   - headings `#`, `##`, `###` at line start → a bold paragraph (14px `font-semibold`, not `<h*>` tags —
     the pane's outline belongs to the panel), with a little space above;
   - simple tables: a header row, a `|---|` separator line and body rows → a real `<table>` inside a
     horizontally scrollable wrapper (the table scrolls, the page never does), cells as inline nodes,
     direction per cell text; a malformed table stays text;
   - links stay text (as today) — the model's text is untrusted.
   Direction rules (steady direction while live) keep working for the new blocks.
2. **Copy.** Under each finished assistant message (not while live) — drawn inside `AssistantText.vue`, which you own, driven by its `live` prop, so the log markup (P2) does not change — a small copy button (lucide `Copy`,
   `aria-label` «کپی پاسخ» / "Copy answer"); every code block gets its own copy button in its corner
   («کپی کد» / "Copy code"). Copies the plain text as written (secret handles stay handles — they are not
   the secret). `navigator.clipboard.writeText` with a hidden-textarea fallback; after copying, the icon
   becomes a check and a polite live region says «کپی شد» / "Copied" for ~1.5 s; a failure says
   «کپی نشد» / "Couldn't copy". The rule (what text a block copies) in the module, tested.
3. **Stop.** While `answering` (streaming here, or resuming after a reload), the send button becomes
   «توقف» / "Stop" (lucide `Square`, danger outline, `aria-label` «توقف پاسخ» / "Stop the answer").
   `session.stop()` (your function in `assistantSession.js`) calls `api.stop(conversationId)` (your
   function in `assistantApi.js`): while it is in flight the button shows a spinner and cannot be pressed
   twice; `{ stopped: false }` (it had already finished) is silent; an error shows as the composer's
   notice. When resuming (no stream in this tab), after a successful stop the conversation is re-read
   promptly rather than waiting for the next poll. The `stopped` stream event itself is drawn by P2 — do
   not handle it. Enter in the composer never triggers Stop.
4. **Wider drawer.** A header button (lucide `MoveHorizontal`, `aria-pressed`, «پهن‌تر» / «باریک‌تر»,
   "Wider" / "Narrower") toggles the drawer between its current width and a wide one (about 40 rem,
   never wider than the viewport minus the menu); remembered with the existing prefs
   (`createAssistantPrefs` in `assistantDrawer.js`, failure-safe storage); hidden at phone width, where
   the drawer is full-width already. The width belongs to the drawer (`.assistant-drawer`, `_Layout`,
   `app.css`), not to the island, and the transition respects reduced motion.

## Tests (node)

Formatter: bold pairs and the lone-star cases, bold not inside code, numbered lists (Latin and Persian
digits, `.` and `)`), headings, tables (good, ragged rows, missing separator → text), and every new block
still yielding string leaves only (feed it `<script>`, `<img onerror>`); what copy takes for a message and
for a code block; `session.stop` (calls the api with the current id, no double press, re-read when
resuming, silent on `stopped: false`, error → notice); the width preference (default, toggle, survives a
storage that throws). If the HTTP tests pin the layout markup you change, update them and say so.

Report: `.superpowers/sdd/r7/p3-report.md`.
