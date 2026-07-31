using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Harbora.Infrastructure.Assistant;

/// <summary>What came back, or why nothing did.</summary>
public sealed record AssistantAnswer(bool Ok, string Text);

/// <summary>
/// Speaks to whichever model the administrator configured.
///
/// Two request shapes cover the field: Anthropic's Messages API, and the OpenAI chat-completions
/// shape that everything else — gateways, and a local Ollama — also speaks. Neither is preferred by
/// this code; the choice is a setting, and no key means no call.
///
/// Failures come back as text rather than exceptions. An assistant is a convenience: when it is
/// unreachable the page must still render the deployment somebody actually came to look at.
/// </summary>
public sealed class AssistantClient(IHttpClientFactory httpClientFactory)
{
    /// <summary>Long enough for a considered answer, short enough not to hold a request open.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public async Task<AssistantAnswer> AskAsync(AssistantConfig config, AssistantAsk ask, CancellationToken ct)
    {
        // Asked again here, not just at the button. The two are different callers, and a check that
        // only guards the UI is not a check.
        if (AssistantAvailability.Check(config) is { } unavailable)
            return new AssistantAnswer(false, unavailable.Reason);

        var provider = config.Provider!.Trim().ToLowerInvariant();

        try
        {
            using var http = httpClientFactory.CreateClient();
            http.Timeout = Timeout;

            return provider == AssistantProviders.Anthropic
                ? await AskAnthropicAsync(http, config, ask, ct)
                : await AskOpenAiAsync(http, config, ask, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AssistantAnswer(false, "The AI provider did not answer in time.");
        }
        catch (Exception ex)
        {
            // The message, not the stack: this is rendered on a page, and the provider's own error
            // text is the useful part ("model not found", "insufficient credit").
            return new AssistantAnswer(false, $"The AI provider could not be reached: {ex.Message}");
        }
    }

    private static async Task<AssistantAnswer> AskAnthropicAsync(
        HttpClient http, AssistantConfig config, AssistantAsk ask, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(config.BaseUrl) ? "https://api.anthropic.com" : config.BaseUrl.Trim();

        using var request = new HttpRequestMessage(HttpMethod.Post, Combine(baseUrl, "/v1/messages"))
        {
            Content = JsonContent.Create(new
            {
                model = config.Model,
                max_tokens = 1024,
                system = ask.SystemPrompt,
                messages = new[] { new { role = "user", content = ask.UserPrompt } }
            })
        };
        request.Headers.TryAddWithoutValidation("x-api-key", config.ApiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) return Failed(response.StatusCode, body);

        // { content: [ { type: "text", text: "…" } ] }
        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement.TryGetProperty("content", out var content)
            ? string.Concat(content.EnumerateArray()
                .Where(part => part.TryGetProperty("text", out _))
                .Select(part => part.GetProperty("text").GetString()))
            : null;

        return Answer(text);
    }

    private static async Task<AssistantAnswer> AskOpenAiAsync(
        HttpClient http, AssistantConfig config, AssistantAsk ask, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(config.BaseUrl) ? "https://api.openai.com" : config.BaseUrl.Trim();

        using var request = new HttpRequestMessage(HttpMethod.Post, Combine(baseUrl, "/v1/chat/completions"))
        {
            Content = JsonContent.Create(new
            {
                model = config.Model,
                messages = new[]
                {
                    new { role = "system", content = ask.SystemPrompt },
                    new { role = "user", content = ask.UserPrompt }
                }
            })
        };

        // A local endpoint may have no key at all — see AssistantAvailability.
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) return Failed(response.StatusCode, body);

        // { choices: [ { message: { content: "…" } } ] }
        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
            ? choices[0].GetProperty("message").GetProperty("content").GetString()
            : null;

        return Answer(text);
    }

    /// <summary>
    /// An empty answer is a failure, not an answer. Rendering a blank panel reads as "the assistant
    /// has nothing to say about this", which is a claim nobody made.
    /// </summary>
    private static AssistantAnswer Answer(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? new AssistantAnswer(false, "The AI provider returned an empty answer.")
            : new AssistantAnswer(true, text.Trim());

    private static AssistantAnswer Failed(System.Net.HttpStatusCode status, string body)
    {
        // Trimmed: a provider error body can be long, and it is going onto a page.
        var detail = body.Length > 400 ? body[..400] + "…" : body;
        return new AssistantAnswer(false, $"The AI provider refused the request ({(int)status}). {detail}");
    }

    private static string Combine(string baseUrl, string path) =>
        baseUrl.TrimEnd('/') + path;
}
