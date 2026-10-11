# D — Agent core (phase 2) — report

Branch `r6/agent-core`, worktree `../Harbora-r6-d`. Rebased onto master `504a09f` (agent C's chat client and
settings) part-way through, and again at the end onto master `1e304fe` (agents A and B landed; `git
merge-tree` showed no conflict and the rebase applied cleanly). The final runs in section 10 are on top of
`1e304fe`.

**Status: DONE_WITH_CONCERNS** — everything in the brief is built and tested; the concerns are wire details
and two behaviours the design did not decide (section 9), plus the Postgres lane, which is unverified here.

Commits (on top of master `1e304fe`, owner identity, no trailer):

```
01fd634 Agent core: conversations, the tool-calling loop, read tools and a plan engine that executes approved steps once
67e1f82 Assistant endpoints over HTTP and the Postgres-lane race proofs for the plan claim and the daily cap
6c31d1d Daily cap reads its limit through the settings page's own parser, and another workspace's resources are proven invisible to every read tool
8aa938c Agent loop: replace blank or repeated tool-call ids so every answer names exactly one call
cc71bd7 get_catalog offers only the instance sizes the workspace's plan allows
db552c1 Plan steps are numbered by 0-based position, and a step refused at re-check is a 409 with the reason
```

plus the commit adding this report. Coordinator's mid-task requests, all done: the cap reads through C's
`AgentSettingsService.EffectiveLimit`; provider/availability text is verbatim only for `PlatformManage`;
every refusal body carries `messageEn`/`messageFa`; step `Index` is the 0-based position and confirmations
are checked by it.

## 1. Data model and migration

`src/Harbora.Domain/Agent/`:

| Entity | Fields (besides `BaseEntity` Id/CreatedAt/UpdatedAt) |
|---|---|
| `AgentConversation` | `WorkspaceId`, `UserId`, `Title` (≤120), `Messages` |
| `AgentMessage` | `WorkspaceId`, `ConversationId`, `Sequence` (order within the conversation — not CreatedAt: one turn writes several rows in one millisecond), `Role` (`AgentMessageRole` User/Assistant/Tool; the system prompt is never stored), `Text`, `ToolCallsJson`, `ToolCallId` (≤128), `ToolName` (≤64), `SummaryEn`/`SummaryFa` (≤500, what the pane shows for a tool row), `PlanId`, `Model` (≤128), `InputTokens`, `OutputTokens` |
| `AgentPlan` | `WorkspaceId`, `UserId`, `ConversationId`, `Status` (`AgentPlanStatus` Pending=0, Executing=1, Executed=2, Failed=3, Rejected=4, Expired=5), `StepsJson`, `ResultsJson`, `ExpiresAt`, `ExecutedAt`, `DecidedAt`; `AgentPlan.Lifetime` = 15 min |
| `AgentToolApproval` | `WorkspaceId`, `UserId`, `ToolName` (≤64) |
| `AgentUsageDay` | `WorkspaceId`, `Day` (`DateOnly`, UTC), `ModelCalls` |

`HarboraDbContext`: five DbSets, `ConfigureAgent(b)` (indexes, lengths, cascade FKs message→conversation and
plan→conversation; no navigation to User/Workspace, like `SignInToken`), and all five workspace-filtered in
`ApplyWorkspaceFilters` — messages and plans carry their own `WorkspaceId` (the inner-join reason `Deployment`
documents). Indexes: `(WorkspaceId, UserId, UpdatedAt)` on conversations; `(ConversationId, Sequence)` on
messages; `(ConversationId, Status)` on plans; **unique** `(UserId, WorkspaceId, ToolName)` on approvals;
**unique** `(WorkspaceId, Day)` on usage days.

Migration `20261011104149_AgentCore` — generated with `dotnet ef migrations add AgentCore --project
src/Harbora.Data --startup-project src/Harbora.Data` right after a fresh `dotnet build src/Harbora.Data`
(MSBUILDDISABLENODEREUSE=1, UseSharedCompilation=false). `Up()` creates the five tables and five indexes and
nothing else; the snapshot diff is 225 insertions, 0 deletions. `MigrationConsistencyTests` passes. The
migration has **not** been applied to a real Postgres here (no Docker); the Postgres lane applies it in CI.

## 2. Classes and exact public signatures (wave B codes against these)

All in `Harbora.Infrastructure.Agent` unless noted. Contracts in `AgentChatContracts/AgentToolContracts/
AgentViews/AgentSettingKeys.cs` are unchanged.

