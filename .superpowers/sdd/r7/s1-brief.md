# S1 — Deleting conversations, stopping a turn, the paged list, and retention (server)

Round seven polishes the in-panel assistant (round six built it; design:
`docs/superpowers/specs/2026-10-11-harbora-assistant-agent-design.md`). Contracts for this round are on
master (`44d38d0`): read `AgentViews.cs` (`AgentConversationSummaryView` doc, `AgentDeleteAllView`,
`AgentStopView`, `AgentStreamEvents.Stopped`) and `AgentSettingKeys.ConversationRetentionDays`.
Server code: `src/Harbora.Infrastructure/Agent/AgentConversationService.cs`, `AgentPlanService.cs`,
`src/Harbora.Web/Controllers/AssistantController.cs`. Agent S2 works in parallel on the same service — you
own `ListAsync`, the new delete/stop methods and `SendAsync`'s token handling; S2 owns `GetAsync` and
`RunReadAsync`. Do not edit those two.

## 1. Delete one conversation

`DELETE /assistant/conversations/{id:guid}` (`[ValidateAntiForgeryToken]`, same `[Authorize]` and
`[RequireFeature]` as the rest) → **204**; **404** `{ error: "not_found", messageEn, messageFa }` when it
is not this person's in this workspace; **409** `{ error: "busy", messageEn, messageFa }` when a turn is
live in it (`TurnStartedAt` younger than the turn-mark lifetime, as `GetAsync` judges it) or one of its
plans is `Executing`. Deleting removes its messages and plans (the FKs cascade). The audit rows the
assistant wrote (`assistant.step_executed`, …) and the usage counter are **not** touched. Audit the delete
itself as `assistant.conversation_deleted` (conversation id only, never the title or text).

**Races that must not corrupt anything:**

- A delete and a new message at once: whichever wins, the other is refused cleanly — a message to a
  deleted conversation is `NotFound`, never "still answering"; a delete over a just-claimed turn is 409.
- A delete and an approve at once: a plan that was claimed for execution must never lose its
  conversation underneath it. Make the check and the delete one conditional operation (or lock the
  conversation row in both paths), so either the approve finds nothing to claim or the delete is refused.
  If you conclude a window remains, make execution against a vanished conversation write nothing,
  throw nothing out of the request, and log it — and say so in the report.
- EF InMemory has no `ExecuteDeleteAsync`/`ExecuteUpdateAsync`: use the codebase's pattern
  (`AgentPlanService.TryMoveAsync`) — the real statement on relational providers, read-then-write in tests.
  The Postgres behaviour belongs in `tests/Harbora.Postgres.Tests` (CI only on this machine — say so).

## 2. Delete all

`DELETE /assistant/conversations` (antiforgery) → **200** `AgentDeleteAllView { deleted, skipped }`: every
conversation of this person in this workspace except the busy ones (counted in `skipped`). One audit row
`assistant.conversations_deleted` with the count.

## 3. Stop

`POST /assistant/conversations/{id:guid}/stop` (antiforgery) → **200** `AgentStopView { stopped }`; 404 as
above. A singleton in-memory turn registry: `SendAsync` registers its turn's linked token source under the
conversation id once the turn is claimed and removes it in `finally`; stop cancels it when it belongs to
this actor. The panel runs as one process — note that in a comment where it matters.

- In `SendAsync`, a cancellation caused by Stop is its own branch (not the deadline's, not shutdown's):
  the text the model had streamed so far in the call that was interrupted is saved as an assistant
  message (through the same scrub and length bound as every assistant message; nothing saved when it is
  only whitespace) and emitted as `message`; then `stopped` `{ messageEn: "You stopped this answer.",
  messageFa: "این پاسخ را متوقف کردید." }`; then `done` as always, and the turn mark is cleared.
- A tool call cut off by the stop leaves no half-written tool message; the history builder already drops a
  call without its answer — verify with a test that the **next** turn's request to the model is valid.
- Stop never interrupts a plan that has been claimed: `RunClaimedAsync` takes no token today — keep it so,
  and cover "stop during auto-approved execution" with a test (the plan finishes, its result is saved).
- Model calls already made still count against today's allowance.
- Stop for a conversation whose turn already ended → `{ stopped: false }`, 200.

## 4. The list: search and pages

`GET /assistant/conversations?q=&skip=` → `ListAsync(actor, q, skip)`: `q` trimmed, at most 100 characters
(longer → 400 bilingual), matched inside the title ignoring case in a way both Npgsql and InMemory
translate (`Title.ToLower().Contains(q.ToLower())` is fine; Persian has no case); `skip` ≥ 0, page size
`ConversationListSize` (30), still most recently active first and still without untitled reserved rows.

## 5. Retention (owner setting)

- Settings page, assistant section (`src/Harbora.Web/Views/Settings/Index.cshtml`, saved through
  `AgentSettingsService` — `AgentSettingsInput` gains the field; validate like the daily limit): a number
  box «نگه‌داری گفت‌وگوها (روز)» / "Keep conversations (days)", help text: blank or 0 keeps them for ever;
  conversations not used for this many days are deleted once a day; the audit log is kept. Range 1–3650.
- A hosted sweeper (pattern: `src/Harbora.Infrastructure/Maintenance/*Sweeper.cs`, registered the same
  way): once a day (and shortly after start), when the setting is > 0, deletes conversations of **every**
  workspace whose `UpdatedAt` is older than the cutoff, skipping busy ones exactly as delete-all does.
  **It runs without a signed-in user, so every query must use `IgnoreQueryFilters()`** — a tenant filter
  in a sweeper reads an empty table and reports success (this has happened in this codebase). It logs one
  line with the count. Not the config-driven `DataRetentionSweeper` (production overrides that to keep
  everything; this one is the owner's own choice in the panel).

## Tests (Harbora.Tests)

- Delete: own → 204 and messages/plans gone, audit row has no title; someone else's / other workspace →
  404 and nothing deleted; live turn → 409; executing plan → 409; stale turn mark → deletes; message to a
  deleted conversation → NotFound.
- Delete all: counts, busy kept, other users untouched.
- Stop: mid-stream (fake streaming client that waits) → `message` with the partial text, then `stopped`,
  then `done`; the partial text stored, the mark cleared, the next turn's model request valid. Stop with
  nothing running → false; someone else's → 404; stop during auto-approved execution → the plan finishes.
- List: `q` matches, case-insensitive, paging with `skip`, 101-char `q` → 400.
- Retention: settings validate and save; sweeper with a fake clock deletes only old, non-busy
  conversations across workspaces; nothing when 0 or blank.
- Controller-level tests for the status codes and antiforgery, as the existing assistant controller
  tests do.

Report: `.superpowers/sdd/r7/s1-report.md`.
