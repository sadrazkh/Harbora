using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Harbora.Domain.Ai;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Ai;

/// <summary>
/// Talks to OpenRouter, and to anything else speaking OpenAI's chat-completions shape.
///
/// One adapter covers both because the wire format is the same; only the base URL and a couple of
/// courtesy headers differ. Keeping them together avoids a second near-identical file that drifts.
///
/// Nothing from the customer's own request headers is forwarded. The customer authenticated to
/// Harbora; forwarding their Authorization upstream would send their Harbora key to a third party,
/// and forwarding arbitrary headers is how a caller smuggles instructions into somebody else's API
/// call.
/// </summary>
public sealed class OpenRouterProviderAdapter(
    IHttpClientFactory httpClientFactory,
    ILogger<OpenRouterProviderAdapter> logger) : IAiProviderAdapter
{
    public AiProviderType Handles => AiProviderType.OpenRouter;

    /// <summary>Long enough for a considered answer; short enough to free the connection.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    public async Task<AiUpstreamResult> SendAsync(
        AiProvider provider, string token, AiModel model, string requestJson,
        string endpoint, CancellationToken ct)
    {
        var url = AiUpstreamUrl.Build(provider.BaseUrl, endpoint);
        if (url is null)
            return Failed(null, "The provider address is not usable.", null);

        try
        {
            using var http = httpClientFactory.CreateClient();
            http.Timeout = RequestTimeout;

            using var request = Build(url, token, provider, requestJson);
            using var response = await http.SendAsync(request, ct);

            var body = await response.Content.ReadAsStringAsync(ct);
            var retryAfter = response.Headers.TryGetValues("Retry-After", out var values)
                ? values.FirstOrDefault()
                : null;

            if (!response.IsSuccessStatusCode)
                return new AiUpstreamResult(false, (int)response.StatusCode, body, 0, 0, 0, retryAfter, null);

            var (input, output, cached) = AiUsageParser.Read(body);
            return new AiUpstreamResult(true, (int)response.StatusCode, body, input, output, cached, null, null);
        }
        catch (Exception ex)
        {
            // Returned as data rather than thrown: the router has to decide what to do about it,
            // and it cannot do that from inside a stack unwind.
            logger.LogWarning(ex, "Upstream request to {Provider} failed.", provider.Name);
            return Failed(null, ex.Message, ex);
        }
    }

    public async IAsyncEnumerable<AiStreamChunk> StreamAsync(
        AiProvider provider, string token, AiModel model, string requestJson,
        string endpoint, [EnumeratorCancellation] CancellationToken ct)
    {
        var url = AiUpstreamUrl.Build(provider.BaseUrl, endpoint);
        if (url is null) yield break;

        using var http = httpClientFactory.CreateClient();
        http.Timeout = RequestTimeout;

        using var request = Build(url, token, provider, requestJson);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            yield return new AiStreamChunk(error, IsFinal: true);
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Read until null rather than testing EndOfStream: that property blocks, which in an
            // async streaming path stalls the thread the response is being written on.
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (line.Length == 0) continue;

            const string prefix = "data: ";
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var data = line[prefix.Length..];

            // The sentinel every OpenAI-shaped stream ends with. Marked rather than swallowed, so
            // the caller knows the stream finished properly and can settle usage.
            if (data == "[DONE]")
            {
                yield return new AiStreamChunk(data, IsFinal: true);
                yield break;
            }

            yield return new AiStreamChunk(data, IsFinal: false);
        }
    }

    private static HttpRequestMessage Build(string url, string token, AiProvider provider, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Only headers the provider documents, and only from configuration an administrator set.
        // Nothing derived from the customer's request reaches this point.
        if (!string.IsNullOrWhiteSpace(provider.ExtraHeadersJson))
        {
            try
            {
                var extra = JsonSerializer.Deserialize<Dictionary<string, string>>(provider.ExtraHeadersJson);
                foreach (var (key, value) in extra ?? [])
                {
                    // Never let configuration overwrite the credential we just set.
                    if (key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) continue;
                    request.Headers.TryAddWithoutValidation(key, value);
                }
            }
            catch (JsonException)
            {
                // Malformed configuration must not stop a request that would otherwise work.
            }
        }

        return request;
    }

    private static AiUpstreamResult Failed(int? status, string? body, Exception? ex) =>
        new(false, status, body, 0, 0, 0, null, ex);
}