```csharp
// AgentToolRegistry.cs
public interface IAgentCapabilityCheck
{ Task<bool> AllowsAsync(ClaimsPrincipal user, string capability, CancellationToken ct); }

public sealed class AgentToolRegistry
{
    public AgentToolRegistry(IEnumerable<IAgentTool> tools, IAgentCapabilityCheck capabilities); // throws on two tools with one name
    public IReadOnlyList<IAgentTool> All { get; }                       // name order
    public IAgentTool? Find(string? name);                              // exact name
    public Task<bool> AllowsAsync(ClaimsPrincipal user, IAgentTool tool, CancellationToken ct); // null capability = anyone
    public Task<IReadOnlyList<IAgentTool>> AllowedForAsync(ClaimsPrincipal user, CancellationToken ct);
    public static AgentToolSpec SpecOf(IAgentTool tool);
}

// AgentConsentPolicy.cs
public static class AgentConsentPolicy
{
    public static bool RunsImmediately(AgentToolKind kind);            // Read
    public static bool IsRememberable(AgentToolKind kind);             // SafeWrite only
    public static bool IsAutoApproved(AgentToolKind kind, bool remembered); // SafeWrite && remembered
    public static bool NeedsTypedConfirmation(AgentToolKind kind);     // Destructive
    public static bool ConfirmationMatches(string? expected, string? typed); // both trimmed, ordinal, case-sensitive; blank expected never matches
    public static string KindLabel(AgentToolKind kind);                // "read" | "safe" | "dangerous" | "destructive"
}

// AgentCoreModels.cs
public sealed record AgentActor(Guid WorkspaceId, Guid UserId, ClaimsPrincipal User, string? IpAddress = null);
public sealed record AgentStreamEvent(string Name, object Data);
public sealed record AgentStartedData(Guid ConversationId);
public sealed record AgentToolEventData(string Name, string Kind, string State, string? SummaryEn, string? SummaryFa);
public sealed record AgentErrorData(string MessageEn, string MessageFa);
public sealed record AgentDoneData(Guid ConversationId, int? RemainingToday);
public sealed record AgentApprovalView(string Tool, DateTimeOffset CreatedAt);          // GET /assistant/approvals rows
public enum AgentSendStatus { Streamed, Invalid, NotEntitled, NotFound }
public sealed record AgentSendOutcome(AgentSendStatus Status, string? MessageEn = null, string? MessageFa = null);
public sealed record AgentPlanRefusal(string Code, string MessageEn, string MessageFa, string? Status = null, int? StepIndex = null);
    // Code: "not_found" | "expired" | "not_pending" | "confirmation_mismatch" | "revalidation_refused"
public sealed record AgentApproveOutcome(AgentPlanResultView? Result, AgentPlanRefusal? Refusal) { public bool Ok { get; } }
public sealed record AgentPlanStep(int Index, string Tool, AgentToolKind Kind, string ArgumentsJson, string? ToolCallId,
    string TitleEn, string TitleFa, string? DetailEn, string? DetailFa,
    IReadOnlyList<string> WarningsEn, IReadOnlyList<string> WarningsFa, string? ConfirmationPhrase, bool AutoApproved);
    // Index = 0-based position; people and the model read "step Index + 1"
public static class AgentJson
{
    public const int MaxToolResultLength = 4000;
    public static readonly JsonSerializerOptions Options;              // Web defaults, enums camelCase by name, Persian unescaped
    public static string Serialize<T>(T value);
    public static T? Deserialize<T>(string? json);
    public static string ForModel(string? text, IEnumerable<string>? knownSecrets = null); // AssistantRedaction, THEN truncate
}

// AgentUsageMeter.cs
public sealed class AgentUsageMeter(HarboraDbContext db, ISystemClock clock)
{
    public Task<int> LimitAsync(CancellationToken ct);                    // AgentSettingsService.EffectiveLimit (C's parser); 0 = unlimited
    public Task<int> UsedTodayAsync(Guid workspaceId, CancellationToken ct);
    public Task<int?> RemainingAsync(Guid workspaceId, CancellationToken ct); // null = unlimited
    public Task<bool> TryReserveAsync(Guid workspaceId, CancellationToken ct); // conditional UPDATE; InMemory: read-then-write
}

// AgentResourceResolver.cs — the one place tools turn names into rows the person may see
public sealed record AgentLookup<T>(T? Value, string? Error, string? ErrorFa) where T : class
{ public bool Found { get; } public static AgentLookup<T> Of(T value); public static AgentLookup<T> Missing(string error, string errorFa); }
public sealed record AgentResourceTree(IReadOnlyList<AgentProjectNode> Projects, int Omitted);
public sealed record AgentProjectNode(string Slug, string Name, IReadOnlyList<AgentEnvironmentNode> Environments);
public sealed record AgentEnvironmentNode(string Slug, string Name, bool Protected, IReadOnlyList<AgentAppNode> Apps, IReadOnlyList<AgentDatabaseNode> Databases);
public sealed record AgentAppNode(string Slug, string Name, string Status, string Kind);
public sealed record AgentDatabaseNode(string Name, string Engine, string Version, string Status);
public sealed class AgentResourceResolver(HarboraDbContext db, ProjectAccessService access)
{
    public Task<AgentLookup<App>> AppAsync(AgentToolContext context, string? name, CancellationToken ct); // slug, then display name; includes Environment.Project
    public Task<AgentLookup<ManagedService>> DatabaseAsync(AgentToolContext context, string? name, string? project, string? environment, CancellationToken ct);
    public Task<AgentLookup<Project>> ProjectAsync(AgentToolContext context, string? name, CancellationToken ct);
    public Task<AgentLookup<Environment>> EnvironmentAsync(AgentToolContext context, string? project, string? environment, CancellationToken ct); // blank env = the project's default
    public Task<AgentResourceTree> TreeAsync(AgentToolContext context, int maxResources, string? projectFilter, CancellationToken ct);
    public static string Place(ManagedService service);   // "project/environment"
    public static string Place(App app);
    public static string Lower<TEnum>(TEnum value) where TEnum : struct, Enum;
}

// AgentWorkspaceSnapshot.cs
public sealed class AgentWorkspaceSnapshot(HarboraDbContext db, AgentResourceResolver resolver, ProjectAccessService access)
{
    public const int MaxResources = 60;
    public Task<string> DescribeAsync(AgentToolContext context, CancellationToken ct);
    public Task<string?> DescribePageAsync(AgentToolContext context, CancellationToken ct);
}

// AgentSystemPrompt.cs
public static class AgentSystemPrompt
{ public const string Rules; public static string Build(string snapshot, string? page, bool persianInterface); }

// AgentPlanService.cs
public sealed class AgentPlanService(HarboraDbContext db, AgentToolRegistry registry, IAuditLogger audit, ISystemClock clock, ILogger<AgentPlanService> logger)
{
    public const string StepExecutedAction = "assistant.step_executed";
    public const string PlanRejectedAction = "assistant.plan_rejected";
    public Task<AgentPlan> CreatePendingAsync(AgentActor actor, Guid conversationId, IReadOnlyList<AgentPlanStep> steps, CancellationToken ct);
    public Task<AgentApproveOutcome> ApproveAsync(AgentActor actor, Guid planId, AgentApproveRequest? request, CancellationToken ct);
    public Task<AgentPlanResultView> ExecuteAutoApprovedAsync(AgentActor actor, AgentPlan plan, CancellationToken ct);
    public Task<AgentPlanRefusal?> RejectAsync(AgentActor actor, Guid planId, CancellationToken ct); // null = rejected (idempotent)
    public Task SupersedePendingAsync(AgentActor actor, Guid conversationId, CancellationToken ct);
    public Task<IReadOnlySet<string>> RememberedToolsAsync(AgentActor actor, CancellationToken ct);
    public Task<IReadOnlyList<AgentApprovalView>> ApprovalsAsync(AgentActor actor, CancellationToken ct);
    public Task RevokeApprovalAsync(AgentActor actor, string tool, CancellationToken ct); // idempotent
    public AgentPlanStatus EffectiveStatus(AgentPlan plan);              // pending past ExpiresAt reads as expired
    public Task<AgentPlan?> PendingPlanAsync(AgentActor actor, Guid conversationId, CancellationToken ct);
    public AgentPlanView ViewOf(AgentPlan plan);
    public static AgentPlanStepView StepView(AgentPlanStep step);
    public static IReadOnlyList<AgentPlanStep> StepsOf(AgentPlan plan);
    public static string StatusName(AgentPlanStatus status);
}

// AgentConversationService.cs
public sealed class AgentConversationService(HarboraDbContext db, IAgentChatClient chat, AgentToolRegistry registry,
    AgentPlanService plans, AgentUsageMeter meter, AgentWorkspaceSnapshot snapshot, IFeatureGate features,
    IAgentCapabilityCheck capabilities, ISystemClock clock, ILogger<AgentConversationService> logger)
{
    public const int MaxModelCallsPerMessage = 8;
    public const int HistoryMessages = 20;
    public const int MaxMessageLength = 4000;
    public const int ConversationListSize = 30;
    public const int ConversationViewMessages = 200;
    public Task<AgentStatusView> StatusAsync(AgentActor actor, CancellationToken ct);
    public Task<IReadOnlyList<AgentConversationSummaryView>> ListAsync(AgentActor actor, CancellationToken ct);
    public Task<AgentConversationView?> GetAsync(AgentActor actor, Guid conversationId, bool persian, CancellationToken ct);
    public Task<AgentSendOutcome> SendAsync(AgentActor actor, Guid? conversationId, AgentSendMessageRequest? request,
        bool persianInterface, Func<AgentStreamEvent, CancellationToken, Task> emit, CancellationToken ct);
}

// Tools/ (namespace Harbora.Infrastructure.Agent.Tools)
public static class AgentArgs { String(JsonElement, string); Int(JsonElement, string); Bool(JsonElement, string); } // tolerant: "3" and 3, "true" and true
public abstract class AgentReadTool : IAgentTool   // Kind = Read, Capability = null, ValidateAsync = Ok; protected Found/NotFound/Clip
public static class AgentSecrets { Task<IReadOnlyList<string>> ForAppAsync(HarboraDbContext, ISecretProtector, Guid appId, Guid workspaceId, CancellationToken); }
public sealed class ListResourcesTool(AgentResourceResolver)                             // list_resources
public sealed class GetAppTool(HarboraDbContext, AgentResourceResolver, ISecretProtector) // get_app
public sealed class GetDatabaseTool(HarboraDbContext, AgentResourceResolver, ProjectAccessService) // get_database
public sealed class GetCatalogTool(HarboraDbContext, IManagedServiceEngine)              // get_catalog
public sealed class GetDeploymentTool(HarboraDbContext, AgentResourceResolver, ISecretProtector) // get_deployment
public sealed class GetUsageTool(IQuotaService, AgentUsageMeter)                         // get_usage
```

