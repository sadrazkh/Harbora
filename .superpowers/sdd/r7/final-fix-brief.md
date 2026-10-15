# Round seven — final-review fix pass (one agent, the whole list)

Both final reviews said "Ready with fixes": nothing Critical. Fix everything below on the integration branch
`r7/int` in `E:/Cash.Net/source/repos/sadrazkh/Harbora-r7-int` (head 63a39b0). The full suite on that head
was already green (7426), so the server review's "Important 1" (run the suite) is done — run it again after
your changes. CONSTRAINTS (`.superpowers/sdd/r7/CONSTRAINTS.md`) bind you: author **and committer**
`sadra zadeh khameneh <1986.aandrii@gmail.com>`, no trailer; branch-named logs; never npm and dotnet at once.

## Pane

1. **(Important) A conversation deleted elsewhere stays on screen, and sending to it shows contradictory words.**
   `assistantSession.js` ~582–591 (read-back after a failed send) and ~180–185 (the poller's `onGone`). A 404
   on the send means it was not sent; a 404/403 on the read-back or the poll means the conversation is gone.
   Then: `session.forget(id)` (new empty conversation, remembered id cleared), the typed words back in the
   composer (`restoreDraft`), and a gentle notice «این گفت‌وگو حذف شده است — پیامتان در گفت‌وگوی تازه
   است، دوباره بفرستید.» / "This conversation was deleted — your message is back in the box; send it again
   to start a new one." Never "may not have been sent" for a 404. Node tests for the send path and the poll.
2. **Delete all can forget a conversation the server did not delete.** `assistantHistory.js` ~471–475: take the
   "certainly gone" shortcut only for the id that was open **at the press**; any other open id is asked
   about with a GET (as the `skipped > 0` path does). Update the test at `assistant-history.test.mjs` ~690.
3. **A Stop that lands before a gate refusal** (`started, stopped, done`, no error) leaves a waiting card marked
   superseded. When a turn ends with `stopped` and produced nothing (no text, no tool, no plan), re-read and
   apply `restoreSuperseded` as the gate-refusal path does. Test.
4. Search box `maxlength="100"` (`AssistantHistory.vue` ~156).
5. Live consistency after a Stop: the server will now emit a `plan` event (expired) for a cut-short draft
   (server item 5 below) — make sure the timeline draws a non-pending `plan` event as its compact
   «منقضی شد — هیچ کاری اجرا نشد» card, and that the proposal lines of that plan are hidden like on reopen.
   Test.

## Server

1. Startup reconciler: the cut-off skipped steps put the restart words in the **summary**
   (`Skipped(step.Index, PanelRestartedEn, PanelRestartedFa)`, `AgentPlanService.cs` ~668) like every other
   skipped result; update `AgentPlanStartupReconcilerTests.cs` ~94.
2. The reconciler's audit read adds `&& a.CreatedAt >= (plan.DecidedAt ?? plan.CreatedAt)` so the CreatedAt
   index carries it (`AgentPlanService.cs` ~647).
3. The reconciler does not touch the conversation's `UpdatedAt` (it must not lift old conversations to the
   top of the list or restart their retention clock). Test.
4. Register `AgentPlanStartupReconciler` above the job gate (`DependencyInjection.cs` ~165, per its own
   comment) — or, if it must stay below, a comment saying why it is exempt.
5. A cut-short draft recorded as expired (`AgentConversationService.cs` ~855) also emits a `plan` event with
   its expired `ViewOf`, before `stopped`/`error`, so live and reopened tell the same story. The stored
   `stopped` partial has no "You stopped this answer." marker on reopen — say so in the
   `AgentConversationView` doc rather than adding a column. Test the event order.
6. Dead and duplicated rules: remove `AgentPlanService.PendingPlanAsync` and `ListAsync(actor, ct)` if only
   tests use them (move those tests to the live methods); build the "plan running" half of busy once
   (`Idle` from `Running`, or one helper both use) and make the turn half at
   `AgentConversationService.cs` ~476 use the same helper as `IsTurnLive`. Behaviour unchanged; tests green.
7. Contract docs (text only, no shapes): `AgentStopView` — false also means "finishing, can no longer be
   stopped"; `Stopped` — never follows a claimed plan; `AgentPlanHistoryView.AfterMessageId` — never null in
   practice (say when it could be).

## Docs and views

1. `docs/tutorial/08-ai.md` ~292: a reopened superseded plan reads «منقضی شد — هیچ کاری اجرا نشد»; the
   «اجرا نشد — پیام تازه‌ای فرستادید» wording is only while the conversation stays open.
2. `08-ai.md` ~282: after a reload, Stop ends the answer and the conversation is read again to show what
   was saved — no «این پاسخ را متوقف کردید.» line and no «دیگر قابل توقف نیست…» notice there.
3. Add one sentence for pane item 1 (a conversation deleted in another tab).
4. `Views/Settings/Index.cshtml` ~172 comment: 0–3650 (0 = keep for ever).

## Done means

Node tests, `npm run build`, then the full dotnet suite (CONSTRAINTS recipe) — all green, numbers reported.
Commit in small commits with prose messages. Report to `.superpowers/sdd/r7/final-fix-report.md`
(`git add -f`): each item → what changed, file:line, test; commands and outputs.
