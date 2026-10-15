# H7 — The Persian guide catches up with round seven (docs only)

Round seven added to the in-panel assistant: deleting conversations, a stop button, people-readable activity
blocks, plans that stay in a reopened conversation, richer text, copy buttons, a wider drawer and an
owner-set conversation retention. What was built is in `.superpowers/sdd/r7/{s1,s2,p1,p2,p3}-report.md` —
read them; the guide says what the product does, not what a brief hoped.

## Write

In `docs/tutorial/08-ai.md`, section «دستیار هوشمند پنل», plain Persian in the existing voice — short
paragraphs and lists, no rewrite of what is still true:

1. «کشو»: the list now has search, date groups and «نمایش بیشتر» (replace "the last 30" — it is 30 per
   page now); the widen button.
2. A new subsection «پاک کردن گفت‌وگوها»: deleting one (trash, confirm in place), «حذف همه», what is not
   undone (what the assistant did; the audit log keeps every executed step), what cannot be deleted while
   it is still answering or a plan is running, and that deleting the conversation you are reading starts a
   new one.
3. «جواب، رفتن و برگشتن»: the Stop button — what it keeps (what was written so far), what it never
   interrupts (a plan already running), that it works after a reload too; the activity block («۳ کار انجام
   شد · …») and what the thinking line says; executed plans and their links stay when you come back.
4. Setup for the owner (Settings → assistant): «نگه‌داری گفت‌وگوها (روز)» — blank or 0 keeps them, the
   daily sweep, the audit log kept.
5. `README.md` / `README.fa.md`: only if their assistant paragraph now says something false.

## Check

Tests that pin the tutorial (`tests/Harbora.Tests/AssistantGuideTests.cs`, anything matching `08-ai`,
`HelpMap`, `LearningLibrary`) pass; then the full suite once (CONSTRAINTS recipe).

Report: `.superpowers/sdd/r7/h-report.md` (`git add -f`), committed.
