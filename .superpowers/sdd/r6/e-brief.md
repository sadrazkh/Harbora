# E — The chat pane (phase 3)

## Why

The owner wants an AWS-style assistant pane inside the panel: open it, type in Persian or English, watch
the assistant read things, approve a plan, see results. Read the design doc first
(`docs/superpowers/specs/2026-10-11-harbora-assistant-agent-design.md`). You build the pane; the server
behind it (agent D) is being built in parallel against the same contract — `src/Harbora.Infrastructure/
Agent/AgentViews.cs` (JSON shapes, camelCase on the wire, and the SSE event names) is your API. Code to
it exactly; you cannot run the real server end to end this wave, so make the client logic testable on
its own.

## Build

1. **Placement** — an overlay drawer on the side **opposite the main menu**: logical `end` (left in
   Persian/RTL, right in English/LTR), about 420 px wide, full height under the topbar, full screen on
   small screens. It overlays content (does not reflow the page). Mount point in
   `src/Harbora.Web/Views/Shared/_Layout.cshtml` **outside** `#content-scroll`.
2. **Toggle** — a button in `src/Harbora.Web/Views/Shared/Design/_Topbar.cshtml` near the command-palette
   trigger (Lucide icon, e.g. `sparkles`; icons are imported one by one in `Scripts/main.ts`), plus a
   keyboard shortcut (Ctrl/Cmd+J). Remember open/closed and the current conversation id in
   `localStorage`. The button follows the entitlement rules for `PlatformFeatures.Assistant` the way
   the sidebar treats `Feature:` items: **Enabled** → working button; **Locked** → greyed with a lock,
   linking to `/features/assistant`; **Hidden** → not rendered. Evaluate it with `IFeatureGate` the way
   the sidebar/navigation does today (find it; do not invent a second mechanism). Authenticated users
   only. The drawer mount is rendered only when the feature is enabled.
3. **The island** — `src/Harbora.Web/Scripts/islands/AssistantPanel.vue`, lazily imported through the
   `islands` registry in `main.ts` (follow `RouteDesigner`/`CodeEditor`). Props via `data-*`: `csrf`
   (POSTs send it as the **`X-CSRF-TOKEN`** header — see `RouteDesigner.vue`), `lang`. It:
   - on open, `GET /assistant/status` → shows the model and remaining requests today, or the reason it is
     unavailable (both languages);
   - lists conversations (`GET /assistant/conversations`), loads one (`GET /assistant/conversations/{id}`,
     which may include a pending plan), starts a new one (send to `conversations/new`);
   - sends with `fetch` POST (`AgentSendMessageRequest`, including the page context — below) and reads
     the `text/event-stream` body incrementally with a `ReadableStream` reader (not `EventSource`: it is
     a POST). Handles `started` (keep the conversation id), `tool` (a compact status line with a spinner
     → check / cross), `message`, `plan`, `executed`, `error`, `done` (update remaining). Network errors
     and a stream that ends without `done` are shown as errors, never silently dropped;
   - renders messages as **text** with light formatting (paragraphs, line breaks, `inline code`, simple
     `-` lists) — never inject model text as HTML; `dir="auto"` per message so Persian and English both
     read right;
   - renders a **plan card** (`AgentPlanView`): numbered steps with a kind badge (safe / dangerous /
     destructive, in both languages), title, detail, warnings; for each `rememberable` step a checkbox
     "don't ask again for this" / «دیگر برای این کار نپرس»; for each destructive step a text field asking
     the user to type `confirmationPhrase`, checked on Run with an inline error when it does not match;
     Run and Cancel → `POST /assistant/plans/{id}/approve` (`AgentApproveRequest`) / `.../reject`; an
     expired plan says so;
   - renders a **result card** (`AgentPlanResultView` from approve, or an `executed` event): per step
     done / failed / skipped, the summary or error, and the links. A step is shown as done only when the
     result says `done`;
   - Persian and English through a `t(en, fa)` helper like the other islands; RTL-correct spacing
     (logical properties).
4. **Page context** — the pane sends `{ path, appId, serviceId, projectId, environmentId }`. Add a small,
   explicit way for a page to declare its context (e.g. `_Layout` renders a `data-assistant-context`
   JSON attribute from `ViewData["AssistantContext"]` when a page sets it) and set it on the app details
   and database details pages.
5. **Tests.** Put the logic in plain TypeScript/JS modules beside the island so it is testable without
   a browser, and cover it with node tests in `src/Harbora.Web/Scripts/tests/*.test.mjs` (CI runs
   `node --test src/Harbora.Web/Scripts/tests/*.test.mjs`): SSE frame parsing across chunk boundaries,
   multi-line `data:`, unknown events ignored; plan-card state (which steps need a phrase, phrase
   matching, which are rememberable, building the approve body); message formatting escapes HTML. HTTP
   tests (`HarboraWebFactory`) for the layout: button + mount present when the feature is enabled,
   greyed lock when locked, absent when hidden; context attribute on the app/database pages. Run
   `npm ci` and `npm run build` in your worktree to prove the bundle builds (never at the same time as a
   `dotnet test` in the same worktree — see CONSTRAINTS).

## Do not touch

Server-side agent code (`src/Harbora.Infrastructure/Agent/*`, any `AssistantController` — agent D),
settings (agent C), `AppsController`/`DatabasesController` actions (agents A and B — only the two
details **views** get the context attribute; keep that edit minimal).

## Report

Full report to `.superpowers/sdd/r6/e-report.md` in your worktree, committed: files, how the toggle
evaluates the entitlement, the event handling, test files and counts, `npm run build` output summary,
exact commands and output lines, what could not be verified without D's server. Return only: status,
commit list, one-line test summary, concerns.
