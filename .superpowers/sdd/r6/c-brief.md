# C — A tool-capable chat client, agent settings, and "test the model" (phase 1)

## Why

The in-panel agent (see the design doc) talks to a model through OpenRouter with the owner's key, using
tool calling. The existing `AssistantClient` (`src/Harbora.Infrastructure/Assistant/AssistantClient.cs`)
is single-turn, text-only, sends no `tools`, and treats a tool-call-only reply (`content: null`) as
"empty answer". You build the client the agent will use — implementing the contract already on master,
`IAgentChatClient` in `src/Harbora.Infrastructure/Agent/AgentChatContracts.cs` — plus the settings it
needs and a button that tells the owner whether a chosen (free) model can actually do Persian tool calls.

## Build

1. **`OpenAiCompatibleAgentChatClient : IAgentChatClient`** in `src/Harbora.Infrastructure/Agent/`.
   - Configuration: `AssistantService.GetConfigAsync` (shared provider, base URL, decrypted key, enabled
     flag) plus `AgentSettingKeys.Model` (blank → `assistant.model`) and `AgentSettingKeys.FallbackModels`
     (comma-separated). Read them the same way `AssistantService` reads settings (`IgnoreQueryFilters`).
   - `GetAvailabilityAsync`: reuse `AssistantAvailability.Check`, and additionally refuse, in both
     languages, when the provider is `anthropic` ("the agent needs an OpenAI-compatible provider such as
     OpenRouter"), and when no model resolves.
   - `CompleteAsync`: POST chat-completions. URL: follow `AssistantClient`'s convention (base +
     `/v1/chat/completions`, default base `https://api.openai.com`) **but** do not double `/v1` when the
     configured base already ends in `/v1` (the owner may paste `https://openrouter.ai/api/v1`).
     Body: `model`, `messages` (system/user/assistant-with-`tool_calls`/`tool` with `tool_call_id`, in
     the exact OpenAI shape), `tools` (`[{type:"function", function:{name, description, parameters}}]`
     with `parameters` parsed from `ParametersJsonSchema` into JSON, not sent as a string),
     `tool_choice: "auto"` when tools are present, `max_tokens`. Send OpenRouter's `models` array (the
     primary followed by the fallbacks) **only when fallbacks are configured** — other OpenAI-compatible
     endpoints may reject unknown fields. Add `HTTP-Referer` and `X-Title: Harbora` headers only when the
     host is `openrouter.ai`.
   - Parse `choices[0].message`: `content` (may be null), `tool_calls[].id/function.name/function.arguments`
     — `arguments` is normally a JSON string, but some providers send an object: normalise to the raw JSON
     text. Also `finish_reason`, the answering `model`, `usage.prompt_tokens/completion_tokens`.
   - Failures are values, never exceptions, always bilingual: non-2xx (include the provider's
     `error.message` when the body has one — OpenRouter returns `{ "error": { "message", "code" } }`),
     429 (rate limited — say so plainly in both languages), timeout (90 s), non-JSON body, a 200 whose
     body carries an `error` object, a response with no choices.
   - A named `HttpClient` registered in DI; register `IAgentChatClient` (scoped) in
     `DependencyInjection.cs` as one contiguous block with a comment naming this work.
2. **Settings.** The assistant section of the settings page (`src/Harbora.Web/Views/Settings/Index.cshtml`,
   about lines 48–142; actions in `src/Harbora.Web/Controllers/SectionControllers.cs`, `POST
   /settings/assistant` about :608, `/settings/assistant/test` about :624, page-load wiring about
   :397) gains: agent model, fallback models, daily request limit (`AgentSettingKeys.DailyRequestLimit`;
   blank → `AgentSettingKeys.DefaultDailyRequestLimit`, `0` = unlimited; validate it is a non-negative
   integer). Same capability gate as the existing fields (`PlatformManage`). Persian and English labels
   and help text; the help text should say that OpenRouter's base URL is `https://openrouter.ai/api` and
   that model ids look like `vendor/model:free`.
3. **"Test the agent model"** — a button in that section → `POST /settings/assistant/agent-test`. An
   `AgentModelBench` runs a fixed set of about six prompts (three Persian, three English, e.g. "یک
   دیتابیس Postgres به اسم shop-db بساز", "list my apps", "اپ shop را ری‌استارت کن") against a fixed set
   of **fake tool specs** defined inside the bench (names and schemas shaped like the real ones the
   design lists: `list_resources`, `create_database`, `attach_database`, `restart_app`, …). For each
   prompt it makes one `CompleteAsync` call and checks that the model called the expected tool with the
   expected key arguments (e.g. engine and name). **Nothing is ever executed** — the bench only inspects
   what the model asked for. The page shows a table: prompt, expected, what the model did, pass/fail,
   latency, the answering model; plus a one-line verdict. Audit it (`assistant.agent_tested`).
4. **Tests** (`tests/Harbora.Tests`), with a fake `HttpMessageHandler` (see `tests/Harbora.Tests/Fakes/
   StubAgentHandler.cs` or the per-file `Responder` pattern in `BackupDeliveryTests.cs`): request shape
   (URL normalisation, auth header, tools serialised with `parameters` as an object, a full
   tool-call round trip in `messages`, `models` sent only with fallbacks, OpenRouter headers only for
   openrouter.ai); response parsing (text, tool calls, object-shaped arguments, null content, usage,
   finish reason); every failure mode above; availability reasons; settings save/validation; the bench's
   scoring on scripted responses (a correct call passes, a wrong tool or wrong argument fails, plain text
   fails).

## Do not touch

`AssistantClient`'s existing behaviour (the deployment explainer keeps working unchanged), controllers
other than the settings actions, `Agent/` files other than your new ones (the contracts are fixed; agent
D is building the orchestrator in parallel against them), `_Layout.cshtml`, `_Topbar.cshtml`, `Scripts/`.

## Report

Full report to `.superpowers/sdd/r6/c-report.md` in your worktree, committed: classes and signatures,
request/response mapping, failure texts, settings and validation, bench prompts and scoring rule, test
files and counts, exact commands and output lines. Return only: status, commit list, one-line test
summary, concerns.
