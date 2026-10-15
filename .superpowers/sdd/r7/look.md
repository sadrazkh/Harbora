# The look the owner approved (round seven mockup, 2026-10-15)

Owner's request: «بشه چت های قدیمی که با ai بوده پاک بشه و امکانات و نمایش و نشون دادن فرایند ها کمی
بهینه تر و قشنگ تر بشن» — old chats can be deleted, and the features, the display and the way processes
are shown become a little more efficient and nicer. The owner saw this mockup and approved it as is.

## Conversation list (the History view)

- A search box at the top: «جست‌وجو در عنوان‌ها» / "Search titles", with a search icon.
- Conversations grouped under small muted captions: «امروز» Today, «دیروز» Yesterday,
  «۷ روز گذشته» Previous 7 days, «قدیمی‌تر» Older (by the browser's local date of `updatedAt`).
- Each row: message icon, title (one line, ellipsis), time under it in 11px muted; the open conversation
  has the soft accent background. A trash button at the row's end, faint until the row is hovered or
  focused (always visible on touch screens).
- Trash → the row is replaced in place by a soft-danger confirm strip: «این گفت‌وگو حذف شود؟» with
  «حذف» (danger) and «نه». Nothing is deleted until «حذف».
- «نمایش بیشتر» "Show more" centred under the list when the last page was full.
- At the bottom, separated by a hairline: «حذف همهٔ گفت‌وگوها» in danger colour with a muted note
  «کارهای انجام‌شده برنمی‌گردند» ("what was done is not undone"); it asks for confirmation the same way.

## The conversation

- **Activity block.** The tools of one stretch of a turn sit in one bordered block. While working it is
  open and live; once the turn is over it collapses to one line: a checks icon (success colour),
  «۳ کار انجام شد · ۲٫۴ ثانیه» "3 actions done · 2.4 s", and a chevron. Opening it lists each action
  with its category icon, its person-readable name and its own duration in 11px muted.
- **Assistant text** keeps its formatting (now with bold, numbered lists, headings and simple tables)
  and gets a small copy button under it; code blocks get their own copy button.
- **Executed plan card, compact.** A finished plan shows as a success-tinted header «برنامه اجرا شد · ۲ گام»
  "Plan ran · 2 steps" with «جزئیات» "Details" and a chevron, and under it the resource links as small
  accent chips with an external-link icon. Details opens the full steps. Pending cards are unchanged
  (they need the full consent UI).
- **Thinking line.** The three dots in their bubble, then the current activity's icon and label —
  «در حال خواندن لاگ‌های استقرار…» — and, after five seconds, the elapsed seconds in 11px muted.
  With nothing running the label is «در حال فکر کردن…» "Thinking…".
- **Composer.** While an answer is coming, the send button becomes «توقف» "Stop" (danger outline,
  stop icon). The lock button stays.
- **Header.** A widen button (horizontal-arrows icon) before History and New.
