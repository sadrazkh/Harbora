using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Harbora.Tests.Fakes;

/// <summary>
/// Stands in for a chat-completions endpoint at the HTTP layer, so the REAL
/// <c>OpenAiCompatibleAgentChatClient</c> — its URL, its headers, its JSON in and out — runs against a
/// scripted provider. Nothing here, and nothing that uses it, ever opens a socket.
///
/// <para>
/// It records every request it saw, body included, so a test can assert on what was sent (and, for a
/// request that must never be made, that nothing was).
/// </para>
/// </summary>
public sealed class RecordingChatHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    /// <summary>What one request looked like when it arrived.</summary>
    public sealed record Seen(
        HttpMethod Method,
        Uri Url,
        IReadOnlyDictionary<string, string> Headers,
        AuthenticationHeaderValue? Authorization,
        string Body)
    {
        /// <summary>The body parsed, for asserting on fields rather than on text.</summary>
        public JsonObject Json => JsonNode.Parse(Body)!.AsObject();
    }

    private readonly object _gate = new();
    private readonly List<Seen> _requests = [];

    public IReadOnlyList<Seen> Requests
    {
        get { lock (_gate) return _requests.ToList(); }
    }

    public Seen Only
    {
        get
        {
            lock (_gate)
            {
                if (_requests.Count != 1) throw new InvalidOperationException($"Expected exactly one request, saw {_requests.Count}.");
                return _requests[0];
            }
        }
    }

    public RecordingChatHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this((request, _) => Task.FromResult(respond(request)))
    {
    }

    /// <summary>Answers every request with the same status and JSON text.</summary>
    public static RecordingChatHandler Always(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => Reply(body, status));

    public static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>A well-formed assistant reply carrying text.</summary>
    public static string TextReply(string text, string model = "vendor/model:free") =>
        new JsonObject
        {
            ["id"] = "gen-1",
            ["model"] = model,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["finish_reason"] = "stop",
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text }
            }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 12, ["completion_tokens"] = 5, ["total_tokens"] = 17 }
        }.ToJsonString();

    /// <summary>
    /// A well-formed assistant reply that only calls one tool (<c>content</c> is null). The arguments
    /// go out as a JSON string holding JSON, the way OpenAI sends them.
    /// </summary>
    public static string ToolCallReply(string name, string argumentsJson, string id = "call_1", string model = "vendor/model:free") =>
        new JsonObject
        {
            ["id"] = "gen-2",
            ["model"] = model,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["finish_reason"] = "tool_calls",
                ["message"] = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = null,
                    ["tool_calls"] = new JsonArray(new JsonObject
                    {
                        ["id"] = id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = name, ["arguments"] = argumentsJson }
                    })
                }
            }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 40, ["completion_tokens"] = 9 }
        }.ToJsonString();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers) headers[header.Key] = string.Join(",", header.Value);

        lock (_gate)
            _requests.Add(new Seen(request.Method, request.RequestUri!, headers, request.Headers.Authorization, body));

        return await respond(request, ct);
    }

    /// <summary>Hands out the same handler for every client name, and remembers which names were asked for.</summary>
    public sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly List<string> _names = [];

        public IReadOnlyList<string> Names
        {
            get { lock (_names) return _names.ToList(); }
        }

        public HttpClient CreateClient(string name)
        {
            lock (_names) _names.Add(name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }
}
