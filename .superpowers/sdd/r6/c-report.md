# C report — tool-capable chat client, agent settings, "test the agent model" (phase 1)

Branch `r6/chat-client`, worktree `Harbora-r6-c`. Status: **DONE** (concerns at the end).

## What was built

| File | What it is |
|---|---|
| `src/Harbora.Infrastructure/Agent/OpenAiCompatibleAgentChatClient.cs` | `IAgentChatClient` over OpenRouter / any chat-completions endpoint |
| `src/Harbora.Infrastructure/Agent/AgentSettingsService.cs` | reads/saves/validates the three `AgentSettingKeys`, resolves primary + fallbacks |
| `src/Harbora.Infrastructure/Agent/AgentModelBench.cs` | the "test the agent model" bench, its fake tools, its scoring |
| `src/Harbora.Infrastructure/DependencyInjection.cs` | one contiguous block (comment names this work) |
| `src/Harbora.Web/Controllers/SectionControllers.cs` | `SettingsController`: page load, `UpdateAssistant`, new `TestAgent` |
| `src/Harbora.Web/Views/Settings/Index.cshtml` | agent fields, test button, results table, error banner |
| `tests/Harbora.Tests/{AgentChatClientTests,AgentModelSettingsTests,AgentModelBenchTests}.cs`, `Http/AgentSettingsPageHttpTests.cs`, `Fakes/RecordingChatHandler.cs` | tests |

No contract file was changed (`AgentChatContracts.cs`, `AgentToolContracts.cs`, `AgentViews.cs`, `AgentSettingKeys.cs` untouched), `AssistantClient`/`AssistantService` untouched, no other `Agent/` file touched.

## Classes and signatures

```csharp
public sealed class OpenAiCompatibleAgentChatClient(
    AssistantService assistant, AgentSettingsService agentSettings, IHttpClientFactory httpClientFactory) : IAgentChatClient
{
    public const string HttpClientName = "harbora-agent-chat";          // the named HttpClient
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(90);
    Task<AssistantUnavailable?> GetAvailabilityAsync(CancellationToken ct);
    Task<string?> GetModelAsync(CancellationToken ct);                   // primary model, null when none resolves
    Task<AgentChatResponse> CompleteAsync(AgentChatRequest request, CancellationToken ct);
}

public sealed class AgentSettingsService(HarboraDbContext db)
{
    Task<AgentSettings> GetAsync(CancellationToken ct);                  // Model, FallbackModels, DailyRequestLimitText, DailyRequestLimit (effective)
    Task SaveAsync(AgentSettingsInput input, CancellationToken ct);      // takes the validated type only
    static AgentSettingsValidation Validate(string? model, string? fallbackModels, string? dailyRequestLimit);
    static AgentModelPlan Resolve(string? agentModel, string? assistantModel, string? fallbackModels); // Primary + de-duplicated Fallbacks
    static int EffectiveLimit(string? stored);
}

public sealed class AgentModelBench(IAgentChatClient client)
{
    static IReadOnlyList<AgentBenchCase> Cases { get; }                  // 6 prompts
    static IReadOnlyList<AgentToolSpec> ToolSpecs { get; }               // 9 fake tools
    Task<AgentBenchReport> RunAsync(CancellationToken ct);
}
// AgentBenchOutcome { Passed, Failed, NoAnswer }; AgentBenchRow; AgentBenchReport(Ran, Rows, Model, Passed, Failed, NoAnswer, Verdict, VerdictFa)
```

DI (`DependencyInjection.cs`): `AddHttpClient("harbora-agent-chat")`, `AgentSettingsService` (scoped), `IAgentChatClient -> OpenAiCompatibleAgentChatClient` (scoped), `AgentModelBench` (scoped).

**For agent D:** `AgentSettingsService.GetAsync(ct).DailyRequestLimit` is the effective daily cap (blank/garbage -> 300, `0` = unlimited). It is stored and validated here; **nothing enforces it** — the counter (`AgentUsageDay`) is D's.

## Request mapping

