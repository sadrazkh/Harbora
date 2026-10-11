using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harbora.Infrastructure.Assistant;

namespace Harbora.Infrastructure.Agent;

/// <summary>
/// The in-panel agent's connection to a model: one chat-completions round trip, with tools, against
/// OpenRouter or any endpoint that speaks OpenAI's shape.
///
/// <para>
/// Shares provider, base URL, key and the enabled flag with the deployment explainer
/// (<see cref="AssistantService.GetConfigAsync"/>) and adds the agent's own model and fallbacks
/// (<see cref="AgentSettingKeys"/>). It is a separate class from <see cref="AssistantClient"/> because
/// that one is single-turn and text-only, and a reply whose content is null — which is what a tool
/// call looks like — is an "empty answer" there. Changing it would change the explainer.
/// </para>
///
/// <para>
/// Every way this can fail comes back as <see cref="AgentChatResponse.Failed"/>, in both languages,
/// with the provider's own words where it gave any. The one exception that does escape is the
/// caller's own cancellation: that is the caller's decision, not a failure of the provider.
/// </para>
///
/// <para>
/// The streamed overload lives in <c>OpenAiCompatibleAgentChatClient.Streaming.cs</c>; it builds the
/// same request and returns the same response, only reading the answer as it is written.
/// </para>
/// </summary>
public sealed partial class OpenAiCompatibleAgentChatClient(
    AssistantService assistant,
    AgentSettingsService agentSettings,
    IHttpClientFactory httpClientFactory) : IAgentChatClient
{
    /// <summary>
    /// The named client in DI. Named so a proxy or a recording handler can be put behind the agent
    /// without touching the default client every other feature in the panel shares.
    /// </summary>
    public const string HttpClientName = "harbora-agent-chat";

    private const string DefaultBaseUrl = "https://api.openai.com";

    /// <summary>OpenRouter shows these in its app rankings; sent only to OpenRouter.</summary>
    private const string OpenRouterReferer = "https://github.com/sadrazkh/Harbora";

    private const string OpenRouterTitle = "Harbora";

    /// <summary>
    /// A tool-calling turn is slower than the explainer's single answer (60 s there), and free models
    /// queue. Enforced here rather than on the HttpClient so a test can shorten it and so reading the
    /// body is inside the same budget.
    /// </summary>
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(90);

    public async Task<AssistantUnavailable?> GetAvailabilityAsync(CancellationToken ct) =>
        Availability(await ResolveAsync(ct));

    public async Task<string?> GetModelAsync(CancellationToken ct) =>
        (await ResolveAsync(ct)).Models.Primary;

    public Task<AgentChatResponse> CompleteAsync(AgentChatRequest request, CancellationToken ct) =>
        CompleteAsync(request, onTextDelta: null, ct);

    /// <summary>
    /// With a callback the answer is streamed and the callback gets each piece of text as it is
    /// written; without one it is the single round trip it always was. Either way the returned
    /// response is the complete one. A callback that throws is not a provider failure, so its
    /// exception is the caller's to see.
    /// </summary>
    public async Task<AgentChatResponse> CompleteAsync(
        AgentChatRequest request, Func<string, Task>? onTextDelta, CancellationToken ct)
    {
        var target = await ResolveAsync(ct);

        // Asked again here, not just by whoever offers the feature: a check that only guards the UI
        // is not a check (AssistantClient says the same).
        if (Availability(target) is { } unavailable)
            return AgentChatResponse.Failed(unavailable.Reason, unavailable.ReasonFa);

        var (url, urlProblem) = BuildUrl(target.Config.BaseUrl);
        if (url is null) return urlProblem!;

        var (body, bodyProblem) = BuildBody(request, target.Models);
        if (body is null) return bodyProblem!;

        return onTextDelta is null
            ? await SendWholeAsync(url, target, body, ct)
            : await StreamAsync(url, target, body, onTextDelta, ct);
    }

    /// <summary>The non-streaming round trip: one request, one JSON body.</summary>
    private async Task<AgentChatResponse> SendWholeAsync(Uri url, Target target, JsonObject body, CancellationToken ct)
    {
        using var message = NewMessage(url, target, body);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(RequestTimeout);

        try
        {
            using var http = httpClientFactory.CreateClient(HttpClientName);
            using var response = await http.SendAsync(message, budget.Token);
            var text = await response.Content.ReadAsStringAsync(budget.Token);
            return Interpret(response.StatusCode, text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own budget, not the caller giving up. (HttpClient's own timeout lands here too.)
            return TimedOut();
        }
        catch (HttpRequestException ex)
        {
            return Unreachable(ex);
        }
    }

    /// <summary>The POST both paths send: JSON body, the key, and OpenRouter's two headers for OpenRouter.</summary>
    private static HttpRequestMessage NewMessage(Uri url, Target target, JsonObject body)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };

        // A local endpoint may have no key at all — see AssistantAvailability.
        if (!string.IsNullOrWhiteSpace(target.Config.ApiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Config.ApiKey);

        if (IsOpenRouter(url))
        {
            message.Headers.TryAddWithoutValidation("HTTP-Referer", OpenRouterReferer);
            message.Headers.TryAddWithoutValidation("X-Title", OpenRouterTitle);
        }

        return message;
    }

    private AgentChatResponse TimedOut()
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(RequestTimeout.TotalSeconds));
        return AgentChatResponse.Failed(
            $"The AI provider did not answer within {seconds} seconds.",
            $"ارائه‌دهندهٔ هوش مصنوعی ظرف {seconds} ثانیه پاسخ نداد.");
    }

    /// <summary>
    /// The message, not the stack: this goes onto a page, and "connection refused" or a DNS failure
    /// is the useful part.
    /// </summary>
    private static AgentChatResponse Unreachable(Exception ex) =>
        AgentChatResponse.Failed(
            $"The AI provider could not be reached: {ex.Message}",
            $"اتصال به ارائه‌دهندهٔ هوش مصنوعی برقرار نشد: {ex.Message}");

    // ---- configuration ---------------------------------------------------------------------

    private sealed record Target(AssistantConfig Config, AgentModelPlan Models);

    private async Task<Target> ResolveAsync(CancellationToken ct)
    {
        var config = await assistant.GetConfigAsync(ct);
        var settings = await agentSettings.GetAsync(ct);
        return new Target(config, AgentSettingsService.Resolve(settings.Model, config.Model, settings.FallbackModels));
    }

    /// <summary>
    /// The deployment explainer's availability rules, with the model being the agent's, plus the two
    /// things that only matter here. Anthropic is refused first (once the feature is on): fixing the
    /// key or the model cannot help while the provider is one this client does not speak to.
    /// </summary>
    private static AssistantUnavailable? Availability(Target target)
    {
        var config = target.Config;

        if (config.Enabled
            && string.Equals(config.Provider?.Trim(), AssistantProviders.Anthropic, StringComparison.OrdinalIgnoreCase))
            return new AssistantUnavailable(
                "The agent needs an OpenAI-compatible provider such as OpenRouter; Anthropic's own API is not supported for it.",
                "عامل به ارائه‌دهنده‌ای سازگار با OpenAI مانند OpenRouter نیاز دارد؛ API خود Anthropic برای آن پشتیبانی نمی‌شود.");

        // A placeholder when nothing resolves, so the explainer's other reasons (off, no provider,
        // no key) are still reported first and the missing model is only reported when it is the
        // last thing wrong.
        var other = AssistantAvailability.Check(config with { Model = target.Models.Primary ?? "-" });
        if (other is not null) return other;

        return target.Models.Primary is null
            ? new AssistantUnavailable(
                "No agent model is configured. Set one in the AI assistant settings, or set the assistant's model.",
                "مدل عامل تنظیم نشده است. آن را در تنظیمات دستیار هوش مصنوعی وارد کنید یا مدل دستیار را تنظیم کنید.")
            : null;
    }

    /// <summary>
    /// Base + <c>/v1/chat/completions</c>, as the explainer does — except that a base that already
    /// ends in <c>/v1</c> (OpenRouter's documented <c>https://openrouter.ai/api/v1</c>, pasted as is)
    /// is not given a second one.
    /// </summary>
    private static (Uri? Url, AgentChatResponse? Problem) BuildUrl(string? configuredBase)
    {
        var baseUrl = string.IsNullOrWhiteSpace(configuredBase) ? DefaultBaseUrl : configuredBase.Trim();
        baseUrl = baseUrl.TrimEnd('/');

        var path = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? "/chat/completions"
            : "/v1/chat/completions";

        if (Uri.TryCreate(baseUrl + path, UriKind.Absolute, out var url)
            && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp))
            return (url, null);

        return (null, AgentChatResponse.Failed(
            $"The base URL \"{configuredBase}\" is not a valid http(s) address.",
            $"آدرس سرویس «{configuredBase}» یک نشانی http(s) معتبر نیست."));
    }

    private static bool IsOpenRouter(Uri url) =>
        url.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase)
        || url.Host.EndsWith(".openrouter.ai", StringComparison.OrdinalIgnoreCase);

    // ---- request ---------------------------------------------------------------------------

    private static (JsonObject? Body, AgentChatResponse? Problem) BuildBody(AgentChatRequest request, AgentModelPlan models)
    {
        var messages = new JsonArray();
        foreach (var m in request.Messages) messages.Add(MessageToJson(m));

        var body = new JsonObject
        {
            ["model"] = models.Primary,
            ["messages"] = messages
        };

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                JsonNode? parameters;
                try { parameters = JsonNode.Parse(tool.ParametersJsonSchema); }
                catch (JsonException) { parameters = null; }

                // A schema that is not JSON is a bug in the tool's definition. Sending the model a
                // made-up empty schema instead would hide it behind a tool that "works" with no
                // arguments, so it fails the call, loudly, naming the tool.
                if (parameters is not JsonObject)
                    return (null, AgentChatResponse.Failed(
                        $"The parameters schema of tool \"{tool.Name}\" is not a JSON object.",
                        $"طرح پارامترهای ابزار «{tool.Name}» یک شیء JSON نیست."));

                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = parameters
                    }
                });
            }

            body["tools"] = tools;
            body["tool_choice"] = "auto";
        }

        body["max_tokens"] = request.MaxOutputTokens;

        // OpenRouter's own fallback list. Only when there is something to fall back to: another
        // OpenAI-compatible endpoint may reject a field it does not know.
        if (models.Fallbacks.Count > 0)
        {
            var list = new JsonArray { models.Primary };
            foreach (var fallback in models.Fallbacks) list.Add(fallback);
            body["models"] = list;
        }

        return (body, null);
    }

    private static JsonObject MessageToJson(AgentChatMessage m)
    {
        switch (m.Role)
        {
            case AgentRole.System:
                return new JsonObject { ["role"] = "system", ["content"] = m.Content ?? "" };

            case AgentRole.User:
                return new JsonObject { ["role"] = "user", ["content"] = m.Content ?? "" };

            case AgentRole.Tool:
                return new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = m.ToolCallId ?? "",
                    ["content"] = m.Content ?? ""
                };

            default:
                var assistantMessage = new JsonObject { ["role"] = "assistant" };
                var hasCalls = m.ToolCalls is { Count: > 0 };

                // OpenAI's own shape for a turn that only called tools: content null, not "".
                assistantMessage["content"] = string.IsNullOrEmpty(m.Content) && hasCalls ? null : m.Content ?? "";

                if (hasCalls)
                {
                    var calls = new JsonArray();
                    foreach (var call in m.ToolCalls!)
                    {
                        calls.Add(new JsonObject
                        {
                            ["id"] = call.Id,
                            ["type"] = "function",
                            // The arguments travel as the JSON *text* the model wrote, which is what
                            // the API expects back — not re-parsed into an object.
                            ["function"] = new JsonObject
                            {
                                ["name"] = call.Name,
                                ["arguments"] = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson
                            }
                        });
                    }

                    assistantMessage["tool_calls"] = calls;
                }

                return assistantMessage;
        }
    }

    // ---- response --------------------------------------------------------------------------

    private static AgentChatResponse Interpret(HttpStatusCode status, string body)
    {
        var code = (int)status;
        var isSuccess = code is >= 200 and < 300;

        JsonDocument? doc = null;
        try
        {
            try { doc = JsonDocument.Parse(body); }
            catch (JsonException) { doc = null; }

            var root = doc?.RootElement;
            var providerError = root is { ValueKind: JsonValueKind.Object } ? ErrorDetail(root.Value) : null;

            if (!isSuccess)
            {
                // Trimmed: a provider error body can be long, and it is going onto a page.
                var detail = string.IsNullOrEmpty(providerError) ? Snippet(body) : providerError;
                var suffix = detail.Length > 0 ? " " + detail : "";

                return status == HttpStatusCode.TooManyRequests
                    ? AgentChatResponse.Failed(
                        $"The AI provider is rate limiting requests (429). Wait a moment and try again, or pick a model with a higher limit.{suffix}",
                        $"ارائه‌دهندهٔ هوش مصنوعی تعداد درخواست‌ها را محدود کرده است (429). کمی صبر کنید و دوباره تلاش کنید یا مدلی با سقف بالاتر انتخاب کنید.{suffix}")
                    : AgentChatResponse.Failed(
                        $"The AI provider refused the request ({code}).{suffix}",
                        $"ارائه‌دهندهٔ هوش مصنوعی درخواست را نپذیرفت ({code}).{suffix}");
            }

            if (root is not { ValueKind: JsonValueKind.Object } json)
                return AgentChatResponse.Failed(
                    $"The AI provider's reply was not JSON. {Snippet(body)}".TrimEnd(),
                    $"پاسخ ارائه‌دهندهٔ هوش مصنوعی JSON نبود. {Snippet(body)}".TrimEnd());

            // A 200 that carries an error object: OpenRouter does this when an upstream fails after
            // it has already accepted the request.
            if (providerError is not null) return Reported(providerError);

            return ParseChoice(json);
        }
        finally
        {
            doc?.Dispose();
        }
    }

    private static AgentChatResponse ParseChoice(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            return AgentChatResponse.Failed(
                "The AI provider's reply had no choices.",
                "پاسخ ارائه‌دهندهٔ هوش مصنوعی هیچ گزینه‌ای نداشت.");

        var choice = choices[0];
        var finishReason = StringOf(choice, "finish_reason");

        if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
        {
            var choiceError = choice.ValueKind == JsonValueKind.Object ? ErrorDetail(choice) : null;
            return choiceError is not null
                ? Reported(choiceError)
                : AgentChatResponse.Failed(
                    "The AI provider's reply had no choices.",
                    "پاسخ ارائه‌دهندهٔ هوش مصنوعی هیچ گزینه‌ای نداشت.");
        }

        return Answer(ContentOf(message), ToolCallsOf(message), StringOf(root, "model"), UsageOf(root), finishReason);
    }

    /// <summary>An <c>error</c> the provider put in a reply or a stream; empty means it said nothing more.</summary>
    private static AgentChatResponse Reported(string detail)
    {
        var said = detail.Length > 0 ? detail : "(no message)";
        return AgentChatResponse.Failed(
            $"The AI provider reported an error: {said}",
            $"ارائه‌دهندهٔ هوش مصنوعی خطا گزارش کرد: {said}");
    }

    /// <summary>
    /// The answer, from its parts. The one place it is assembled — the whole reply and the streamed
    /// one both end here, so "the streamed response equals the whole one" is a property of the code
    /// and not of two copies that happen to agree.
    /// </summary>
    private static AgentChatResponse Answer(
        string? content, List<AgentToolCall> calls, string? model, AgentChatUsage? usage, string? finishReason)
    {
        // Neither words nor a call is not an answer. Handing the pane an empty assistant message
        // would read as "the agent has nothing to say", a claim nobody made — AssistantClient makes
        // the same call. A reasoning model that spent its whole budget thinking ends up here.
        if (string.IsNullOrWhiteSpace(content) && calls.Count == 0)
        {
            var why = finishReason is null ? "" : $" (finish reason: {finishReason})";
            return AgentChatResponse.Failed(
                $"The model returned neither text nor a tool call{why}.",
                $"مدل نه متنی برگرداند و نه ابزاری را فراخوانی کرد{why}.");
        }

        return new AgentChatResponse(
            Ok: true,
            Message: new AgentChatMessage(AgentRole.Assistant, string.IsNullOrWhiteSpace(content) ? null : content,
                calls.Count > 0 ? calls : null),
            Model: model,
            Usage: usage,
            FinishReason: finishReason,
            Error: null,
            ErrorFa: null);
    }

    /// <summary>Content is normally a string or null; some gateways send an array of text parts.</summary>
    private static string? ContentOf(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return null;

        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => string.Concat(content.EnumerateArray()
                .Select(part => part.ValueKind == JsonValueKind.Object ? StringOf(part, "text") : null)
                .Where(text => text is not null)),
            _ => null
        };
    }

    private static List<AgentToolCall> ToolCallsOf(JsonElement message)
    {
        var calls = new List<AgentToolCall>();
        if (!message.TryGetProperty("tool_calls", out var raw) || raw.ValueKind != JsonValueKind.Array) return calls;

        foreach (var item in raw.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var function = item.TryGetProperty("function", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;
            var name = function.ValueKind == JsonValueKind.Object ? StringOf(function, "name") ?? "" : "";

            // The call has to be answerable by id, and a few gateways leave it out. A made-up id is
            // fine: it only has to match the tool message we send back.
            var id = StringOf(item, "id");
            if (string.IsNullOrEmpty(id)) id = $"call_{calls.Count + 1}";

            calls.Add(new AgentToolCall(id, name, ArgumentsOf(function)));
        }

        return calls;
    }

    /// <summary>
    /// Normally a JSON string holding JSON. Some providers send the object itself; its raw text is
    /// what the tool layer wants either way. Absent or null is "no arguments", which is valid for a
    /// tool that takes none.
    /// </summary>
    private static string ArgumentsOf(JsonElement function)
    {
        if (function.ValueKind != JsonValueKind.Object || !function.TryGetProperty("arguments", out var args))
            return "{}";

        return args.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(args.GetString()) ? "{}" : args.GetString()!,
            JsonValueKind.Null or JsonValueKind.Undefined => "{}",
            _ => args.GetRawText()
        };
    }

    private static AgentChatUsage? UsageOf(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;

        var input = LongOf(usage, "prompt_tokens");
        var output = LongOf(usage, "completion_tokens");
        return input is null && output is null ? null : new AgentChatUsage(input ?? 0, output ?? 0);
    }

    // ---- small readers ---------------------------------------------------------------------

    private static string? StringOf(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? LongOf(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : null;

    /// <summary>
    /// The provider's own explanation, when the body has an <c>error</c>: OpenRouter's
    /// <c>{ "error": { "message", "code", "metadata": { "raw" } } }</c>, or a bare string. The
    /// upstream text in <c>metadata.raw</c> is kept because "Provider returned error" alone does not
    /// say whether the model is rate limited or gone. Null when there is no error; empty when there is
    /// one that says nothing.
    /// </summary>
    private static string? ErrorDetail(JsonElement holder)
    {
        if (!holder.TryGetProperty("error", out var error)) return null;

        switch (error.ValueKind)
        {
            case JsonValueKind.String:
                return Trim(error.GetString());

            case JsonValueKind.Object:
                var message = StringOf(error, "message");
                var raw = error.TryGetProperty("metadata", out var metadata) ? StringOf(metadata, "raw") : null;

                var parts = new[] { message, raw }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim());
                return Trim(string.Join(" — ", parts));

            default:
                return null;
        }
    }

    private static string Snippet(string body) => Trim(string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));

    private static string Trim(string? text)
    {
        text = text?.Trim() ?? "";
        return text.Length > 300 ? text[..300] + "…" : text;
    }
}