Web: `Harbora.Web.Infrastructure.AuthorizationAgentCapabilityCheck(IAuthorizationService) : IAgentCapabilityCheck`
(asks the very capability policies the controllers' `[Authorize(Policy = …)]` use) and
`Harbora.Web.Controllers.AssistantController`.

DI: one contiguous block in `DependencyInjection.cs` right after `IFeatureGate` (registry, meter, resolver,
snapshot, plan service, conversation service, the six read tools — all scoped). One line in `Program.cs`
after `AddCapabilityAuthorization()` registers `IAgentCapabilityCheck` — it cannot live in Infrastructure,
which has no ASP.NET Core reference (see concern 3).

**For wave B tool authors:** implement `IAgentTool`; resolve names with `AgentResourceResolver` (never by id);
`ValidateAsync` must be side-effect-free and return `Creates` for what the step makes so a later step can name
it via `scope.Plans(kind, name)`; a `Destructive` tool must return a non-blank `ConfirmationPhrase` (the
orchestrator refuses the step otherwise) and must return the *same* phrase at execution-time re-validation
(the engine refuses if it changed); `ExecuteAsync` must re-check everything and never put a secret in
`ResultJson`. Register as `services.AddScoped<IAgentTool, YourTool>()`.

## 3. The loop, case by case (`AgentConversationService.SendAsync`)

Before any stream (nothing emitted, the controller answers with a status code): empty / >4000-char text →
`Invalid` (400); workspace not entitled (`IFeatureGate`, `PlatformFeatures.Assistant`) → `NotEntitled` (403,
nothing stored); conversation id that is not this person's in this workspace → `NotFound` (404).

Then: an existing conversation's still-pending plans are **superseded** (moved to `expired`, and the
conversation is told "not approved — nothing ran"); the user message is stored (new conversation titled from
it: whitespace collapsed, ≤60 chars with "…"); `started` is emitted. Then, inside a try (any exception →
`error` event, logged; cancellation propagates because nobody is listening), and `done` always last:

