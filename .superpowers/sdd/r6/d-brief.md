# D — The agent core: conversations, the tool loop, read tools, plans, endpoints (phase 2)

## Why

This is the heart of the in-panel agent. Read the design doc
(`docs/superpowers/specs/2026-10-11-harbora-assistant-agent-design.md`) in full first — "How one message
flows", "Data" and "Safety rules" are your specification. You build everything between the chat pane
(agent E, in parallel) and the model client (agent C, in parallel), against the contracts already on
master in `src/Harbora.Infrastructure/Agent/`. Write tools that change resources come in the next wave;
you build the **generic** plan engine and prove it with test-double tools.

## Build

1. **Data.** Entities in `src/Harbora.Domain/Agent/`: `AgentConversation`, `AgentMessage`, `AgentPlan`,
   `AgentToolApproval`, `AgentUsageDay` (fields per the design's "Data" section; add what the work needs).
   DbSets and configuration in `HarboraDbContext`, workspace query filters like other tenant data,
   sensible indexes (a user's conversations by `UpdatedAt`; one approval per user+workspace+tool;
   one usage row per workspace+UTC date). One migration, `AgentCore`, created with `dotnet ef migrations
   add` **after a fresh build** of the current tree (a migration generated against a stale assembly
   captures the old model; `MigrationConsistencyTests` catches that — it must pass). You are the only
   agent adding a migration this wave.
2. **`AgentToolRegistry`**: all `IAgentTool`s from DI; the specs offered to the model are only the tools
   whose `Capability` the user holds (`IAuthorizationService.AuthorizeAsync(user, capability)` — the
   same policies the controllers use; see how `TemplatesController.CanDeployAsync` does it).
3. **Consent policy**: `Read` → run; `SafeWrite` → card unless the user has an `AgentToolApproval` for
   that tool in this workspace; `DangerousWrite` → always card; `Destructive` → always card with typed
   confirmation (`AgentValidation.ConfirmationPhrase`, compared exactly, trimmed, case-sensitive).
   Approvals are only ever recorded for `SafeWrite` tools, whatever the request says.
4. **The orchestrator** (`AgentConversationService` or similar): exactly the loop in the design —
   entitlement (`IFeatureGate`, `PlatformFeatures.Assistant`) → `IAgentChatClient.GetAvailabilityAsync`
   → daily cap (`AgentSettingKeys.DailyRequestLimit`, default `DefaultDailyRequestLimit`, 0 = unlimited;
   count every `CompleteAsync` call) → system prompt + workspace snapshot + page context + last 20
   messages + allowed tools → loop of at most 8 model calls. Handle: unknown tool, invalid JSON
   arguments, capability missing, read execution, write validation with an `AgentPlanScope` (add
   `Creates` from each accepted step), refusals fed back to the model, the "added as step N — nothing has
   run yet; the user will approve it" tool message, the final text. Then: no steps → message only; all
   steps auto-approved → execute now and emit `executed`; otherwise persist a pending plan (15-minute
   expiry) and emit `plan`. Persist every message (user, assistant with tool calls, tool results) so the
   next turn has the context. Truncate each tool result sent to the model (e.g. 4,000 chars) and pass it
   through `AssistantRedaction`. Title a new conversation from its first user message (trimmed, short).
   - **System prompt** (keep it in one class, English, readable): who it is (Harbora's assistant);
     answer in the user's language (Persian or English); use tools rather than guessing; address
     resources by the names the snapshot shows; never invent ids; when a request is ambiguous, ask one
     short question instead of guessing; slugs are lowercase Latin letters, digits and hyphens —
     transliterate Persian names; write tools do not perform anything when called — they become a plan
     the user approves — so never say something was done unless a tool result says so.
   - **Snapshot**: projects → environments → apps (slug, status, kind) and databases (name, engine,
     version, status) the user can see (`ProjectAccessService.VisibleProjectIdsAsync`), capped (say,
     60 resources, with "and N more"), plus the page context resolved to names.
5. **Plan engine** (`AgentPlanService` or similar): approve (`AgentApproveRequest`) / reject / expiry
   (computed lazily on read and approve — no background job). Approve: plan belongs to this user and
   workspace, is `pending` and unexpired, every destructive step's phrase matches; move
   `pending → executing` with a **conditional update** so it can run once — follow
   `SignInTokenService.TryClaimAsync` exactly (relational `ExecuteUpdateAsync` + `RefreshTrackedAsync`,
   InMemory fallback, and its comments on why). Re-validate every step with a fresh scope; if any is
   refused now, run nothing and report it. Execute in order; the first failure stops the rest
   (`skipped`). Write results into the plan and as tool messages in the conversation. Audit
   `assistant.step_executed` per executed step (conversation, plan, step index, tool, ok) and
   `assistant.plan_rejected`. Save remembered approvals. Return `AgentPlanResultView`.
6. **Read tools** (each an `IAgentTool` of kind `Read`, registered in DI, honest JSON results, no secret
   values ever — env vars by key with "secret: set"): `list_resources`, `get_app` (status, domains, env
   keys, attached databases, last five deployments), `get_database` (engine, version, status, attached
   apps, size if known — never passwords), `get_catalog` (engines and versions from
   `ServiceCatalogReader`, templates visible to the workspace, instance sizes), `get_deployment` (status,
   timestamps, last 40 log lines, redacted), `get_usage` (quota usage via `IQuotaService`). Resolve names
   to rows with the workspace filter and project visibility; an invisible resource is "not found".
7. **Endpoints** — `AssistantController`, `[Route("assistant")]`, `[Authorize]`,
   `[RequireFeature(PlatformFeatures.Assistant)]`, JSON shapes exactly as `AgentViews.cs`:
   - `GET status` → `AgentStatusView`
   - `GET conversations` → the user's latest 30 `AgentConversationSummaryView`
   - `GET conversations/{id}` → `AgentConversationView` (with the pending plan, if any)
   - `POST conversations/{id}/messages` (`id` may be `new`) body `AgentSendMessageRequest` →
     `text/event-stream`, events from `AgentStreamEvents` (`started` first, `done` always last, `error`
     instead of a 500 once the stream has begun); flush after each event; no response buffering.
   - `POST plans/{id}/approve` body `AgentApproveRequest` → `AgentPlanResultView`
   - `POST plans/{id}/reject` → 204
   - `GET approvals` → the user's remembered tools; `POST approvals/{tool}/revoke` → 204
   POSTs require the antiforgery token: this app reads it from the **`X-CSRF-TOKEN` header**
   (`Program.cs` `AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN")`); islands get it via a
   `data-csrf` attribute (see `RouteDesigner.vue`). Agent E's pane sends it that way.
8. **Tests.** A scripted fake `IAgentChatClient` (returns queued `AgentChatResponse`s, records requests)
   and test-double tools of every kind. Cover: read tool executed and its result in the next request;
   unknown tool / bad JSON / missing capability fed back; write refused → fed back → corrected call
   accepted; plan created with steps in order and scope-aware validation ("create X then attach X");
   remembered safe tool auto-executes (`executed`); dangerous and destructive always card; remembering a
   dangerous tool is ignored; phrase mismatch refused; expiry; re-validation refusal runs nothing;
   failure stops later steps; second approve refused; another user's / workspace's plan refused; daily
   cap and the 8-call ceiling; entitlement locked → refused; secrets never in a model request (seed a
   secret env var, assert its value appears in no recorded request); HTTP tests for the endpoints and
   the SSE framing (`HarboraWebFactory`); and a **Postgres-lane** test in `tests/Harbora.Postgres.Tests`
   that two concurrent approvals execute a plan once (unverified locally — say so).

## Do not touch

The phase-0 controllers/services (agents A and B), the client and settings (agent C — you depend only on
`IAgentChatClient`), `_Layout.cshtml`, `_Topbar.cshtml`, `Scripts/` (agent E). Register your services in
`DependencyInjection.cs` as one contiguous block with a comment naming this work.

## Report

Full report to `.superpowers/sdd/r6/d-report.md` in your worktree, committed: data model and migration,
classes and exact public signatures (wave B's tool authors will code against them), the loop's
behaviour per case, prompt text, endpoint list, test files and counts, exact commands and output lines,
what is unverified. Return only: status, commit list, one-line test summary, concerns.
