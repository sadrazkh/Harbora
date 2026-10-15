# S1 — Deleting conversations, stopping a turn, the paged list, and retention (server) — report

Branch `r7/s1`, worktree `../Harbora-r7-s1`, based on master `44d38d0`. No migration (none was needed:
the cascades from conversation to messages and plans are already in `20261011104149_AgentCore`).

**Status: DONE_WITH_CONCERNS** — everything in the brief is built and tested in `Harbora.Tests`; the six
Postgres-lane facts are written but **skipped on this machine (no Docker) and so unverified here**; the
concerns are in section 8.

Commits (owner identity, no trailer):

```
0256f07 Assistant conversations can be deleted one at a time or all at once, a running answer can be stopped, …
0d08667 A rejection whose conversation was deleted under it answers not found instead of throwing, and the Postgres lane gains the delete races …
(third) the whitespace-only Stop test and this report — the commit that adds this file
```

## 1. What was built

### Endpoints (`src/Harbora.Web/Controllers/AssistantController.cs`)

All under the controller's existing `[Authorize]` + `[RequireFeature(PlatformFeatures.Assistant)]`; the three
writes carry `[ValidateAntiForgeryToken]` (the token in `X-CSRF-TOKEN`, as for every other POST — DELETE too).

| Request | Answer |
|---|---|
| `DELETE /assistant/conversations/{id:guid}` | **204**; **404** `{ error: "not_found", messageEn, messageFa }` (the same body `NoSuchConversation()` already returns); **409** `{ error: "busy", messageEn, messageFa }` — two wordings: a live turn ("…still answering a message — stop it or wait…") or an executing plan ("A plan in this conversation is still running…") |
| `DELETE /assistant/conversations` | **200** `AgentDeleteAllView { deleted, skipped }` |
| `POST /assistant/conversations/{id:guid}/stop` | **200** `AgentStopView { stopped }`; **404** as above |
| `GET /assistant/conversations?q=&skip=` | **200** array of `AgentConversationSummaryView` (unchanged shape, ≤30 rows); **400** `{ error: "invalid", messageEn, messageFa }` for `q` over 100 characters (after trimming), a negative `skip`, or a `skip` that does not bind as an integer (`?skip=abc`) |

### Service (`AgentConversationService`)

- `ListAsync(actor, q, skip, ct)` → `ServiceOutcome<IReadOnlyList<AgentConversationSummaryView>>`. `q` trimmed,
  ≤ `MaxSearchLength` (100); `c.Title.ToLower().Contains(q.ToLowerInvariant())`; `skip` ≥ 0; ordered by
  `UpdatedAt DESC, Id DESC` (the id makes page boundaries stable); untitled reserved rows still excluded. The
  old `ListAsync(actor, ct)` is kept as an overload returning the first page, so existing callers compile.
  The query lives in `internal static Page(...)` so its Npgsql SQL is checked without a database.
- `DeleteAsync(actor, id, ct)` → `AgentDeleteOutcome { Status: Deleted | NotFound | Busy, MessageEn, MessageFa }`.
  Audits `assistant.conversation_deleted`, `targetType "agent_conversation"`, `targetId` = the id, **no
  metadata** (never the title or text).
- `DeleteAllAsync(actor, ct)` → `AgentDeleteAllView`. Every *listed* conversation of this person in this
  workspace (see concern 7 on reserved, untitled ones), busy ones kept and counted. One audit row
  `assistant.conversations_deleted` with `{"deleted":n,"skipped":m}`.
