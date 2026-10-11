# Wave B — write tools: rules shared by F (app tools) and G (project and database tools)

Read first: `CONSTRAINTS.md` (this folder), the design doc
`docs/superpowers/specs/2026-10-11-harbora-assistant-agent-design.md`, and `d-report.md` (the agent core
you plug into: its loop, plan engine, resolver and read tools). The contracts in
`src/Harbora.Infrastructure/Agent/AgentToolContracts.cs` are fixed.

## What a write tool is

A class implementing `IAgentTool` — derive from `AgentWriteTool` in
`src/Harbora.Infrastructure/Agent/Tools/AgentWriteTool.cs` (already on master: it maps `ServiceRefusal`s
to `AgentValidation`/`AgentToolResult` in both languages, so every tool refuses the same way) — registered
in DI as `IAgentTool` in **one contiguous block per agent** in `DependencyInjection.cs`, with a comment
naming your work. The registry offers it to the model only when the user holds its `Capability`.

- **Arguments are names, never ids.** Apps by slug (or display name), databases by name (optionally
  project/environment), projects by slug or name, environments by project + slug. Resolve them only with
  `AgentResourceResolver` (`AppAsync`, `DatabaseAsync`, `ProjectAsync`, `EnvironmentAsync`): it applies the
  workspace filter and project visibility, and words "cannot see" exactly like "does not exist".
- **Seeing is not touching.** The resolver checks visibility; your tool must also check the capability on
  that resource before validating and again before executing — `ProjectAccessService.CanTouchAppAsync` /
  `CanTouchServiceAsync` / `AllowsAsync(placement, capability)` with the same capability the matching
  controller action's policy uses. Engine and operations services (`IDeploymentEngine`,
  `IAppOperationsService`, `IManagedServiceEngine`, `ProjectService`) do **no** access checks of their own.
- **Planned resources.** A step may name something an earlier step of the same plan creates
  ("create shop-db, then attach shop-db to shop"). In `ValidateAsync`, when the resolver cannot find a
  name but `scope.Plans(kind, name)` is true, treat it as will-exist: run only the checks that do not need
  the row (name format, catalog values, the other side of the relation), and say so in the step detail.
  `ExecuteAsync` resolves for real. Creation tools set `AgentValidation.Creates` so later steps can refer
  to what they make.
- **`ValidateAsync` changes nothing.** Call the phase-0 service's `Validate…Async`; never call a mutating
  method; never `Add`/`Remove` on the context. The agent loop refuses a validation that leaves tracked
  changes and drops them — do not rely on that, it is a guard.
- **`ExecuteAsync` re-checks and does the work through the service's mutating method** (which re-validates
  under the creation lock and saves). **Save your own work before returning** — the loop's next save would
  otherwise write anything you left tracked, after the user saw a result. Return `Ok = true` only when the
  service returned success. Do not wait for long work: a deploy or a provisioning is *queued* — say
  "queued", return the deployment or resource link, never "running" or "done".
- **Bounded run time.** No polling, no sleeps. If you call anything over HTTP, give it its own timeout.
- **Results.** `ResultJson` goes to the model: small, factual, never a secret value (no passwords,
  connection strings, secret env values, tokens). `SummaryEn`/`SummaryFa` and `Links` (the panel URLs of the
  resource pages — find the real routes in the controllers) go to the person.
- **Card text.** `TitleEn/TitleFa` is a short imperative ("Create database shop-db" /
  «ساخت دیتابیس shop-db»); `DetailEn/DetailFa` names engine/version/size/environment or whatever the person
  needs to decide; `WarningsEn/Fa` for real consequences (a redeploy replaces running containers; the first
  hour is charged when the plan has a price and validation cannot check the wallet; a protected environment
  will need approval). Persian in Persian, not transliterated English.
- **Destructive tools** return `ConfirmationPhrase` = the resource's exact name (its slug), and their
  detail says what is lost (data, volumes, domains).
- **Kinds** follow the design: SafeWrite = additive or reversible; DangerousWrite = can break a running app
  or cost money; Destructive = deletes. When unsure, choose the stricter kind and say why in the report.
- **Deploys queued by a tool** use `DeploymentTrigger.Assistant` and the user's id.

## Known traps (from the phase-0 reviews)

- `DatabaseCreateRequest.EnvironmentId` that the workspace does not own silently falls back to the default
  environment (kept for parity). Resolve the environment yourself first, and after create compare the
  result's environment with the one you asked for — a mismatch is a failure, reported as such.
- Validation cannot check billing; a create that validated can still be refused for payment at execute.
- `ServiceRefusal` has no machine code; map its text through `AgentWriteTool`, do not parse it.
- The shared fixture workspace in `HarboraWebFactory` is moved onto a plan where the assistant is
  **Locked** by `AppAddressHttpTests`; HTTP tests that need the assistant must seed their own workspace
  with the feature enabled (see `AssistantPaneHttpTests` / `AssistantHttpTests`).

## Tests (per tool, EF InMemory, following `tests/Harbora.Tests/Agent/AgentTestKit.cs`)

Validate: happy path with card text; each refusal it maps; the planned-resource path; capability refused
for a Viewer and for a scoped member without a grant; another workspace's resource is "not found";
validate leaves the context unchanged. Execute: happy path (row exists, link, bilingual summary,
`ResultJson` without secrets); a refusal at execute (state changed after validate) returns `Ok = false`
and changes nothing. Kind and capability asserted. Plus scenario tests through `AgentConversationService`
with the scripted fake chat client (see D's tests) — your brief names them.

## Report

Full report to `.superpowers/sdd/r6/<letter>-report.md` in your worktree, committed (the folder is matched
by `.gitignore`; use `git add -f` for the report): every tool with kind, capability, arguments, what it
calls, card text, tests and counts, exact commands and output lines, what is unverified. Return only:
status, commits, one-line test summary, concerns.