* **URL:** configured base (default `https://api.openai.com`), trailing `/` trimmed, then `/v1/chat/completions` — unless the base already ends in `/v1` (case-insensitive), then `/chat/completions`. `https://openrouter.ai/api` and `https://openrouter.ai/api/v1` both give `https://openrouter.ai/api/v1/chat/completions`. A base that is not an absolute http(s) URL fails before anything is sent.
* **Headers:** `Authorization: Bearer <decrypted key>` when a key is stored (a local endpoint may have none); `HTTP-Referer: https://github.com/sadrazkh/Harbora` and `X-Title: Harbora` only when the host is `openrouter.ai` or a subdomain (`openrouter.ai.evil.example` and `notopenrouter.ai` get nothing).
* **Body (built with `JsonObject`):** `model` (primary), `messages`, `tools`+`tool_choice:"auto"` only when tools are present, `max_tokens`, `models` (`[primary, ...fallbacks]`) only when fallbacks exist after removing repeats of the primary.
* **Messages:** `system`/`user` -> `{role, content}`; assistant with calls -> `content` null when blank (OpenAI's own shape) plus `tool_calls:[{id, type:"function", function:{name, arguments:"<json text>"}}]` (arguments stay the model's raw text; blank -> `"{}"`); tool -> `{role:"tool", tool_call_id, content}`.
* **Tools:** `{type:"function", function:{name, description, parameters}}`, `parameters` parsed from `ParametersJsonSchema` into a JSON object. A schema that is not a JSON object fails the call naming the tool (it would otherwise be hidden behind an invented empty schema).
* **Model resolution:** `assistant.agent_model` if non-blank, else `assistant.model`; `assistant.agent_fallback_models` comma-split, trimmed, de-duplicated.

## Response mapping

`choices[0].message`: `content` (string, null, or an array of text parts — joined), `tool_calls[]` -> `AgentToolCall(id, function.name, arguments)`; `arguments` as a string is kept verbatim, as an object/array is normalised to its raw JSON text, absent/null -> `"{}"`; a call with no `id` gets `call_<n>` so it can still be answered. `finish_reason`, root `model` (the answering model), `usage.prompt_tokens`/`completion_tokens` (null when the provider sent no usage — never zeros).

## Failures (all `AgentChatResponse.Failed(en, fa)`, never exceptions)

| Case | English text (Persian is its own sentence) |
|---|---|
| not available | the availability reason, in both languages; **no HTTP call is made** |
| bad base URL | `The base URL "..." is not a valid http(s) address.` |
| non-2xx | `The AI provider refused the request (<code>). <error.message [— error.metadata.raw] or trimmed body>` |
| 429 | `The AI provider is rate limiting requests (429). Wait a moment and try again, or pick a model with a higher limit. <detail>` |
| timeout (90 s, covers reading the body) | `The AI provider did not answer within 90 seconds.` |
| connection failure | `The AI provider could not be reached: <HttpRequestException.Message>` |
| 200, not JSON | `The AI provider's reply was not JSON. <whitespace-collapsed first 300 chars>` |
| 200 carrying `error` (object or string) | `The AI provider reported an error: <message>` |
| no choices / no message | `The AI provider's reply had no choices.` |
| **empty reply** (no text and no tool calls) | `The model returned neither text nor a tool call (finish reason: length).` |

