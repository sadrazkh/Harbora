# Round seven — final-review fix pass: report

Branch `r7/int`, worktree `E:/Cash.Net/source/repos/sadrazkh/Harbora-r7-int`, from head `63a39b0`.
Every commit is authored and committed by `sadra zadeh khameneh <1986.aandrii@gmail.com>`, no trailer. Nothing pushed.

Status: DONE_WITH_CONCERNS (the concerns are readings of two brief items, listed at the end; nothing is unverified
except what is said to be).

## Commits (oldest first)

| Commit | Items |
|---|---|
| `91d9243` | Pane 1 — a conversation deleted elsewhere is let go of (send 404, read-back / poll 404) |
| `d8f6ff9` | Pane 2 — delete all's "certainly gone" shortcut only for the id open at the press |
| `fbac969` | Pane 3 — a bare Stop (started, stopped, done) re-reads and restores superseded cards |
| `116b5b2` | Pane 4 — search box `maxlength="100"` |
| `05fa5ee` | Server 1, 2, 3 — reconciler: restart words as skipped summary, audit read bounded by the claim, conversation not touched |
| `39e3fab` | Server 4 — reconciler registered above the job gate |
| `a85f13b` | Server 3 (doc) — the retention sweeper's doc says the reconciler does not move `UpdatedAt` |
| `4106332` | Server 5 — a cut-short draft is also sent live as an expired `plan` event; `AgentConversationView` / `Plan` / `Stopped` docs |
| `7fb621b` | Server 6 — dead methods removed, busy rules written once |
| `65b7261` | Server 7 — `AgentStopView`, `AfterMessageId` docs |
| `e228b6c` | Pane 5 — node tests for the live expired `plan` event |
| `5d7788c` | Docs 1, 2, 3 — `docs/tutorial/08-ai.md` |
| `a4d6e72` | Docs 4 — `Views/Settings/Index.cshtml` comment |
| (this report) | |

Only the final head was built and run in full (below). The intermediate commits were not each built on their own;
`4106332`, `7fb621b` and `65b7261` were split from one working tree by file content (`AgentConversationService.cs` and
`AgentViews.cs` carried changes for more than one item), and each part was checked by reading its diff before it was
committed.

## Pane

**1. A conversation deleted elsewhere (Important).**
- `assistantApi.js:367` — a send answered 404 *for a conversation id* returns `{ completed: false, gone: true }` and
  reports no `error` event (as 409 "busy" already did). A 404 to `new` is still reported as before (no conversation to be gone).
