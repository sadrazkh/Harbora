# P2 — Showing the work: activity blocks, plans that stay, compact results, a talking indicator (pane)

Read `pane-common.md` (binding) and `look.md` first. You own the timeline reducer and the log's markup.

## Build

1. **Tool lines speak people's words.** `tool` items keep `labelEn/labelFa/category/durationMs` from the
   event; the running line reads "<label>…", a finished line "<label>", a failed one the server's summary
   (it says what failed) — the identifier only as a last resort when no label came (an older server).
   `tool-history` items from the view use `toolLabel/toolCategory/durationMs/planId`. Each line has its
   category's icon (map in `pane-common.md`), with the state shown by a small spinner, check or cross
   beside it as today.
2. **Activity blocks.** A pure function in a new `assistantActivity.js` turns `timeline.items` into what is
   drawn: each run of consecutive `tool` / `tool-history` items becomes one block `{ items, running,
   done, failed, totalMs }` (`totalMs` only when every item has a duration). Markup: a bordered block whose
   header is a button (`aria-expanded`, controls the list):
   - while any item runs (or the turn is streaming and the block is the last thing): open, header «در حال
     کار…» "Working…" with a spinner;
   - after: collapsed to «۳ کار انجام شد · ۲٫۴ ثانیه» / "3 actions done · 2.4 s" (digits and the decimal
     separator in the panel's locale — use `Intl.NumberFormat`); with failures «۱ کار ناموفق، ۲ انجام شد»
     in danger colour and **open**, so a failure is never hidden behind a click; a single item without a
     failure is still a block (consistent look).
   - The person's open/closed choice for a block survives the next events of the same turn (keyed by the
     block's first item key).
3. **Plans that stay.** `loadConversation` takes `view.plans`: each becomes a `plan` item (with the
   server's status; not actionable unless pending) placed right after the message `afterMessageId` names
   (at the start when null; at the end when that id is not in the list), followed by its `result` item
   when `result` is not null. Hide the `tool-history` lines whose `planId` belongs to a plan drawn this way
   (proposal and execution lines say what the card says). The pending plan stays as today.
4. **Compact finished cards.** `AssistantPlanCard`: a plan that is `executed` and has its result card under
   it collapses into the result card's header — draw one compact card, as in `look.md`: success header
   «برنامه اجرا شد · ۲ گام» "Plan ran · 2 steps" with «جزئیات» toggling the full step list (the result card's
   rows), and the result's links as accent chips under the header, always visible. `failed` stays expanded
   (it says which step failed and why — never hidden). `rejected` / `expired` become one muted line with
   their icon and words («لغو شد — هیچ کاری اجرا نشد» / «منقضی شد — هیچ کاری اجرا نشد») and «جزئیات».
   Pending cards are unchanged. Keep every existing `data-*` hook the tests use.
5. **The thinking line.** Next to the dots: the current activity's icon and label — the running tool's
   label when one is running (and that tool's own line then does not spin a second spinner), else
   «در حال فکر کردن…» / "Thinking…" — and after 5 s the elapsed seconds since the dots appeared, in 11px
   muted, ticking once a second (the timer is stopped when the line goes and on unmount). Screen readers
   get the label through the existing status line, not the ticking seconds. Reduced motion: dots still.
   Rules in a pure selector (`thinkingLine(state, now)`), tested.
6. **Stop.** `assistantStream.js` accepts the `stopped` event; the reducer closes the live bubble (not
   marked cut — the person stopped it), settles running tool lines as `interrupted`, and adds a `stopped`
   item drawn as one muted line with a square-stop icon and the server's words. (P3 builds the button.)

## Tests (node)

Grouping into blocks (runs split by text, plans, user messages; durations summed only when all known;
counts); block open/closed rules (running open, done collapsed, failure open, the person's choice kept);
history plans placed after the right message, at the start, at the end, with results, with their lines
hidden, pending untouched; tool lines from labelled and unlabelled events; `thinkingLine` (label, thinking,
5 s threshold); the `stopped` event (bubble not cut, running tools interrupted, item added, `done` after it
still settles the turn). Keep the existing timeline tests green — update one only when the behaviour
changed on purpose, and list it.

Report: `.superpowers/sdd/r7/p2-report.md`.
