# In-panel AI agent — design

Approved by the owner on 2026-10-11.

## What it is

A chat pane in the panel. The owner (and, later, customers who buy the feature) types in Persian or
English — "create a Postgres 16 database called shop-db, attach it to shop and redeploy" — and the
agent does it. The model is reached through OpenRouter (or any OpenAI-compatible endpoint) with the
owner's own key; the owner has credit for ~1000 requests/day on free models.

There is no separate "text to function" service: the model's own tool calling is that. Harbora's job is
the set of tools, and an executor that never changes anything the user did not consent to.

## Decisions

| Question | Decision |
|---|---|
| Approach | Tool-calling agent; reads run freely; writes become a plan the user approves; Harbora executes approved steps itself, without the model in the loop |
| Pane position | The side opposite the main menu (logical `end`: left in Persian, right in English), overlaying content, full screen on mobile |
| Who | Every workspace user, each with exactly their own capabilities and project access; gated by the `assistant` entitlement, `Locked` by default, so in practice only the provider (owner) workspace has it until the owner grants it |
| Consent | `Read` runs immediately. `SafeWrite` shows a plan the first time; the user may tick "don't ask again" for that tool, after which it runs without a card. `DangerousWrite` is always confirmed. `Destructive` (delete) is always confirmed by typing the resource's name |
| Delete | In v1, with typed confirmation |
| Model | Platform setting: a model plus comma-separated fallbacks (OpenRouter `models`), and a "test model" button that runs canned Persian/English prompts |
| Budget | Per-workspace daily cap on model calls (default 300, 0 = unlimited); at most 8 model calls per user message |

## What exists and is reused

- `Infrastructure/Assistant`: platform-wide settings (`assistant.*`), the encrypted API key, availability
  in both languages, `AssistantRedaction`. The agent shares provider, base URL, key and the enabled flag.
- Capabilities, `ProjectAccessService`, quotas, entitlements (`IFeatureGate`, `[RequireFeature]`),
  `IAuditLogger`, `IDeploymentEngine`, `IAppOperationsService`, `IManagedServiceEngine`,
  `ProjectService`, `TemplateDeploymentService`, `LogicalDatabaseService`.
- Vue islands (`Scripts/main.ts`), the `_Layout` shell, `IHtmlLocalizer`/`IsFa` strings.

## What has to be built first

App creation, database creation, attach/detach, env vars, domains and volumes are written inline in
controller actions (`AppsController.Create`, `DatabasesController.Create`, …) and several of them write
no audit entry. They are extracted into services that both the controllers and the agent call, with a
side-effect-free validate method beside each mutating one. This is worth having without the agent too:
it is what lets the API and CLI create resources later.

## Contracts (committed before any agent started)

- `src/Harbora.Infrastructure/Agent/AgentChatContracts.cs` — chat messages, tool specs, `IAgentChatClient`.
- `src/Harbora.Infrastructure/Agent/AgentToolContracts.cs` — `AgentToolKind`, `IAgentTool`,
  `AgentToolContext`, `AgentPlanScope`, `AgentValidation`, `AgentToolResult`.
- `src/Harbora.Infrastructure/Agent/AgentViews.cs` — the JSON the pane reads, and SSE event names.
- `src/Harbora.Infrastructure/Agent/AgentSettingKeys.cs` — agent model, fallbacks, daily cap.
- `PlatformFeatures.Assistant` (`"assistant"`, Locked) and `DeploymentTrigger.Assistant = 7`.

## How one message flows

1. `POST /assistant/conversations/{id}/messages` with `{ text, page }` (`id` = `new` starts one). The
   response is `text/event-stream` (events in `AgentStreamEvents`). Everything runs inside this request,
   so the workspace filter, `ICurrentUser` and project access behave exactly as for a click.
2. The orchestrator checks: entitlement, availability, daily cap. It builds the system prompt (rules,
   language: answer in the user's language), a compact snapshot of what the user can see (projects,
   environments, apps, databases — names and states, capped), the page context, the last 20 messages,
   and the tools the user's capabilities allow.
