# H7 — The Persian guide catches up with round seven — report

Branch `r7/h`, worktree `E:/Cash.Net/source/repos/sadrazkh/Harbora-r7-h`, based on the integration branch (HEAD `e81b863`,
which carries S1, S2, P1, P2 and P3 together). Docs, plus one test that pins the new figures.

**Status: DONE_WITH_CONCERNS** (concerns at the end; none is a failing test).

Commits (owner identity, no trailer; checked with `git log --format='%an <%ae> | %cn <%ce>'`):

```
3e82e55  The assistant guide catches up with round seven: …
(next)   this report
```

## What I read, and which source won

I read the brief, CONSTRAINTS, the five reports (all fix passes) and then the code in this worktree for every claim
the guide now makes. Where a report and the code disagreed or were silent, the code decided:

- The pane's strings (`AssistantHistory.vue`, `assistantHistory.js`, `assistantActivity.js`, `AssistantPanel.vue`,
  `assistantSession.js`, `AssistantResultCard.vue`) for every quoted Persian label: «نمایش بیشتر», «حذف همهٔ گفت‌وگوها»,
  «همهٔ گفت‌وگوها حذف شوند؟», «حذف همه», «۷ روز گذشته», «در حال توقف…», «توقف انجام نشد — دوباره امتحان کنید»,
  «در حال کار…», «۳ کار انجام شد · ۲٫۴ ثانیه», «برنامه اجرا شد · ۲ گام», «جزئیات», «لغو شد / منقضی شد — هیچ کاری اجرا نشد».
- The server's strings in `AgentConversationService.BusyReasonAsync` (the two 409 sentences) and
  `AgentPlanService.PanelRestartedFa` (the restart sentence).
- `app.css` for the drawer (420 px, `is-wide` 40 rem from 640 px up), `AgentSettingsService` (retention 1–3650, blank/0 =
  keep), `AgentConversationRetentionSweeper` (daily, first pass 10 min after start, every workspace, audit row, busy ones
  kept), `AgentConversationDeletion` (busy = live turn mark or an executing plan claimed < 30 min ago),
  `AgentPlanStartupReconciler` (registered in `DependencyInjection.cs`).

## What I changed in `docs/tutorial/08-ai.md` and why

Only what round seven made false or incomplete; everything else is untouched.

1. **Intro of «دستیار هوشمند پنل»** — one added sentence: Stop exists, conversations can be deleted.
2. **Owner setup** (item 6 list + a short paragraph after «تست اتصال»): the fourth box «نگه‌داری گفت‌وگوها (روز)» — blank
   or `0` keeps them (the default), whole days 1–3650, Persian digits accepted; how the sweep works (daily, first pass
   shortly after start, change takes effect at the next pass, every workspace and person, busy ones kept and re-checked next
   pass, messages and plans go with the conversation, the audit log stays and each pass that deleted something has its own
   audit row with counts only).
3. **The «دستیار تازه است» block-quote** — a second paragraph: delete, Stop, the activity block and automatic retention were
   tested only with the scripted provider and automated tests, not a real model; deleting concurrently with an approval
   depends on Postgres row locks and is only exercised by the Postgres-lane test, which is unproven until it runs against
   a real Postgres. (I did not say whether CI has run it — I do not know.)
4. **Consent table, «خواندن» row** — reads now collect into the «بلوک فعالیت» (pointer only; the table is still 3 tables,
   which `AssistantGuideTests` pins).
5. **«بعد از زدن اجرا»** — three changes: a plan whose every step is done becomes one compact card
   «برنامه اجرا شد · ۲ گام» with the links under the title and the steps behind «جزئیات» (a failed step keeps the full card);
   a plan the panel's restart interrupted is settled at startup (done steps stay done, the running step fails with the
   quoted sentence, the rest are refused) instead of staying «executing»; cancelled/expired cards shrink to one line.
   The `**۱۵ دقیقه**` figure the test pins is untouched.