- `StopAsync(actor, id, ct)` → `AgentStopView?` (null = not this person's here).
- Constructor gains two optional parameters at the end, `IAuditLogger? audit = null, AgentTurnRegistry? turns =
  null`, so every existing positional call site (tests, the Postgres lane) still compiles. DI provides both.
- Constants `ConversationDeletedAction`, `ConversationsDeletedAction`, `MaxSearchLength`.

### The shared delete (`src/Harbora.Infrastructure/Agent/AgentConversationDeletion.cs`, new)

One internal static `DeleteIdleAsync(db, ids, eligible, staleBefore, ct)` used by all three deleters (one,
all, retention), so "busy" means the same everywhere: a turn mark younger than the turn-mark lifetime (the
same cut `IsTurnLive`/`TryClaimTurnAsync` make) or a plan in `Executing`. Batches of 500; every read
`IgnoreQueryFilters()` and pinned by the caller's predicate. Relational: one transaction per batch — lock the
plan rows, then the conditional `ExecuteDeleteAsync` (section 2). InMemory: read-then-write that removes
messages, plans and conversations by hand (no database cascade there), as `TryMoveAsync` does.
`AgentDeleteStatus`/`AgentDeleteOutcome` are declared in the same file — new types, no contract file touched.

### Stop (`src/Harbora.Infrastructure/Agent/AgentTurnRegistry.cs`, new; `SendAsync`)

- `AgentTurnRegistry` — a DI **singleton**; a dictionary under one lock: `Register` (after the claim),
  `Release` (removes only its own handle, so a turn that took over a stale mark is not unregistered by its
  predecessor), `Stop(conversationId, workspaceId, userId)` (owner-checked; sets `StopRequested` under the lock,
  then `CancelAsync()` so the Stop request does not wait on Npgsql/HttpClient cancellation callbacks). The class
  doc says plainly that it is in-memory and correct because the panel is one process.
- In `SendAsync`: the turn keeps its deadline+shutdown token `turn` for the preamble (gate, supersede, storing the
  person's message, `started`); `RunTurnAsync` runs on `answering = linked(turn, stop)`. A Stop is its own
  `catch` branch, first in the order (`answering.IsCancellationRequested && stop.StopRequested`), logging at
  Information and emitting no error. After the body the turn is **released first, then** `StopRequested` is
  read, and `stopped { messageEn: "You stopped this answer.", messageFa: "این پاسخ را متوقف کردید." }` is
  emitted before `done`. Because release and Stop share the lock, **`stopped: true` from the endpoint always
  means a `stopped` event on the stream**, and a Stop after the release answers `false`.
- The partial text: the existing `catch (Exception) when (streamed.Length > 0)` → `FinalizePartialAsync` path
  saves what was streamed through the same `AppendAsync` as every assistant message and emits it as `message`.
  `FinalizePartialAsync` now writes **no row** when the text is only whitespace (still emits `message` with
  `text: null` to close the bubble) — for every cut-short call, not only Stop. See concern 4 on "scrub and
  length bound".
- A tool call cut off by Stop: the read throws the caller's cancellation (`RunReadAsync` rethrows it,
  unchanged), so no tool row is written; the assistant row with the unanswered call is dropped by `Sanitize`.
  Tested by asserting on the **next turn's actual request** (every call answered once, no orphan answer).
- Plans: the post-loop section now checks the token once (`ct.ThrowIfCancellationRequested()`) and then stores
  and claims the plan on **no token** (`CreatePendingAsync(…, None)`, `ExecuteAutoApprovedAsync(…, None)`, the
  `executed`/`plan` emits on None). Reason: a store or claim that committed and was then reported cancelled
  would leave an unseen pending plan, or a plan `Executing` with nothing running it — which would make its
  conversation undeletable for good. `RunClaimedAsync` still takes no token. A Stop during an auto-approved
  plan: the plan runs to its end and is recorded; the stream is `…, executed, stopped, done`.
- Model calls already made stay counted (each is reserved before it is made; nothing is refunded) — asserted.
- Stop with nothing running → `{ stopped: false }`, 200.

### The claim and a deleted row

- `SendAsync`: when the claim fails, it re-checks that the row still exists; gone → `NotFound`, never `Busy`.
- `TryClaimTurnAsync` InMemory path: a `DbUpdateConcurrencyException` (the row was deleted after it was read)
  is now a failed claim (entity detached) instead of an exception out of the request.

### `AgentPlanService` (two small hardenings)

- `FinishAsync`: if the plan row is gone (its conversation deleted while it ran — section 2's residual window,
  or InMemory), it logs a warning, writes nothing, and returns the results; nothing is thrown out of the
  approval. The steps' `assistant.step_executed` audit rows are already written by then.
- `RejectAsync`: after a lost move, `FirstOrDefaultAsync` instead of `FirstAsync`, so a rejection racing a delete
  is a 404 `not_found`, not an `InvalidOperationException` → 500.

### Retention

- `AgentSettingsService`: `AgentSettings` gains `ConversationRetentionDaysText` and `ConversationRetentionDays`
  (both defaulted, so positional constructions elsewhere are unchanged); `AgentSettingsInput` gains
  `ConversationRetentionDays = ""`; `Validate(model, fallbacks, limit, conversationRetentionDays = null)` —
  blank → `""`, otherwise digits only (`NumberStyles.None`, Persian/Arabic-Indic digits read), 0–3650 (0 = for
  ever), anything else refused in both languages; `EffectiveRetentionDays(stored)` — unreadable or out of range
  is **0 (keep everything)**, the opposite default to the limit's, because the wrong guess deletes data.
  `MaxConversationRetentionDays = 3650`. Saved with the other agent keys under
  `AgentSettingKeys.ConversationRetentionDays`.
- `SettingsController.UpdateAssistant` takes `agentConversationRetentionDays`, validated before anything is saved
  (as the limit is). `Views/Settings/Index.cshtml`: a text box `inputmode="numeric"` labelled
  «نگه‌داری گفت‌وگوها (روز)» / "Keep conversations (days)", help «خالی یا ۰ یعنی برای همیشه نگه داشته می‌شوند.
  گفت‌وگوهایی که این تعداد روز استفاده نشده‌اند روزی یک بار حذف می‌شوند؛ گزارش ممیزی نگه داشته می‌شود.» /
  "Blank or 0 keeps them for ever. Conversations not used for this many days are deleted once a day; the audit
  log is kept."
- `src/Harbora.Infrastructure/Maintenance/AgentConversationRetentionSweeper.cs` (new) — a `BackgroundService`
  shaped like `DanglingImageSweeper`: public constructor for the container, internal one with settable waits;
  first pass 10 minutes after start, then every 24 h; reads the setting on every run; when > 0 lists
  conversations of **every** workspace with `UpdatedAt < now − days` (`IgnoreQueryFilters()`), deletes them through
  `DeleteIdleAsync` with the cut re-applied inside each delete and the production turn-mark lifetime
  (`DefaultTurnDeadline + TurnMarkGrace`), and logs **one line** — also when off and when it deleted nothing;
  never throws into the host. Registered with `AddHostedService` beside the agent services (below the job gate
  opener), with `AddSingleton<AgentTurnRegistry>()` next to it. Not the config-driven `DataRetentionSweeper`.

## 2. Race analysis

### Delete vs approve

The approval's claim is `UPDATE "AgentPlans" SET "Status" = 1, … WHERE "Id" = @p AND "Status" = 0 AND …`
(`TryMoveAsync`): a row lock on the **plan** row only; it never touches the conversation row, and the FK is not
re-checked because the key column does not change.

**Why one `DELETE … WHERE NOT EXISTS (executing plan)` is not enough.** Under read committed the `NOT EXISTS`
reads the DELETE statement's snapshot. An approval that commits after that snapshot is invisible to the check,
but the `ON DELETE CASCADE` on `AgentPlans` runs its own `DELETE … WHERE "ConversationId" = $1` with a fresh
snapshot (Postgres's RI triggers do that in read committed), waits for the claim's row lock if it is still in
flight, and deletes the plan that has just been claimed. That is the window the brief describes.

**What the delete does instead** (relational, per batch, one transaction — the SQL as Npgsql generates it,
captured with `ToQueryString` on this machine):

```sql
BEGIN;
SELECT "Id" AS "Value" FROM "AgentPlans" WHERE "ConversationId" = ANY(@p0) ORDER BY "Id" FOR UPDATE;
DELETE FROM "AgentConversations" AS a
 WHERE a."Id" = ANY (@ids) AND <the caller's predicate: owner / listed / UpdatedAt < cutoff>
   AND (a."TurnStartedAt" IS NULL OR a."TurnStartedAt" <= @staleBefore)
   AND NOT EXISTS (SELECT 1 FROM "AgentPlans" AS a0 WHERE a0."ConversationId" = a."Id" AND a0."Status" = 1);
COMMIT;
```

(The `DELETE` text is the `ExecuteDeleteAsync` of the predicate whose `SELECT` form was printed; the
`ExecuteDeleteAsync` statement itself is only run in the Postgres lane.)

- **Claim committed before the lock statement** → the lock returns the committed (executing) row; the `DELETE`, a
  new statement with a new snapshot, sees `Status = 1` → 0 rows → `Busy` (409). The conversation and plan stay.
- **Claim in flight when the lock statement reaches its row** → `FOR UPDATE` waits for it; on commit it locks
  and returns the updated version → as above, 409. On rollback the plan is still pending → the delete proceeds
  and the cascade removes the pending plan (which is what "a card waiting for a decision" should do).
  *Postgres lane: `A_delete_that_meets_an_approval_claiming_its_plan_waits_for_it_and_then_refuses`.*
- **Lock statement first** → every plan row of the conversation is locked until commit; the claim's `UPDATE`
  waits; after commit its row is gone → 0 rows → `DiagnoseLostClaimAsync` finds no plan → `not_found` (404),
  nothing ran. *Postgres lane: `An_approval_that_meets_a_delete_waits_for_it_and_then_claims_nothing`* — the
  delete is held open after its `DELETE` by a command interceptor, the approval is started, the test waits
  until `pg_stat_activity` shows a session blocked on a lock, then releases.
- **Deadlocks:** plan locks are taken in id order, so two deletes over overlapping sets lock in the same order;
  the delete never holds a conversation row while it waits on a plan row (plans are locked first), and the paths
  that wait on a plan row (claim, reject, expire) hold no conversation row at that moment. I found no cycle.
- **Residual window.** The lock statement locks the plans that exist at its snapshot. A plan inserted after it
  is not locked. Plans are inserted only by a turn, and only while that turn's mark is live — and the `DELETE`
  refuses a conversation with a live mark. The only way through is a turn that outlives its mark lifetime
  (3-minute deadline + 30 s grace) and *then* stores and claims a plan; the turn's token is cancelled at the
  deadline and is now checked immediately before the plan is stored, so this needs a > 30 s stall between that
  check and the claim. For that case (and for InMemory, where none of this is atomic) `FinishAsync` writes
  nothing, throws nothing, and logs `Plan {Plan} finished as {Status}, but it and its conversation were deleted
  while it ran; its outcome is in the audit log only.` — tested by forcing it in
  `A_plan_whose_conversation_vanishes_while_it_runs_writes_nothing_throws_nothing_and_says_so_in_the_log`.
- **Reject vs delete** — the same shape as the claim; it used to throw after a lost move on a deleted plan; now
  `not_found`. *Postgres lane: `A_rejection_that_meets_a_delete_…_not_a_server_error`.*

### Delete vs send

The message's claim is `UPDATE "AgentConversations" SET "TurnStartedAt" = @stamp WHERE "Id" = @id AND
("TurnStartedAt" IS NULL OR "TurnStartedAt" <= @staleBefore)` — the **same row** the `DELETE` removes, so the
row lock serialises them:

- **Claim first** → the `DELETE` blocks on the row, then re-evaluates its `WHERE` on the committed version
  (read committed's re-check of an updated row), sees a live mark → 0 rows → 409 "still answering". Messages and
  plans the turn then writes have a conversation to belong to.
- **Delete first** → the claim's `UPDATE` waits; after commit the row is gone → 0 rows. `SendAsync` now re-reads
  whether the row exists and answers **`NotFound`** (404), not `Busy`. *Postgres lane:
  `A_message_that_meets_a_delete_waits_for_it_and_is_then_not_found_never_busy`.* InMemory: the claim is a tracked
  save of a row that is gone, which throws `DbUpdateConcurrencyException` — caught as a failed claim; *tested in
  `A_delete_that_lands_between_reading_the_conversation_and_claiming_it_makes_the_message_not_found_never_busy`*
  with a `SaveChangesInterceptor` that runs the delete from another context at the moment of the claim.
- A message to an id deleted earlier is `NotFound` at the first read; a new conversation (`new`) cannot race.

## 3. Tests added

`tests/Harbora.Tests` (all run here, all green):

| Class | Facts/cases | Covers |
|---|---|---|
| `Agent/AgentConversationDeletionTests` | 22 | own delete → messages/plans gone, usage counter untouched, audit has id only and no title; someone else's / same person in another workspace / unknown → NotFound and nothing deleted; live turn → Busy; executing plan → Busy; every non-executing plan status goes with it; stale mark → deleted; message to a deleted conversation → NotFound, nothing sent; delete landing between read and claim → NotFound never Busy; approve after delete → `not_found`, nothing ran; plan vanishing mid-run → writes nothing, throws nothing, logs; delete-all counts (3 deleted, 2 skipped), busy kept, reserved kept, colleague's and other workspace's untouched, one audit row `{"deleted":3,"skipped":2}`; list: substring, case-insensitive, Persian, reserved excluded, paging 37 rows → 30 + 7 + 0 with no repeats and stable ties, 101-char `q` refused (bilingual), 100 and trimmed-100 accepted, negative skip refused; the Npgsql SQL of the search (`lower(…) LIKE`, `ORDER BY`, `LIMIT/OFFSET`) and of the lock/idle check (`FOR UPDATE`, `ANY(`, `ORDER BY`, `EXISTS`); the registry (owner check, takeover, release) |
| `Agent/AgentStopTests` | 6 | mid-stream → `started, delta, delta, message, stopped, done`, partial stored, mark cleared, usage 1, a second Stop false, the next turn's real request valid and carries the partial as an assistant message; whitespace-only → `message` with null text, no row; Stop during a read → no tool row for the cut call, next request valid with the cut exchange dropped; Stop at `started` → `started, stopped, done`, the person's message kept; nothing running → false; someone else's / other workspace / unknown → null (404); Stop during an auto-approved plan → `started, message, executed, stopped, done`, plan `Executed` with results and its tool message, one `step_executed` audit |
| `Agent/AgentConversationRetentionTests` | 32 | sweep over a context whose workspace filter hides every row: deletes old idle conversations in two workspaces (incl. an old reserved one and a dead-mark one), keeps recent / live-turn / executing, messages and plans go too, one Information line with the counts; blank / "" / 0 / unreadable → nothing deleted, one "off" line; nothing old → one line "deleted 0"; a failing run → one Error line, null, no throw; the timer's first pass after the startup delay; DI: registered once as a hosted service, buildable by `ActivatorUtilities`, `AgentTurnRegistry` a singleton; validation accepted (8 cases incl. Persian digits, 0, 3650) and refused (7 cases incl. 3651, -1, +5, 1.5, 1,000, overflow) in both languages; `EffectiveRetentionDays` (7 cases); save and read back |
| `Http/AssistantConversationHttpTests` | 8 | DELETE → 204, GET then 404, gone from the list, messages/plans gone, the `step_executed` audit row stays, the delete's audit has no text, second DELETE 404; DELETE one / DELETE all / POST stop without the header → 400 and nothing deleted; someone else's → 404 `not_found` with both messages (DELETE and stop); live turn and executing plan → 409 `busy` with both messages; delete-all → `{deleted:2, skipped:1}`, colleague untouched, audit row; list `q` (trimmed, case-insensitive) and `skip` paging 30 + 2, row shape `id/title/updatedAt`, 400 `invalid` for 101-char `q`, `skip=-1`, `skip=abc`; Stop with nothing running → 200 false; Stop mid-stream over real HTTP → 200 true and the stream ends `…message, stopped, done` with the bilingual stopped text, the conversation then not in progress and holding the partial answer |
| `Http/AgentSettingsPageHttpTests` | +4 | the box renders; "۹۰" saved as "90" and shown back; 3651 / -1 / "a year" refused visibly with nothing saved |

`AgentTestKit.AgentHarness` gains one shared `Turns` registry and passes `Audit` and `Turns` to every service it
builds (as the container shares one singleton across requests).

`tests/Harbora.Postgres.Tests/AgentConversationDeleteRaceTests.cs` (new, 6 facts) — **compiled, skipped here, not
run**: delete vs in-flight claim; claim vs held delete; reject vs held delete; message vs held delete; delete-all
with the real cascade (busy and other people's kept); the list search in Postgres (case, Persian, `%` and `_`
searched for literally, reserved excluded, paging).

**Mutation check.** With two deliberate breaks — the sweeper's candidate query without `IgnoreQueryFilters()`,
and the `stopped` emit disabled plus the claim-failure existence re-check disabled — 8 of the new tests failed
(the sweep's, the timer's, five Stop tests and the between-read-and-claim delete test); the source was restored
with `git checkout --` and the tests are green again.

## 4. Commands run and what they printed

(`/tmp` below is Git Bash's temp directory; logs carry the branch name.)

```
dotnet restore Harbora.slnx --ignore-failed-sources            → RESTORE_EXIT=0 (5 projects restored)

export MSBUILDDISABLENODEREUSE=1
dotnet build Harbora.slnx --nologo -v q --no-restore -p:UseSharedCompilation=false > /tmp/test-r7-s1.log.build
                                                               → BUILD_EXIT=0; grep -c "warning (CS|IDE|CA)" → 0
  (the only warnings are NU1900 — the unreachable private feed — and NU1903 on SSH.NET in Harbora.Postgres.Tests,
   both pre-existing)

# the existing agent/assistant classes, after the first code change
dotnet test tests/Harbora.Tests --no-build --filter "…Harbora.Tests.Agent|…Assistant|…AgentModelSettings|…AgentSettingsPage|…DependencyInjectionTests"
                                                               → EXIT=0  Passed! - Failed: 0, Passed: 797, Total: 797

# the new classes, first run (then committed 0256f07)
dotnet test tests/Harbora.Tests --no-build --filter "…AgentConversationDeletionTests|…AgentStopTests|…AgentConversationRetentionTests|…AssistantConversationHttpTests|…AgentSettingsPageHttpTests"
                                                               → EXIT=0  Passed! - Failed: 0, Passed: 83, Total: 83

# mutation run (two deliberate breaks, then reverted)
                                                               → EXIT=1  Failed! - Failed: 8, Passed: 59, Total: 67

# Postgres lane, new class
dotnet test tests/Harbora.Postgres.Tests --no-build --filter "…AgentConversationDeleteRaceTests"
                                                               → EXIT=0  Skipped! - Failed: 0, Passed: 0, Skipped: 5, Total: 5
  (five at the time; the sixth, reject vs delete, was added after; same skip reason: no Docker)

# all agent/assistant classes after the second change
                                                               → EXIT=0  Passed! - Failed: 0, Passed: 857, Total: 857

# new classes, three runs in a row (flakiness check)
                                                               → 68/68, 68/68, 68/68

# full suite, at the end (on 0d08667 plus the whitespace Stop test)
export MSBUILDDISABLENODEREUSE=1
LOG=/tmp/test-r7-s1.log
dotnet build Harbora.slnx --nologo -v q --no-restore -p:UseSharedCompilation=false > "$LOG.build" 2>&1; echo "BUILD_EXIT=$?"
                                                               → BUILD_EXIT=0; compiler warnings (CS/IDE/CA): 0
dotnet test Harbora.slnx --nologo -v q --no-build > "$LOG" 2>&1; echo "EXIT=$?"
                                                               → EXIT=0
  Skipped! - Failed: 0, Passed:    0, Skipped: 115, Total:  115 - Harbora.Postgres.Tests.dll   (no Docker; 6 of them new)
  Passed!  - Failed: 0, Passed:  500, Skipped:  17, Total:  517 - Harbora.NodeAgent.Tests.dll
  Passed!  - Failed: 0, Passed:   15, Skipped:   0, Total:   15 - Harbora.NodeIngress.Tests.dll
  Passed!  - Failed: 0, Passed: 7376, Skipped:   0, Total: 7376 - Harbora.Tests.dll
grep -c "Harbora.Tests.dll" "$LOG"                             → 2 (present)

Harbora.Tests.dll: 7376 = the 7304 baseline + the 72 tests this task adds (22 + 6 + 32 + 8 + 4); none lost.
```

## 5. Unverified

- **Everything Postgres-only**: the `FOR UPDATE` + `ExecuteDeleteAsync` transaction, the cascade, the lock
  ordering, the three blocked-session races, the reject race, the `LIKE` escaping of `%`/`_` in the search. The
  SQL was compiled with Npgsql's real provider (`ToQueryString`) here; it was never executed against a database.
  The six facts in `AgentConversationDeleteRaceTests` are the CI-lane proof and were **skipped** here.
- The sweeper's 24-hour cadence (only a 20 ms startup delay was exercised).
- The pane: nothing in `Scripts/` was touched by this task, so the pane agents' code against these endpoints is
  not exercised here.

## 6. Contracts

Used as they are in `44d38d0`: `AgentDeleteAllView`, `AgentStopView`, `AgentStreamEvents.Stopped`,
`AgentConversationSummaryView`, `AgentSettingKeys.ConversationRetentionDays`. The `stopped` payload is the
existing `AgentErrorData` record, which serialises to exactly the documented `{ messageEn, messageFa }`. Nothing
in a contract file was changed. New types (`AgentDeleteStatus`, `AgentDeleteOutcome`, `AgentTurnRegistry`) are in
new files.

## 7. Notes for the pane agents

- DELETE needs the same `X-CSRF-TOKEN` header as POST.
- `stopped: true` from `POST …/stop` guarantees a `stopped` event before `done` on the open stream — also when
  the Stop landed while an auto-approved plan ran (then `executed` comes first and the plan did run) or just
  after the last step that listens (then the full answer is already there). `stopped` is a note, not "nothing
  ran".
- A read cut off by Stop has a `tool` event with `state: "running"` and no `done`/`failed` after it
  (`RunReadAsync` is S2's and was not changed); close running tool lines on `stopped`/`done`.
- The list's 400 body is `{ error: "invalid", messageEn, messageFa }`.
- A conversation reserved with `POST /assistant/conversations` and left unused longer than the retention period
  is deleted by the sweep; sending to it is then a 404 — create a new one.

## 8. Concerns

1. **Postgres lane unverified here** (section 5).
2. **A draft cut off by Stop (or the deadline, or a shutdown) before it is stored as a plan is lost, but the
   history keeps its "Added as step N — nothing has run yet; the user will approve it" tool answers.** The next
   turn's model may then speak of a card that does not exist. This existed for the deadline and shutdown since
   round six; Stop makes it likely (stopping while the model writes "I've prepared a plan…"). Nothing runs
   because of it. A fix would be: when a turn ends cut short with a non-empty draft, store the draft and expire it
   at once (`CreatePendingAsync` + `SupersedePendingAsync`), which writes "expired — nothing ran" into the
   history. Not done, to keep this task's diff inside `RunTurnAsync` small while S2 edits the same method.
3. **A plan left `Executing` for good** (a process that died mid-plan, or `FinishBrokenRunAsync`'s "stays
   executing") makes its conversation undeletable for good — by the person (409) and by retention (kept). The
   contract and the brief say an executing plan always blocks; there is no staleness bound for `Executing` as
   there is for turn marks. An owner decision (e.g. treat `Executing` older than some hours as abandoned).
4. **"The same scrub and length bound as every assistant message" — there is none today.** Assistant text is
   stored as the model returned it: it is bounded only by `MaxOutputTokens` (2048) and is clean by construction
   (everything sent to the model is scrubbed of held values). The partial goes through the same `AppendAsync`
   path as every other assistant message, so it gets exactly what they get, which is no extra scrub and no bound.
5. The `stopped`-always-follows-`true` rule means a Stop that lands during an auto-approved plan or after the last
   cancellable step produces `stopped` after a complete result. Chosen so the endpoint's answer and the stream
   never disagree; the alternative (emit only when something was cut) would need the pane to cope with
   `stopped: true` and no event.
6. A Stop pressed while a plan approved from its card runs is `{ stopped: false }` — an approval is its own
   request, not a turn, and is never interruptible.
7. **Delete-all leaves untitled reserved conversations alone** (they are not in the list, hold nothing, and may
   be the one the pane has open); `skipped` does not count them. Retention does delete them once old. Read the
   brief's "every conversation" as "every listed one"; say if that is wrong.
8. The constructor's new parameters are optional; a service built without `audit` (only older tests) deletes
   without an audit row. DI always supplies it.
9. Offset paging (`skip`) can repeat or skip a row across pages when conversations are touched between the two
   requests — inherent in the contract's `skip` design.
10. Merge with S2: my edits in `AgentConversationService` are in `ListAsync` (replaced), the new
    deleting/stopping region after `CreateAsync`, `SendAsync`'s claim-failure branch and token wiring, the
    InMemory branch of `TryClaimTurnAsync`, the post-loop plan section of `RunTurnAsync` (≈10 lines),
    `FinalizePartialAsync` (one line) and the constructor tail. `GetAsync` and `RunReadAsync` are untouched.