3. It calls the model. For each tool call:
   - unknown tool, bad JSON, missing capability → a tool message with the reason, back to the model;
   - `Read` → executed now; result (truncated, redacted) back to the model; a `tool` event to the pane;
   - a write → `ValidateAsync` with the plan scope; refused → reason back to the model so it can fix the
     call; accepted → added to the draft plan, and the model is told "added as step N; nothing has run
     yet; the user will approve".
4. It loops until the model answers with text, or 8 model calls. Then:
   - no draft steps → just the `message`;
   - every step is a remembered `SafeWrite` → executed now, `executed` event with results;
   - otherwise → the plan is stored (`pending`, expires in 15 minutes) and sent as a `plan` event.
5. `POST /assistant/plans/{id}/approve` `{ confirmations, rememberTools }`: the plan must belong to this
   user and workspace and be `pending` and unexpired; destructive steps' typed text must equal their
   `ConfirmationPhrase`; the status moves `pending → executing` with a conditional update (one
   execution, ever). Every step is validated again with a fresh scope — if any is now refused, nothing
   runs. Steps execute in order; the first failure stops the rest (`skipped`). Results are stored as
   tool messages in the conversation, so the next turn knows what happened. Remembered tools are saved.
6. Long work (deploys, provisioning) continues in the job queue; results carry links, and the pane
   follows status through the existing pages/endpoints.

The model is told that a write tool call does not perform anything. The pane never says "done" for a
step that has not executed — the defining defect of this codebase is a surface that reports success for
work it never did.

## Data

`AgentConversation` (workspace, user, title, timestamps), `AgentMessage` (role, text, tool calls JSON,
tool call id, tool name, tokens), `AgentPlan` (conversation, user, workspace, status, steps JSON,
results JSON, expires, executed at), `AgentToolApproval` (user, workspace, tool name, created),
`AgentUsageDay` (workspace, UTC date, model calls). Workspace-filtered like other tenant data.

## Safety rules

- The agent has exactly the user's permissions: each tool declares a capability; each tool's own checks
  (project access, quotas, protected environments) are the same code the panel runs.
- Secret values never reach the model: tools report that a secret is set, never its value; every tool
  result passes through `AssistantRedaction`.
- Every executed step writes an `assistant.step_executed` audit entry (conversation, plan, step, tool),
  beside whatever the underlying service audits.
- Tools address resources by name/slug, never by ids the model would have to invent.

## Tools in v1

Read: overview of resources, app details, database details, catalog (engines, versions, templates,
sizes), deployment status with the last log lines, usage and quota.

SafeWrite: create project, add environment, create database, create app (Git, image, template),
deploy template, attach database, set env vars, add domain, add volume, deploy, restart.

DangerousWrite: rollback, detach database, delete env var, remove domain, resize/scale, stop app,
rotate a database password.

Destructive (typed): delete app, delete database, delete project, remove volume.

## Phases

| Phase | Work | Depends on |
|---|---|---|
| 0a | Extract app creation, env vars, domains, volumes into services; add missing audits | — |
| 0b | Extract database creation, attach/detach, removal refusals into services; add missing audits | — |
| 1 | Tool-capable chat client, agent settings, "test model" | contracts |
| 2 | Agent core: data, registry, consent policy, loop, read tools, plan engine, endpoints, cap | contracts |
| 3 | The chat pane (Vue island), topbar button, plan card, typed confirmation | contracts |
| 4 | Write and destructive tools over the phase-0 services | 0a, 0b, 2 |
| 5 | Learning-center page, final wiring and end-to-end checks | 1–4 |

## Testing

A scripted fake `IAgentChatClient` drives the loop: read tool execution, plan creation, refusal fed
back to the model, remembered tools auto-executing, dangerous/destructive always carded, typed phrase
mismatch refused, plan expiry, re-validation at approval, Viewer refused writes, another workspace's
resources invisible. The Postgres lane proves two concurrent approvals execute a plan once. The phase-0
extractions keep every existing HTTP test passing unchanged.