Details are capped at 300 characters. OpenRouter's `error.metadata.raw` is appended because "Provider returned error" alone does not say whether a model is rate limited or gone. The caller's own cancellation is **not** swallowed: it propagates as `OperationCanceledException` (only our budget expiring or HttpClient's timeout becomes the timeout value).

Availability order: off -> (enabled and provider is `anthropic`: *"The agent needs an OpenAI-compatible provider such as OpenRouter; Anthropic's own API is not supported for it."* + Persian) -> `AssistantAvailability.Check` with the resolved model (no provider, no key on a non-local endpoint) -> no model resolves (*"No agent model is configured. Set one in the AI assistant settings, or set the assistant's model."* + Persian). Anthropic is deliberately ahead of the key check: adding a key cannot fix it.

## Settings page

* The form `POST /settings/assistant` (`PlatformManage`) gains `agentModel`, `agentFallbackModels`, `agentDailyRequestLimit` in a "In-panel AI agent" block under the existing fields, EN + FA labels and help; the help says OpenRouter's base URL is `https://openrouter.ai/api` and model ids look like `vendor/model:free`. The limit box is a text input (`inputmode=numeric`), not `type=number`, so Persian digits are accepted.
* **Validation** (`AgentSettingsService.Validate`, before anything is stored — a refused form does not half-save the provider/key fields beside it): limit blank = default 300, otherwise Persian/Arabic-Indic digits are read as digits and the text must be digits only and fit `int` (`-1`, `+5`, `1.5`, `1,000`, `abc`, `2147483648` refused; `0` accepted = unlimited; `007` stored as `7`); model ids may not contain whitespace/control characters or exceed 200 chars; fallbacks are trimmed, blanks and repeats dropped, stored comma-joined without spaces. A refusal is bilingual, via `TempData["Error"]`.
* **Found while doing this:** `Settings/Index.cshtml` never rendered `TempData["Error"]`, so the existing "Test connection" failure (`TestAssistant` sets it) was silently dropped. The page now draws it (`data-settings-error`), which fixes that too.
* "Settings saved." is now bilingual (it was English-only).

## "Test the agent model"

`POST /settings/assistant/agent-test` (`PlatformManage`, antiforgery) -> `AgentModelBench.RunAsync`, audit `assistant.agent_tested` (metadata `{model, prompts, passed, failed, noAnswer}`; written only when something was actually asked of the provider), and the **page itself is returned** with the results (`ViewBag.AgentBench`) rather than a redirect — the table does not fit in the cookie-backed TempData. Consequence: refreshing that page re-posts (the browser warns) and would spend six more requests.

Prompts (fixed order, sequential; each is one `CompleteAsync` with a short system message + the prompt + all 9 fake tools):

| # | Prompt | Expected call |
|---|---|---|
| fa | `یک دیتابیس Postgres به اسم shop-db بساز` | `create_database(engine="postgres", name="shop-db")` |
| fa | `اپ shop را ری‌استارت کن` | `restart_app(app="shop")` |
| fa | `دیتابیس shop-db را به اپ shop وصل کن` | `attach_database(app="shop", database="shop-db")` |
| en | `list my apps` | `list_resources(kind="apps")` |
| en | `Create a MySQL database called blog-db` | `create_database(engine="mysql", name="blog-db")` |
| en | `Attach the database blog-db to the app blog` | `attach_database(app="blog", database="blog-db")` |

Fake tools (defined only in the bench, specs with no code behind them): `list_resources`, `get_app`, `create_project`, `create_database`, `attach_database`, `set_env_vars`, `deploy_app`, `restart_app`, `stop_app`. **Nothing is ever executed**; the bench holds only the chat client.

**Scoring:** *Passed* = exactly one tool call, tool name equal (ordinal), `arguments` is a JSON object holding every key argument with the expected value (case-insensitive, trimmed; non-strings compared by raw JSON text; extra arguments ignored). *Failed* = text instead of a call, wrong tool, a second call (even if the first was right — it did something not asked for), invalid-JSON/non-object arguments, missing/wrong argument. *NoAnswer* = the provider returned a failure value (rate limit, error, timeout, empty reply) — counted separately because it says nothing about the model. The whole test has a 180 s ceiling; prompts past it are NoAnswer rows.

Verdict (EN+FA, one line): all six -> "Passed 6 of 6 ..."; none judged -> "could not be judged"; failures: `passed*3 >= judged*2` -> "Usable with care", else "Not recommended"; always with `Persian x/3, English y/3` and a count of unanswered prompts. Table columns: prompt, expected, what the model did, result pill, latency (ms), answering model.

## Tests (`tests/Harbora.Tests`)

| File | Tests |
|---|---|
| `AgentChatClientTests.cs` | 66 — URL normalisation (9 cases), named client, bearer/no-key, OpenRouter headers (7 hosts), body shape, tools as object, full tool-call round trip, `models` only with fallbacks, model precedence; text/tool-call/object-arguments/null-content/missing-id/array-content/usage/finish-reason/answering-model parsing; every failure mode in both languages; availability reasons; DI wiring |
| `AgentModelSettingsTests.cs` | 41 — validation, Persian digits, normalisation, resolve, effective limit, round trip, zero vs default |
| `AgentModelBenchTests.cs` | 26 — fixed prompts/tools are consistent, request shape, scoring (pass, wrong tool, wrong/missing arg, case-insensitive, text, second call, bad JSON), NoAnswer vs Failed, verdict thresholds, unavailable, time budget, cancellation |
| `Http/AgentSettingsPageHttpTests.cs` | 12 — real pipeline: save + render back, blank, refused limits (visible error, nothing saved), zero, member refused (`/account/denied`) on both POSTs, agent block not rendered for a member, unconfigured test (no table, no audit), full bench through the real controller/bench/client against a scripted provider (6 requests, URL/auth/headers/9 tools, 5 pass + 1 fail rendered, audit row), all-429 provider (6 no-answer rows, no failures) |
| `Fakes/RecordingChatHandler.cs` | fake `HttpMessageHandler` that records requests; no test can reach a real provider |

I also checked the tests bite: with seven deliberate mutations applied at once (always-add `/v1`, always send `models`, always send OpenRouter headers, drop the empty-reply failure, drop the two-call rule, drop the argument comparison, accept signed limits) 20 tests failed; reverted with `git checkout` afterwards.

## Commands and output

```
export MSBUILDDISABLENODEREUSE=1
dotnet build Harbora.slnx --nologo -v q -p:UseSharedCompilation=false   -> BUILD_EXIT=0, 0 Error(s); only NU1900/NU1903 NuGet feed/advisory warnings (none from compilation)
dotnet test  Harbora.slnx --nologo -v q --no-build                       -> EXIT=0
Passed!  - Failed:     0, Passed:  6574, Skipped:     0, Total:  6574 - Harbora.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   500, Skipped:    17, Total:   517 - Harbora.NodeAgent.Tests.dll
Passed!  - Failed:     0, Passed:    15, Skipped:     0, Total:    15 - Harbora.NodeIngress.Tests.dll
Skipped! - Failed:     0, Passed:     0, Skipped:   104, Total:   104 - Harbora.Postgres.Tests.dll
grep -c "Harbora.Tests.dll" /tmp/test-r6-chat-client.log  -> 2 (present)
```

`Harbora.Tests`: **6574** (baseline 6429, +145 = 66 + 41 + 26 + 12). Logs: `/tmp/test-r6-chat-client.log{,.build,.exit}`.

## Not verified / honest limits

* **Nothing here has touched a real provider** (as required). Unverified against real OpenRouter: that it accepts `model` and `models` together, that `tool_choice:"auto"` plus `models` behaves as documented, how particular free models shape `tool_calls`, and — the thing the button exists to measure — whether any given free model calls tools well in Persian.
* The settings page and results table were checked as rendered HTML from the in-process test host (structure, `data-` attributes, counts), **not** visually in a browser: no running panel is possible here (no Postgres) and I did not stand up a static render with the Vite CSS. Layout/RTL polish is unseen.
* `Harbora.Postgres.Tests` was skipped (104 skipped, no Postgres here). Nothing I wrote is database-specific: only plain `Settings` rows, no schema change.
* The daily limit is validated and stored but **not enforced anywhere** (D's job); the bench's six calls are not counted against any cap because that counter does not exist yet.

## Concerns for the integrator / other agents

1. **Empty reply = failure.** A response with neither text nor tool calls (typical when a reasoning model spends its whole `max_tokens`) is returned as `Ok=false` with the finish reason in the message, following `AssistantClient`'s "an empty answer is a failure" rule. D's loop will never see `Ok=true` with a blank message.
2. **Caller cancellation throws** `OperationCanceledException` out of `CompleteAsync` (only provider timeouts/network failures are values), as `HttpClient` callers expect.
3. **Tool calls without an id** get a synthetic `call_<n>`; D must answer them by that id like any other.
4. **`SettingsController` constructor** gained `AgentSettingsService` and `AgentModelBench`; nothing else constructs it by hand (the build proves it). `Index` was split into `PrepareIndexAsync` so the test POST can render the same page.
5. `DependencyInjection.cs` and `SectionControllers.cs`/`Settings/Index.cshtml` are shared files; my edits are one contiguous DI block, three controller regions, and additions inside the assistant section — expect trivial textual merges with D/E if they touch the same neighbourhood.
6. The agent test returns a page on POST (no redirect) — refreshing it re-posts and spends six requests.
