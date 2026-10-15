# S2 — Names people read for the tools, timings, and plans that stay in a reopened conversation (server)

Round seven polishes the in-panel assistant. Contracts on master (`44d38d0`): `AgentToolLabels.cs` (the
`All` dictionary is empty — you fill it), the new optional fields on `AgentToolEventData`,
`AgentMessageView` (`ToolLabel`, `ToolCategory`, `DurationMs`, `PlanId`), `AgentConversationView.Plans`
and `AgentPlanHistoryView`. Code: `src/Harbora.Infrastructure/Agent/AgentConversationService.cs`
(you own `GetAsync` and `RunReadAsync`; agent S1 edits `ListAsync`, `SendAsync` and adds delete/stop in
the same file — do not touch those), `AgentPlanService.cs`, tools in `Agent/Tools/`.

## 1. Labels for all 29 tools

Fill `AgentToolLabels.All` for every tool the registry holds (6 read, 13 app, 10 project/database — find
them in `Agent/Tools/` and the DI registration). Each label is an activity in plain words, the same words
serving "running…" and "done": for example `get_deployment` → "Reading the deployment's logs" /
«خواندن لاگ‌های استقرار», `list_resources` → "Looking through your projects and resources" /
«مرور پروژه‌ها و منابع», `create_database` → "Creating a database" / «ساخت دیتابیس». Persian in the
voice of the panel (look at nearby `*Fa` strings). Category from `AgentToolLabels.Categories`.

Test: every tool resolved from the real registry has a label; no label equals its tool name; every Fa
label contains Persian letters; every category is one of the constants.

## 2. Tool events and stored tool lines

- `RunReadAsync`: the running event carries `LabelEn`, `LabelFa`, `Category`; done/failed add `DurationMs`
  (measure with `Stopwatch`/`TimeProvider` around `ExecuteAsync`, rounded to whole ms). The fallback
  summaries `$"Looked up: {tool.Name}"` / `"بررسی شد: …"` and the failure ones use the label instead of the
  identifier.
- Store the duration on the tool message: a nullable `int? DurationMs` on `AgentMessage` and one migration
  (you are the only agent adding one this round), named `AgentToolDuration`. Build first, then
  `dotnet ef migrations add` — a migration generated against a stale build captures the old model, and
  `MigrationConsistencyTests` catches that.

## 3. The conversation view (`GetAsync`)

- Tool messages carry `ToolLabel` (in the panel's language, from `AgentToolLabels`), `ToolCategory`,
  `DurationMs` (stored; null for old rows and for proposals) and `PlanId`: for a line written by a plan's
  execution it is that plan (`AgentMessage.PlanId`); for a proposal line ("Proposed as step 1: …", written
  during the turn with `PlanId` null in the row) it is the plan whose steps include that line's
  `ToolCallId` (`AgentPlanStep` keeps the call id) — derived at read time, no column.
- `Plans`: every plan of this conversation except the pending one (which stays in `PendingPlan` only),
  oldest first, at most 50, limited to plans whose proposal lines are inside the message window the view
  returns. Each with:
  - its `AgentPlanView` (same mapping as today — `plans.ViewOf`, effective status);
  - its `Result`, built from `ResultsJson` with **the same mapping the approve response uses** (find it in
    `AgentPlanService`; do not write a second one that can drift), null when nothing ran;
  - `AfterMessageId`: the last message of `Messages` that belongs to the turn that proposed it (the last
    visible message before the next user message after its proposal lines), so the pane can draw the card
    where it appeared live.
- An expired plan whose status is still stored as pending is shown with its effective status, as now.

## Tests (Harbora.Tests)

- Labels (above).
- Events: running has the label and no duration; done has label + duration ≥ 0; failed too; the stored
  tool message has the duration.
- View: a turn with two reads and a proposal, then approve → `Plans` has the plan once, with `Result`
  steps matching the approve response exactly and `AfterMessageId` = the turn's final assistant message;
  proposal and execution lines carry the `PlanId`; the pending plan is not in `Plans`; a rejected and an
  expired plan appear with `Result` null; old rows without a duration give null; a plan whose proposal is
  outside the 200-message window is left out; other users' plans never appear.
- Migration consistency test green.

Report: `.superpowers/sdd/r7/s2-report.md`.