6. **«کشو»** — the 30-row claim replaced: 30 per page, newest first, «نمایش بیشتر» brings the next 30; the four groups
   (امروز / دیروز / ۷ روز گذشته / قدیمی‌تر) by the browser's calendar, time for today/yesterday and date otherwise (Persian
   calendar in the Persian UI); title-only, case-insensitive, debounced search and what Esc does in it; the «پهن‌تر» button
   (about 420 → 640 px, remembered, absent below 640 px). The pinned phrase «۳۰ گفت‌وگوی آخر» is still in the text and still true.
7. **New subsection «پاک کردن گفت‌وگوها»** — deleting one (trash, in-place «این گفت‌وگو حذف شود؟», «حذف» / «نه», the row
   leaves only after the server says so), «حذف همه» (all of *your* conversations in this workspace, not only search results,
   not colleagues'), what is not undone (what the assistant did; the audit log keeps every executed step and records the
   delete by id only; the day's requests are not given back), what cannot be deleted (a message still being answered; a plan
   still running — with both server sentences, why it can last a few minutes after a crash, a restart-interrupted plan being
   closed at startup, a claim older than 30 minutes no longer blocking), «حذف همه» skipping busy ones and saying how many,
   and that deleting the open conversation opens a fresh empty one.
8. **«جواب، رفتن و برگشتن»** — the thinking line now names the activity («در حال خواندن لاگ‌های استقرار…») and counts
   seconds after 5 s; the «ارسال» bullet now says the Send button is **توقف** while an answer is coming; the 3-minute bullet
   says half-collected steps are recorded as expired; new bullets: **توقف** (what it keeps, the cut read, the expired
   draft, the already-spent requests, «در حال توقف…» and its retries and the «توقف انجام نشد» notice, that it works after
   a reload or when you open a conversation still being answered), **آنچه توقف قطع نمی‌کند** (a plan already running —
   approved or card-less — and an answer already complete), **بلوک فعالیت** (open while working, one line when done,
   failed stays open and red, what each line shows, how it reopens and what older conversations show), **برنامه‌ها می‌مانند**
   (executed → compact card with links, cancelled/expired → one line, failed/refused → full card; the 200-message / 50-plan
   window; a pending card always), **متن جواب و کپی** (bold, numbered lists, headings, tables as plain formatting; links and
   HTML are shown as text; «کپی پاسخ» and «کپی کد»).
9. **«سقف‌ها»** — the daily-request bullet now says Stop and a later delete do not give requests back.

`README.md` and `README.fa.md` — **not changed**: their assistant paragraphs say nothing round seven made false (streamed
answers kept across a reload, approvals, lock button, per-workspace daily count, "tested against scripted fakes" all still
hold). `docs/tutorial/README.md` says the assistant section was written from source, not a live panel; that is still the
case for this update too.

## The one change outside the guide

`tests/Harbora.Tests/AssistantGuideTests.cs` — one new fact,
`The_guides_figures_for_paging_deleting_and_reopening_are_the_ones_the_assistant_runs_on`. The existing test's own doc
comment is the reason: a figure the guide states must be read from the constant the code runs on. It pins:
`ConversationListSize` («۳۰ تای بعدی»), `ConversationViewMessages` («آخرین ۲۰۰ پیام»), `PlanHistorySize`
(«حداکثر ۵۰ تای آخر»), `AgentConversationDeletion.ExecutingPlanAbandonedAfter` («بیش از ۳۰ دقیقه پیش»),
`AgentSettingsService.MaxConversationRetentionDays` («از ۱ تا ۳۶۵۰ باشد») and `AgentPlanService.PanelRestartedFa` quoted
verbatim. Mutation check: I changed «۳۰ دقیقه» to «۴۰» and «۳۶۵۰» to «۳۶۰» in the chapter → that test failed
(`Failed: 1, Passed: 3`), the file was restored from a copy and the test is green again. No existing assertion was edited.
This is the only non-doc change; say if you would rather it were not in this commit.