- `assistantSession.js:78–97` — `CONVERSATION_GONE` with the brief's words verbatim
  («این گفت‌وگو حذف شده است — پیامتان در گفت‌وگوی تازه است، دوباره بفرستید.» / "This conversation was deleted — your
  message is back in the box; send it again to start a new one."), `kind: 'gone'`; `isGone(error)`.
- `assistantSession.js:392` `conversationGone(id, notice)`: `session.forget(id)` (new empty conversation, remembered id
  and waiting words cleared), the notice, `onChange('gone')`.
- Send path: `:614` a `gone` send → `conversationGone` + `{ sent: false, restoreDraft }`, no read-back. `:641` a read-back
  (after a send with no `started`) that is gone → the same; never `UNSURE_SENT` / `NOT_SENT` for it. The `error` line the
  failed send drew goes with the old conversation, so nothing on screen contradicts the notice.
- Poll path: `:129` the poller passes its id to `onGone`; `:211` a gone poll forgets the conversation if it is still the one
  on screen. If the person's words had been handed back to the box (the notice was `busy` or `restored`), the brief's
  notice; otherwise `CONVERSATION_GONE_QUIETLY` («این گفت‌وگو حذف شده است.» / "This conversation was deleted.") — see concern 2.
  A poller that only failed to read (no 404/403) leaves the conversation where it is, as before.
- `AssistantPanel.vue:65` — on `gone` the lock-button form closes, as after a delete from the list.
- Tests: `assistant-api.test.mjs:406, :413`; `assistant-session.test.mjs:659` (send 404: forgotten, words back, exact
  words in both languages, no GET, next message creates a new conversation), `:685` (read-back 404), `:699` (read-back 403
  gone; 403 `feature_not_enabled` is not), `:724` (poll 404 after "still answering"), `:740` (poll 404 with nothing
  waiting), `:752` (unreadable is not deleted). Mutation check: with the three `gone` branches disabled, 5 of these fail.

**2. Delete all forgetting a conversation the server kept.** `assistantHistory.js:454` reads the open id at the press;
`:478` takes the shortcut only when nothing was skipped *and* the id open now is that one; any other open id goes to the
GET. Doc above it rewritten. Tests: `assistant-history.test.mjs:691` replaces the old test at ~690 (an id opened while
the request was out is asked about: 404 → forgotten, 200 → not), `:713` (the press-time id with nothing kept: forgotten,
no GET). Mutation check: without `open === openAtPress`, `:691` fails.

**3. A Stop before a gate refusal.** `assistantSession.js:560, :583` `stoppedBare` — `stopped` after `started` with no
`tool`/`delta`/`message`/`plan`/`executed` before it; `:662` the re-read + `restoreSuperseded` now runs for it as for a gate
refusal. Tests: `assistant-session.test.mjs:602` (server still has the plan pending → card pending again, stop line kept,
one extra GET), `:618` (server did withdraw it → stays superseded), `:631` (text, a tool or a plan before the Stop → no
re-read). Mutation check: without `stoppedBare` in the condition, `:602` and `:618` fail.

**4. Search box.** `AssistantHistory.vue:157` `maxlength="100"` with a comment naming `MaxSearchLength`. Test:
`assistant-history.test.mjs:994` reads the limit from `AgentConversationService.cs` and the attribute from the `.vue`.

**5. Live consistency after a Stop.** No pane code change was needed: `applyStreamEvent('plan', …)` already makes a card
from any status, and `planCardLine` folds an expired one (not superseded) to «منقضی شد — هیچ کاری اجرا نشد» with its steps
behind «جزئیات». Tests: `assistant-timeline.test.mjs:801` — the live stream of a cut-short turn (`started, delta, message,
plan(expired), stopped, done`) gives `user, assistant, plan, stopped`, no tool line; the card and the one `loadConversation`
draws for the same plan from `plans` are alike (not superseded, the compact line in both languages, not actionable, not
"ran"). `:834` — an older waiting card is the one marked superseded, the turn's own expired card is not. See concern 1 on
"proposal lines".

## Server

**1. Restart words as the summary.** `AgentPlanService.cs:653` `Skipped(step.Index, PanelRestartedEn, PanelRestartedFa)`;
its outcome line no longer carries an `error` either, like every other skipped step's. Test:
`AgentPlanStartupReconcilerTests.cs:88` (step 3: `SummaryEn/Fa` are the restart words, `ErrorEn/Fa` null).

**2. Audit read bounded by the claim.** `AgentPlanService.cs:700` `StepEntriesOf(db, plan)` (internal, so its SQL can be
checked): `&& a.CreatedAt >= since`, `since = plan.DecidedAt ?? plan.CreatedAt` computed outside the query. Tests:
`AgentPlanStartupReconcilerTests.cs:188` (Npgsql `ToQueryString` contains `"CreatedAt" >=`), `:201` (an entry dated before
the claim is not the run's). The test helper now dates its audit entries after the claim explicitly instead of relying on
the real clock being later than `AgentHarness.Start`.

**3. The conversation's `UpdatedAt` is not touched.** `AgentPlanService.cs:690` `touch: false`; `:1086` the one static
writer touches only when asked (every other caller passes `touch: true`). Test: `AgentPlanStartupReconcilerTests.cs:173`
(conversation last used 20 days before, restart 3 days after: `UpdatedAt` unchanged, the outcome lines still written).
Mutation check: with `touch: true` and the `CreatedAt` bound removed, `:173`, `:188` and `:201` fail.
`AgentConversationRetentionSweeper.cs:26` doc updated to match.

**4. Registration.** `DependencyInjection.cs:165` — above `JobStartupGateOpener` (`:169`), with the other startup
reconcilers. Test: `DependencyInjectionTests.cs:70` (index below the opener).

**5. Live `plan` event for a cut-short draft.** `AgentConversationService.cs:853` passes `emit`; `:1107` after the plan is
stored and superseded, and only if it is now `Expired`, a `plan` event with `plans.ViewOf(plan)` — inside the existing
never-throws `try`, before `RunTurnAsync` rethrows, so before `SendAsync` writes `stopped` or the deadline/shutdown/failure
`error`. Docs: `AgentViews.cs:105` (`AgentConversationView` remarks: the stored partial has no "You stopped this answer."
marker on reopen, no column records a stop), `:169` (`Plan`), `:185` (`Stopped`). Tests (event order):
`AgentStopTests.cs:326` (`started, delta, message, plan, stopped, done`; the live plan is equivalent to the one the reopened
view returns), `:370` (`started, plan, error, done` for the deadline), `:401` (a Stop during that recording: still
`started, plan, error, done`).

**6. Dead and duplicated rules.** Removed `AgentPlanService.PendingPlanAsync` and the one-argument
`AgentConversationService.ListAsync(actor, ct)`; their only callers were tests, now on `PendingAmong(PlansOfAsync(…))`
(`AgentPlanServiceTests.cs:147`) and `ListAsync(actor, null, 0, ct)` (`AgentConversationServiceTests.cs`, `AgentTurnTests.cs`;
the assertion that the old overload equals the first page is gone with it). Busy: `AgentConversationDeletion.cs:131`
`Running(cut)` is the plan half — `Idle` (`:124`) inlines it into its subquery, and the refused delete's reason
(`AgentConversationService.cs:478`) asks it for one conversation. `AgentBusyCut.TurnLive` (`AgentConversationDeletion.cs:43`)
is the turn half over a mark already read, used by `IsTurnLive` (`AgentConversationService.cs:341`) and by the refused
delete (`:472`) — the same inequality as before (`now − started < lifetime` ⇔ `started > now − lifetime`). The existing
Npgsql SQL-shape test for `Idle` (EXISTS, `AgentPlans`, `TurnStartedAt`, COALESCE) still passes, so EF inlines the shared
expression; behaviour unchanged, every existing deletion/busy test green.

**7. Contract docs (text only).** `AgentViews.cs:30` `AgentStopView` — false also means finishing, no longer stoppable;
`:185` `Stopped` — never follows a claimed plan; `:123` `AfterMessageId` — never null from this server, and the one case
it could be (a plan with nothing in the view to anchor it, which the server leaves out).

## Docs and views

`docs/tutorial/08-ai.md`:
1. `:292` — the cancelled/expired line no longer lists «اجرا نشد — پیام تازه‌ای فرستادید» as a reopen wording: it is only
   while the conversation stays open; reopened, a superseded plan reads «منقضی شد — هیچ کاری اجرا نشد».
2. `:282` — after a reload Stop ends the answer and the conversation is read again to show what was saved; no
   «این پاسخ را متوقف کردید.» line and no «دیگر قابل توقف نیست…» notice there.
3. `:268` — one sentence: a conversation deleted in another tab is replaced by a new one, the message back in the box, with
   the notice quoted.
- Also `:279` (not in the brief, follows from server item 5): the cut-short steps now appear at once as the one
  «منقضی شد — هیچ کاری اجرا نشد» line, as on reopen.

4. `Views/Settings/Index.cshtml:172` — comment says 0–3650 (0 = keep for ever), which is what
   `AgentSettingsService` checks.

## Commands and outputs (final head)

Run one after another, never two at once, on head `a4d6e72` (the code; this report is the only commit after it).

```
$ node --test src/Harbora.Web/Scripts/tests/*.test.mjs > $TEMP/test-r7-int-node.log 2>&1; echo NODE_EXIT=$?
NODE_EXIT=0
# tests 500
# pass 500
# fail 0
# cancelled 0
# skipped 0

$ cd src/Harbora.Web && npm run build > $TEMP/test-r7-int-npm.log 2>&1; echo NPM_EXIT=$?
NPM_EXIT=0
✓ 1867 modules transformed.   …   ✓ built in 6.12s

$ export MSBUILDDISABLENODEREUSE=1; LOG=$TEMP/test-r7-int.log
$ dotnet build Harbora.slnx --no-restore --nologo -v q -p:UseSharedCompilation=false > "$LOG.build" 2>&1; echo BUILD_EXIT=$?
BUILD_EXIT=0
$ grep -c "warning CS" "$LOG.build"
0
$ dotnet test Harbora.slnx --nologo -v q --no-build > "$LOG" 2>&1; echo EXIT=$?
EXIT=0
$ grep -E "^Passed!|^Failed!|\[FAIL\]" "$LOG"
Passed!  - Failed:     0, Passed:   500, Skipped:    17, Total:   517, Duration: 30 s - Harbora.NodeAgent.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    15, Skipped:     0, Total:    15, Duration: 51 s - Harbora.NodeIngress.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:  7429, Skipped:     0, Total:  7429, Duration: 1 m 44 s - Harbora.Tests.dll (net10.0)
$ grep -c "Harbora.Tests.dll" "$LOG"
2
(also in the log: Skipped! - Failed: 0, Passed: 0, Skipped: 115, Total: 115 - Harbora.Postgres.Tests.dll — no Docker here)
```

Against the baselines on `63a39b0`: Harbora.Tests.dll 7426 → 7429 (+3 reconciler tests; the DI check is a new assertion in
an existing test; the stop tests were changed in place); node 485 → 500 (+2 api, +9 session, +2 history (one test replaced
by two, one new), +2 timeline); npm build exit 0; zero CS warnings. While iterating I ran only filtered tests (the
reconciler class; then `Harbora.Tests.Agent|DependencyInjectionTests|Assistant`, 922 passed) and the mutation checks named
above; the full suite ran once, at the end.

## Concerns

1. **Pane item 5, "the proposal lines of that plan are hidden like on reopen".** I read "proposal lines" as the card's
   proposed steps: on reopen an expired card shows them only behind «جزئیات», and the live expired card now does the same
   (tested). If the stored "Proposed as step N" *tool lines* were meant: the server sends no live event for a proposal, so
   none is drawn live; on reopen those stored lines are still drawn for every plan without a result card (expired, rejected,
   pending) — P2's approved rule, unchanged here. Hiding them on reopen would be a change to `loadConversation` and to the
   P2 test "a plan without a result keeps the lines it wrote"; I did not make it.
2. **Pane item 1, two small additions to the brief's wording.** (a) When the poller finds the conversation gone and no
   words of the person's were waiting (a conversation only being followed after a reload), the brief's notice would say
   "your message is back in the box" with nothing there; that case says only «این گفت‌وگو حذف شده است.» / "This conversation
   was deleted." (the brief's first clause). (b) A 403 counts as gone except `feature_not_enabled`: on these endpoints a 403
   is the workspace's assistant being switched off, and calling that "deleted" would be false. Every 404 is gone, as asked.
3. The Postgres lane (`tests/Harbora.Postgres.Tests`) needs Docker and was not run here; nothing in it was changed. Its
   race tests use the busy rules that item 6 rewrote — the rewrite is behaviour-preserving and the Npgsql SQL shape is
   checked locally, but the lane itself is unverified locally.