1. Availability (`IAgentChatClient.GetAvailabilityAsync`) → `error` event, no model call.
2. Daily cap already used up → `error` event, no model call.
3. System message = `AgentSystemPrompt.Rules` + interface-language hint + snapshot (≤60 apps/databases, "…and N
   more") + page context resolved to names (ids the person cannot see are dropped; a path is kept only if it
   matches `^/[A-Za-z0-9/_\-.~%]*$` and ≤200 chars). Then the last 20 stored messages, sanitised so a tool
   answer is only sent behind its call and a call only with all its answers. Tools = `AllowedForAsync`.
4. Up to **8** model calls; each is reserved against the cap first (`TryReserveAsync`; refused → `error`).
   Provider failure → `error`. The assistant message (text and/or tool calls, model, tokens) is stored; any
   text is emitted as `message`. Blank or repeated tool-call ids are replaced (`call_r{round}_{n}`) so every
   answer names exactly one call. For each call:
   - unknown tool → "Unknown tool 'x'. The tools you can use are: …";
   - capability missing (checked again here, not just when offering) → "The user's role does not allow 'x'
     (it needs the 'cap' permission). Nothing was done; tell the user …";
   - arguments not a JSON object (blank counts as `{}`) → "The arguments for 'x' are not a valid JSON object …";
   - `Read` → `tool` event `running`, execute (an exception becomes a failed result, logged, not leaked),
     `tool` event `done`/`failed` with summaries; the result goes back via `AgentJson.ForModel` (redacted,
     then cut to 4,000 chars);
   - write → `ValidateAsync(args, context, scope)`; refused → "Refused: {reason} Nothing was added to the plan;
     correct the arguments and call it again, or ask the user."; a destructive tool with no phrase → refused;
     accepted → `Creates` added to the scope, a step added, and the model is told **"Added as step N — nothing
     has run yet; the user will approve it. (Step N: {title})"**.
   Every tool answer is stored (with a display summary for reads and accepted writes).
5. 8 calls without a final text → `error` "I stopped after 8 steps without finishing…".
6. No steps → done. Turn finished **and** every step a remembered `SafeWrite` → plan stored and executed at
   once through the same claim and re-validation as an approval → `executed`. Otherwise → pending plan
   (15 min) → `plan`. A turn the model never finished (cap, ceiling, provider error) never auto-runs; its
   steps always wait on a card.