## Commands and what they printed

```
cd Harbora-r7-h ; export MSBUILDDISABLENODEREUSE=1
dotnet restore Harbora.slnx --ignore-failed-sources              → RESTORE_EXIT=0
dotnet build tests/Harbora.Tests/Harbora.Tests.csproj --nologo -v q --no-restore -p:UseSharedCompilation=false
                                                                 → BUILD_EXIT=0, 7 warnings (all NU1900), 0 errors

# while iterating
dotnet test tests/Harbora.Tests --no-build --nologo -v q --filter "FullyQualifiedName~AssistantGuideTests|…~LearningLibrary|…~LearningCensus|…~HelpMap|…~LearnHttp|…~LearningCentre"
                                                                 → EXIT=0  Passed! 31/31  (twice: before and after the last wording edit)
# mutation (two figures broken in the chapter, then restored)
dotnet test … --filter "FullyQualifiedName~AssistantGuideTests"   → EXIT=1  Failed: 1, Passed: 3  (the new fact)

# commit 3e82e55 (docs + the one test), then the full suite once, at the end
LOG=/tmp/test-r7-h.log
dotnet build Harbora.slnx --nologo -v q --no-restore -p:UseSharedCompilation=false > "$LOG.build" 2>&1
                                                                 → BUILD_EXIT=0; 14 warnings (NU1900/NU1903), 0 errors; compiler warnings (CS/IDE/CA): 0
dotnet test Harbora.slnx --nologo -v q --no-build > "$LOG" 2>&1  → EXIT=0
  Skipped! - Failed: 0, Passed:    0, Skipped: 115, Total:  115 - Harbora.Postgres.Tests.dll   (no Docker)
  Passed!  - Failed: 0, Passed:  500, Skipped:  17, Total:  517 - Harbora.NodeAgent.Tests.dll
  Passed!  - Failed: 0, Passed:   15, Skipped:   0, Total:   15 - Harbora.NodeIngress.Tests.dll
  Passed!  - Failed: 0, Passed: 7421, Skipped:   0, Total: 7421 - Harbora.Tests.dll
grep -c "Harbora.Tests.dll" "$LOG" → 2 (present); grep -c MSB3030 → 0
```

`Harbora.Tests.dll`: **7421** = the branch baseline 7420 + the 1 new fact; none lost. The rendered chapter still has
exactly 3 `<table>`s (that assertion is in the green run).

## Unverified / concerns

1. **Everything in the guide is from the code and the reports, nothing from a running panel.** I did not start the panel or
   the pane; no screenshots were taken (the chapter's section has none, as `docs/tutorial/README.md` already says). Pixel
   figures (420 → 640) are CSS values and are not pinned by a test — they can drift without the build noticing.
2. **The Stop wording is deliberately honest about a rough edge.** When Stop is pressed over a card-less auto-approved plan
   (or a split second after the answer is complete) the server answers "nothing stopped", and the pane — which cannot tell
   that apart from "the turn was not registered yet" while its own stream is open — retries up to six times and may then
   show «توقف انجام نشد — دوباره امتحان کنید». The guide says that. Whether that notice is the right product wording for
   "it already finished" is a product question for P3/S1, not a docs one.
3. **The two 409 sentences are quoted from private literals** in `AgentConversationService.BusyReasonAsync`, so no test can
   pin them; if the wording changes the guide will quietly drift. (The restart sentence is a public constant and is pinned.)
4. **The Postgres lane is still unverified** (no Docker here), and the guide says so in the block-quote; I did not claim
   whether CI has run it.
5. **The retention "audit row per pass"** is stated as "a pass that deleted something has its own audit row, counts only"
   (S1 fix pass: `assistant.conversations_expired`, actor `system`). I did not name the action code in the guide.
6. Line endings: the working copy is CRLF (`core.autocrlf=true`); git stores LF, as for the rest of the repository.