Error text from the client (provider errors, availability reasons) is shown verbatim only to someone holding
`Capabilities.PlatformManage`; everyone else gets "The AI provider could not answer right now…" / "The
assistant is not available right now. The platform's owner can check its settings." (both languages), and the
detail is logged (coordinator's instruction).

## 4. Plan engine (`AgentPlanService`)

- **Approve:** load by id **and** this user **and** this workspace (else `not_found`); pending past
  `ExpiresAt` → moved to `expired` on the spot, refused `expired`; not pending → `not_pending`; every
  destructive step's typed text (`Confirmations[step.Index]`) must match → else `confirmation_mismatch` with
  `StepIndex` and the plan **stays pending**. Then the claim: `pending → executing` with one conditional
  `ExecuteUpdateAsync` (`Id`, user, workspace, `Status == Pending`, `ExpiresAt > now`) + `RefreshTrackedAsync`,
  InMemory fallback — `SignInTokenService.TryClaimAsync`'s shape and comments. Zero rows → diagnosed from a
  fresh read. After the claim, work runs with `CancellationToken.None` (a plan abandoned half-way because the
  browser closed would be the worst outcome).
- Remembered tools are saved after the claim: only names the registry *currently* marks `SafeWrite` **and**
  that a step of this plan used; duplicates ignored; a concurrent duplicate insert is swallowed (unique index).
- **Re-validation:** every step again, in order, fresh `AgentPlanScope`: tool still exists, same `Kind` as when
  carded, capability still held, arguments parse, `ValidateAsync` OK, destructive phrase unchanged. Any
  refusal → **nothing runs**, plan `failed`, results (refused step `failed`, all others `skipped`) and tool
  messages written, and the approval returns refusal `revalidation_refused` (409) with the reason.
- **Execute** in order; an exception or `Ok == false` fails the step and the rest are `skipped`. One
  `assistant.step_executed` audit per step that ran: metadata `{ conversationId, planId, step (Index+1),
  stepIndex, tool, ok }`, target `agent_plan`/planId, workspace passed explicitly, request IP.
- Results are written three times: `ResultsJson` (+ status `executed`/`failed`, `ExecutedAt`); the conversation
  (one assistant message calling each step's tool with id `plan_{planId:N}_{index}`, then one tool message per
  step `{plan, step, tool, state, summary, error?, result?}`) so the next turn knows; the audit log.
- **Reject:** pending → `rejected` (conditional move), audit `assistant.plan_rejected`
  `{conversationId, planId, tools}`, conversation told "rejected by the user — it did not run". A second
  reject returns success and changes nothing; rejecting anything else → `not_pending`/`expired`.
- **Expiry** is lazy: computed on read (`EffectiveStatus`, `PendingPlanAsync`) and written on approve/reject.
  No background job.

## 5. Read tools

| Tool | Args | Returns |
|---|---|---|
| `list_resources` | `project?` | the visible tree (cap 200, `omitted`) |
| `get_app` | `app` | slug, name, project, environment, status, kind, source, image, gitRef, size, replicas, maintenance, domains, env vars (secret → `"secret":"set"`, plain → value redacted by every secret the app holds), attached databases, last 5 deployments (errors redacted by value) |
| `get_database` | `database`, `project?`, `environment?` | engine, version, status, error, size key, `storageBytes`/`storageMeasuredAt`, tls, PITR, replicaOf, attached apps (only ones the person can see), logical databases. Never username/password/connection string. Ambiguous name → asks for project/environment |
| `get_catalog` | — | engines + versions (`ServiceCatalogReader.EffectiveAsync`), templates visible to the workspace (≤60, `TemplateCatalog.IsVisibleTo`), enabled instance sizes the workspace's plan allows (`AllowedSizeKeys`, same filter as the panel's choosers) |
| `get_deployment` | `app`, `number?` | latest (or #n): status, trigger, timestamps, commit, error, last 40 log lines — redacted by value against the app's secret env vars **and** attached databases' passwords (decrypted), then by shape |
| `get_usage` | — | `IQuotaService.GetUsageAsync` figures (used/max, 0 = unlimited) + `assistantRequestsLeftToday` |

All resolve names through `AgentResourceResolver` (workspace pinned explicitly + `ProjectAccessService.CanSee…`);
an invisible resource gets exactly the not-found sentence a missing one gets.

## 6. System prompt (verbatim, `AgentSystemPrompt.Rules`)

```
You are Harbora's assistant, built into the Harbora panel — a self-hosted platform where people run their apps and databases. You help one signed-in person manage their own workspace, and you have exactly their permissions: the tools you are given are the ones they may use.

Language: answer in the language the user writes in. If they write in Persian (Farsi), answer in Persian; if they write in English, answer in English.

How to work:
- Use the tools to look things up instead of guessing. Never invent names, ids, versions, statuses or numbers.
- Address resources by the names and slugs shown in the workspace snapshot below or returned by a tool. Never make up an id; the tools take names.
- When a request is ambiguous — which app, which environment, which version — ask one short question instead of guessing.
- Slugs use only lowercase Latin letters, digits and hyphens. When the user gives a Persian name, transliterate it into Latin letters for the slug (for example «فروشگاه» becomes "forushgah").
- Write tools do not perform anything when you call them. Each accepted call becomes a step of a plan that the user reviews and approves in the panel; Harbora runs the approved steps itself. After proposing steps, tell the user briefly what the plan will do and that it is waiting for their approval.
- Never say that something was created, changed, deployed, restarted or deleted unless a tool result says it happened.
- If a tool refuses a call, read the reason, correct the arguments and call it again, or explain the problem to the user. Do not repeat a call that was refused for the same reason.
- Tools never return passwords, keys or other secret values. Never ask the user to paste one into the chat.
- Keep answers short and practical.
```

Followed by "The panel's interface is in Persian/English; if the user's language is unclear, answer in …", the
snapshot, and the page line ("The user is currently looking at the page /apps/… — app "shop-web"
(shop/production). "This app" or "this database" means that one.").

## 7. Endpoints (`AssistantController`, `[Authorize]`, `[Route("assistant")]`, `[RequireFeature(PlatformFeatures.Assistant)]`)

| Method / path | Answer |
|---|---|
| `GET status` | `AgentStatusView` (unavailable also when today's cap is used up) |
| `GET conversations` | latest 30 `AgentConversationSummaryView` |
| `GET conversations/{id:guid}` | `AgentConversationView` (+ pending plan) · 404 `{error,messageEn,messageFa}` |
| `POST conversations/{id}/messages` (`id` = guid or `new`), body `AgentSendMessageRequest` | `text/event-stream`, `Cache-Control: no-cache, no-transform`, `X-Accel-Buffering: no`, response buffering disabled, flush per event; frames `event: {name}\ndata: {json}\n\n`, `started` first, `done` last, `error` instead of a 500 once begun. Before the stream: 400/403/404 `{error,messageEn,messageFa}` |
| `POST plans/{id:guid}/approve`, body `AgentApproveRequest` (may be empty) | 200 `AgentPlanResultView` · 404 `not_found` · 400 `confirmation_mismatch` (plan still pending) · 409 `not_pending` / `expired` / `revalidation_refused` |
| `POST plans/{id:guid}/reject` | 204 · 404 · 409 |
| `GET approvals` | `[{ tool, createdAt }]` (`AgentApprovalView`) |
| `POST approvals/{tool}/revoke` | 204 (idempotent) |

Every refusal body is `{ error, messageEn, messageFa, status?, stepIndex? }` — `messageEn`/`messageFa` are the
same pair the stream's `error` event carries (coordinator's request 1; the extra keys are additive).
All POSTs carry `[ValidateAntiForgeryToken]` (token in the `X-CSRF-TOKEN` header). Step `Index` (in
`AgentPlanStepView`, `AgentPlanStepResultView`, the `Confirmations` key and `stepIndex`) is the step's
**0-based position** (coordinator's request 2); human-facing text says "step Index + 1".

## 8. Tests

| File | Tests |
|---|---|
| `tests/Harbora.Tests/Agent/AgentTestKit.cs` | `ScriptedAgentChatClient` (queued answers, records snapshots of every request, no network), `FakeTool` (any kind, scripted verdicts/results, counts validations/executions), `AgentHarness` (the real core over EF InMemory with the panel's **real** capability policies), clock, audit recorder, protector |
| `Agent/AgentConversationServiceTests.cs` | 34 — the loop: read result in next request; unknown tool / non-object JSON (3 cases) / blank args / missing capability fed back; refused write → corrected call → plan; scope-aware order ("create then attach"); remembered safe tool auto-runs (`executed`); dangerous and destructive always carded even with an approval row (2); mixed plan carded; destructive without phrase refused; unfinished turn never auto-runs; entitlement locked (nothing stored, nothing sent); unavailable; provider failure; non-manager never sees provider text; daily cap counted per call and stops the turn; cap 0 = unlimited; 8-call ceiling; **a secret env value, echoed in a deployment error and log, appears in no recorded request and no stored message**; system prompt carries rules/snapshot/page; history replayed and title; new message supersedes the pending plan; another user's conversation; empty message (2); throwing tool; conversation view; history sanitiser; status; blank/repeated call ids |
| `Agent/AgentPlanServiceTests.cs` | 16 — runs in order and writes plan/conversation/audit; second approve refused, ran once; typed name exact (case, extras, empty, missing, wrong key) and plan stays pending; expiry at exactly 15 min, persisted; re-validation refusal runs nothing (409 code, plan failed); fresh scope; first failure stops the rest; throwing step leaks nothing; another person's / workspace's plan not found; reject audited, idempotent, final; only safe tools used by the plan are remembered; refused approval remembers nothing; kind changed since carding refused; destructive resource renamed refused; role lost refused; plan view |
| `Agent/AgentReadToolsTests.cs` | 11 — get_app secrets/domains/attachments/last 5; invisible = missing wording; another workspace invisible to every tool and the snapshot; scoped member sees only granted project; get_database no credentials + ambiguity; catalog versions/templates/sizes; catalog honours the plan's sizes; get_deployment last 40 lines with env and DB-password secrets redacted; get_usage; snapshot cap "and 7 more"; page ids invisible / non-path path dropped |
| `Agent/AgentPolicyTests.cs` | 33 — consent matrix (4), confirmation matching (10 cases incl. Persian), badges, registry by role (Owner/Member/Operator/Viewer) via real policies, duplicate names throw, spec; limit parsing (9 cases incl. Persian digits), reserve to limit and reset at the next UTC day, per-workspace counters, unlimited; redact-then-truncate; shape redaction; Persian unescaped JSON |
| `Http/AssistantHttpTests.cs` | 9 — status; locked workspace 403 JSON + no model call; POST without antiforgery header 400; SSE framing started→message→done, reopen, list, colleague 404; plan → approve over HTTP runs once, second approve 409, approvals list/revoke, audit row; destructive typed name 400 then 200, index 0-based, reject 204 then approve 409; re-validation refusal 409 with messageEn/messageFa; pre-stream refusals (400/404); provider failure is an `error` event |
| `tests/Harbora.Postgres.Tests/AgentPlanApprovalRaceTests.cs` | 4 — **two concurrent approvals execute a plan once**; approving on the creating context sees its own claim (tracked refresh); expired plan refused and persisted; six concurrent calls at a cap of 3 admit exactly 3 with one usage row. **Unverified locally — no Docker; they report Skipped here** |

A mutation check was run once: making `IsAutoApproved` ignore the kind failed 5 agent tests; restored and
rebuilt before the runs below.

## 9. Concerns and decisions to look at

1. **Locked workspace and the stream.** `[RequireFeature]` answers 403 JSON only when the request looks like
   JSON (`X-Requested-With: XMLHttpRequest`, or `Accept: application/json` without `text/html`); otherwise it
   redirects to `/features/assistant`. The pane's `fetch` calls should send `X-Requested-With: XMLHttpRequest`
   to get the JSON. Existing filter, unchanged.
2. **Superseding (not in the design).** A new message in a conversation expires any plan still pending there,
   and the model is told it did not run — so a stale card cannot be approved after the conversation moved on.
   The pane should drop an open card when the person sends a new message.
3. **`IAgentCapabilityCheck`.** The brief asks for `IAuthorizationService.AuthorizeAsync(user, capability)`;
   Infrastructure has no ASP.NET Core reference, so the core asks this small interface and the web host
   implements it over exactly that call. Hence one registration line in `Program.cs`, outside the
   `DependencyInjection.cs` block.
4. **A plan can stay `executing` forever** if the process dies mid-execution: no sweeper (the brief said none).
   Honest (it never claims success) but stuck; worth a reconciler later.
5. **Execution ignores client disconnects** after the claim (`CancellationToken.None`) — wave B tools must
   bound their own work (deploys go to the job queue anyway).
6. **Contract notes, no shape changed:** refusal bodies and `AgentApprovalView` are new shapes outside
   `AgentViews.cs` (in `AgentCoreModels.cs`); `AgentApproveRequest.Confirmations` binds from JSON object keys
   `"0"`, `"1"` … (verified over HTTP). A `revalidation_refused` approval is a 409 (per the coordinator), while
   the same situation in an auto-approved turn arrives as an `executed` event with status `failed` (there is no
   status code to carry it there).
7. **Snapshot "databases"** are all managed services (caches and brokers too), labelled by engine.
8. **Postgres lane unverified:** the migration has never been applied to Postgres and the four race tests have
   never run — CI must prove them.
9. The audit metadata's `step` is the 1-based ordinal a person reads; `stepIndex` the 0-based position.

## 10. Commands run and what they printed

Every build/test used `export MSBUILDDISABLENODEREUSE=1` and `-p:UseSharedCompilation=false`.

- Baseline before any change: `dotnet build Harbora.slnx --nologo -v q -p:UseSharedCompilation=false` →
  `BUILD_EXIT=0`, `28 Warning(s)` (all NU1900/NU1903 package warnings), `0 Error(s)`.
- Focused runs while iterating: `dotnet test tests/Harbora.Tests --no-build --filter
  "FullyQualifiedName~Harbora.Tests.Agent.|FullyQualifiedName~AssistantHttpTests"` → last:
  `Passed!  - Failed:     0, Passed:   103, Skipped:     0, Total:   103 - Harbora.Tests.dll`.
- `dotnet test tests/Harbora.Postgres.Tests --no-build --filter "FullyQualifiedName~AgentPlanApprovalRaceTests"`
  → `Skipped! - Failed: 0, Passed: 0, Skipped: 4, Total: 4` (no Docker).
- Full run on the final code before the last rebase (on `504a09f`): `Harbora.Tests.dll` 6677 passed, 0 failed.
- **Final, after rebasing onto `1e304fe`** (`LOG=/tmp/test-r6-agent-core.log`):
  - `dotnet build Harbora.slnx --nologo -v q -p:UseSharedCompilation=false` → `BUILD_EXIT=0`,
    `28 Warning(s)`, `0 Error(s)` — the same 28 package warnings as the baseline, no compiler warnings.
  - `dotnet test Harbora.slnx --nologo -v q --no-build` → `EXIT=0`, and:
    ```
    Skipped! - Failed:     0, Passed:     0, Skipped:   108, Total:   108 - Harbora.Postgres.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:   500, Skipped:    17, Total:   517 - Harbora.NodeAgent.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:    15, Skipped:     0, Total:    15 - Harbora.NodeIngress.Tests.dll (net10.0)
    Passed!  - Failed:     0, Passed:  6793, Skipped:     0, Total:  6793 - Harbora.Tests.dll (net10.0)
    ```
    `grep -c "Harbora.Tests.dll"` → 2 (present). No `[FAIL]` lines. 6793 = this branch's 103 agent tests +
    6690, the count agent A's report gives for master after A, B and C landed (not re-run separately here).

## 11. Fixes after review

Rebased onto master `a6c4b48` (agent E's chat pane) first — no conflicts. All fixes are in `038b03f`; the
sections above still describe the design, with the changes below on top.

### Important 1 — every claimed plan ends `executed` or `failed`

- `AgentPlanService.RunClaimedAsync` now takes **no** cancellation token and catches **every** exception from
  a tool after the claim — `OperationCanceledException` included, since nothing post-claim is the caller's
  cancellation (an `HttpClient.Timeout` throws `TaskCanceledException`). The same in re-validation.
- After a step fails or throws, the tracked changes that are not agent rows are detached
  (`AgentTracker.DetachForeignChanges`, new `src/Harbora.Infrastructure/Agent/AgentTracker.cs`) before the
  audit entry and the plan are saved, so an entity left `Added` by a save that threw (a unique-slug race) is not
  retried by the agent's own saves.
- Per-step results live in a `RunLedger` outside the work. Anything that still escapes goes to
  `FinishBrokenRunAsync`: every unsaved change is detached (`DetachAllChanges`), the running step fails, steps
  without a result are skipped, and the plan is finished. Status, `ResultsJson` and the conversation's tool
  messages are now written in **one** save (`FinishAsync` + `AddOutcomeMessagesAsync`). If even that save
  fails it is logged and the plan stays `executing`, the one state that claims nothing.
- `TryMoveAsync` refreshes the tracked plan with `CancellationToken.None` once the move has committed.
- On the auto-run path the person now gets an `executed` event with the failed step, not "nothing further was
  done".

### Important 2 — tool results fit the 4,000-character cut

- `list_resources` answers in pages (`page` argument, from 1). Each page is packed greedily to serialise under
  `ListResourcesTool.PageBudget` (3,600 chars), with the paging facts first:
  `{"page":1,"pages":4,"nextPage":2,"totalApps":230,"totalDatabases":1,"projects":[{"project","name","environments":[{"environment","protected"?,"apps":["slug (status, kind)"],"databases":["name (engine version, status)"]}]}]}`.
  A page past the end is refused ("There are only N pages."). The snapshot's "…and N more" line now says the
  tool answers in pages, and the person's summary names the page and what is on it.
- `get_deployment` redacts each line, clips it to 200 chars (`MaxLineLength`), then drops the **oldest**
  lines until the whole result serialises under `ResultBudget` (3,700); `earlierLinesLeftOut` says how many.
- `get_app` (not flagged, same hazard): environment variables moved to the end of the payload and values
  clipped to 120, so a long variable list loses its own tail rather than the deployments.

### Important 3 — every secret that reaches the app is redacted by value

`AgentSecrets.ForAppAsync` now also collects the secret entries of attached config groups
(`AppConfigGroups → ConfigGroupEntry.IsSecret`) and, in the same spirit, the attached storage buckets'
`EncryptedSecretKey`, email providers' `EncryptedPassword` and error trackers' `EncryptedDsn`. New:
`AgentSecrets.ForDatabaseAsync(db, protector, service, ct)` — the database's and its logical databases'
passwords.

### Minors

- **Expiry at approval** is now told to the conversation (`ExpireAsync`, shared with superseding).
- **Superseding runs after the gates:** `SendAsync` asks availability and the cap first (`GateAsync`); only
  a message that will be answered withdraws a pending card, so a refused message leaves the card approvable.
- **Validation must change nothing:** after `ValidateAsync` — in the loop and at re-validation — any new
  unsaved change outside the agent's rows is logged, detached and the step refused ("This step was refused:
  checking it tried to change data…"). `AgentPlanService.ValidationLeftChanges` (internal) is the shared
  check. A read tool that leaves changes has them detached and logged.
- **A read tool's own timeout** is a failed read for the model, not the end of the turn; only the request's
  own cancellation ends it.
- **`NormalizeCalls`** first reserves every usable id the reply carries, then generates ids
  (`call_r{round}_{n}`, suffixed `_2`, `_3`… until unique).
- **`get_database`** redacts its error by value (its own passwords) and by shape **before** clipping. Its
  constructor is now `GetDatabaseTool(HarboraDbContext, AgentResourceResolver, ProjectAccessService, ISecretProtector)`.
- **Resolver `AppAsync`** only considers visible apps, so an invisible slug match no longer hides a visible
  app carrying that display name.
- **Remembered tools when re-validation refuses — decided:** a plan refused at the re-check remembers
  **nothing**. "Don't ask again" is recorded only once re-validation has passed and the steps are about to
  run; like every other refusal, a 409 `revalidation_refused` changes nothing but the plan's own status.
- `get_usage` stays open to members (no change).

### Tests added (17), by file

| File | New tests |
|---|---|
| `tests/Harbora.Tests/Agent/AgentPlanServiceTests.cs` | a step throwing `TaskCanceledException` leaves a failed plan with results done/failed/skipped in the plan, the conversation and the audit; a step whose own save throws `DbUpdateException` (asserted to be that type) leaves a failed plan, nothing half-written saved, a clean tracker; an auto-run whose tool times out reports `executed`/failed; a validation that changes data is refused at the re-check and never saved; expiry at approval recorded in the conversation; a re-validation refusal remembers nothing |
| `tests/Harbora.Tests/Agent/AgentConversationServiceTests.cs` | a read tool timing out is a failed read and the turn continues; a write whose validation changes data is refused and nothing is saved; a message refused by the gates leaves the card approvable; a failed model call still spends its share of the cap; a generated call id never collides with a real one |
| `tests/Harbora.Tests/Agent/AgentReadToolsTests.cs` | through the real `AgentJson.ForModel`: all 231 resources (230 apps and a database; the snapshot shows 60) reachable across `list_resources` pages, each page at most 4,000 chars, untruncated, paging facts first; the last log line survives and lines are at most 201 chars; config-group, storage, mail and error-tracking secrets redacted by value in `get_deployment` and `get_app`; a database error redacted before clipping; an invisible slug does not hide a visible display name |
| `tests/Harbora.Tests/Http/AssistantHttpTests.cs` | approve, reject and revoke each refuse a POST without the antiforgery header (400), and the plan stays pending |

Mutation check: with both the post-failure detach and the broken-run fallback disabled,
`A_step_whose_save_throws_leaves_nothing_half_written_and_the_plan_still_finishes` failed; restored and
rebuilt before the runs below.

### Commands and output

All with `MSBUILDDISABLENODEREUSE=1`, `-p:UseSharedCompilation=false`, `LOG=/tmp/test-r6-agent-core.log`.

- `dotnet build Harbora.slnx --nologo -v q -p:UseSharedCompilation=false` → `BUILD_EXIT=0`, `28 Warning(s)`,
  `0 Error(s)` (the baseline's package warnings; no compiler warnings).
- Focused: `dotnet test tests/Harbora.Tests --no-build --filter "FullyQualifiedName~Harbora.Tests.Agent.|FullyQualifiedName~AssistantHttpTests|FullyQualifiedName~MigrationConsistencyTests"`
  → `Passed!  - Failed:     0, Passed:   122, Skipped:     0, Total:   122 - Harbora.Tests.dll`.
- Full: `dotnet test Harbora.slnx --nologo -v q --no-build` → `EXIT=0`:
  ```
  Skipped! - Failed:     0, Passed:     0, Skipped:   108, Total:   108 - Harbora.Postgres.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:   500, Skipped:    17, Total:   517 - Harbora.NodeAgent.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:    15, Skipped:     0, Total:    15 - Harbora.NodeIngress.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:  6819, Skipped:     0, Total:  6819 - Harbora.Tests.dll (net10.0)
  ```
  `grep -c "Harbora.Tests.dll"` → 2. 6819 = the coordinator's 6802 for master plus this branch, plus the 17
  new tests.

### Still unverified, and remaining concerns

- The Postgres lane (the migration and the four race tests) is still unverified locally — no Docker.
- `FinishBrokenRunAsync` cannot help when the database itself is unreachable: the plan then stays
  `executing` and the failure is only in the log. No reconciler exists for that case.
- Detaching what a failed step left behind assumes tools save their own work. A tool that returns
  **success** without saving still has its changes written by the agent's next save — deliberately not
  detached, since dropping them would make "done" untrue. Wave-B tools should save before returning.
