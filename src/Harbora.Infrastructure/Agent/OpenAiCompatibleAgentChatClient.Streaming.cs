using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Harbora.Infrastructure.Agent;

/// <summary>
/// The streamed round trip: the same request with <c>stream: true</c>, the answer read as it is
/// written. What comes back is the response the whole-reply call would have produced for the same
/// answer — both end in <c>Answer(...)</c> — and the text pieces are only a running display of it.
/// </summary>
public sealed partial class OpenAiCompatibleAgentChatClient
{
    /// <summary>
    /// A stream that sends nothing, not even a keep-alive comment, for this long has stopped. The
    /// overall <see cref="RequestTimeout"/> still bounds the whole call: a stream that dribbles
    /// comments for ever does not get to stay open.
    /// </summary>
    internal TimeSpan StreamIdleTimeout { get; init; } = TimeSpan.FromSeconds(45);

    private async Task<AgentChatResponse> StreamAsync(
        Uri url, Target target, JsonObject body, Func<string, Task> onTextDelta, CancellationToken ct)
    {
        // Added to a copy: the whole-reply body (the fallback below) must not carry them.
        var streamBody = (JsonObject)body.DeepClone();
        streamBody["stream"] = true;
        // OpenRouter always sends usage in the last chunk; OpenAI only on request.
        streamBody["stream_options"] = new JsonObject { ["include_usage"] = true };

        var streamed = await TryStreamAsync(url, target, streamBody, onTextDelta, ct);
        if (streamed is not null) return streamed;

        // The provider said it will not stream. Once, without — the answer is the same either way,
        // and the person still gets the text, in one piece.
        var whole = await SendWholeAsync(url, target, body, ct);
        if (whole.Ok && !string.IsNullOrEmpty(whole.Message?.Content))
            await onTextDelta(whole.Message.Content);
        return whole;
    }

    /// <summary>The response, or null when the provider refused streaming and the caller should fall back.</summary>
    private async Task<AgentChatResponse?> TryStreamAsync(
        Uri url, Target target, JsonObject streamBody, Func<string, Task> onTextDelta, CancellationToken ct)
    {
        using var message = NewMessage(url, target, streamBody);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(RequestTimeout);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);

        var answer = new StreamedAnswer();
        var started = false;

        try
        {
            using var http = httpClientFactory.CreateClient(HttpClientName);

            // Headers only: the body is read as it arrives, not buffered until the end.
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, budget.Token);

            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(budget.Token);
                return RefusesStreaming(response.StatusCode, text) ? null : Interpret(response.StatusCode, text);
            }

            // A provider that ignores `stream` and answers with one JSON body is still answering.
            if (string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                var text = await response.Content.ReadAsStringAsync(budget.Token);
                var whole = Interpret(response.StatusCode, text);
                if (whole.Ok && !string.IsNullOrEmpty(whole.Message?.Content))
                    await onTextDelta(whole.Message.Content);
                return whole;
            }

            started = true;
            await using var stream = await response.Content.ReadAsStreamAsync(budget.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await ReadStreamAsync(reader, answer, onTextDelta, idle);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own overall budget or the silence limit, never the caller leaving. Once the model
            // has said it is finished the answer is whole; only the usage line can be missing, and
            // a finished answer is not thrown away for that.
            if (answer.IsFinished) return answer.Build();
            return budget.IsCancellationRequested ? TimedOut() : WentSilent();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            if (answer.IsFinished) return answer.Build();
            return started ? Dropped(ex) : Unreachable(ex);
        }
    }

    /// <summary>
    /// Server-sent events: lines, grouped into events by a blank line. <c>data:</c> lines carry the
    /// chunks; lines starting with a colon are comments (OpenRouter's <c>: OPENROUTER PROCESSING</c>
    /// keep-alives) and every other field name is not ours to read.
    /// </summary>
    private async Task<AgentChatResponse> ReadStreamAsync(
        StreamReader reader, StreamedAnswer answer, Func<string, Task> onTextDelta, CancellationTokenSource idle)
    {
        var pending = new StringBuilder();
        var hasPending = false;

        while (true)
        {
            // Armed again before every read, and by any line at all — a comment is a sign of life.
            idle.CancelAfter(StreamIdleTimeout);
            var line = await reader.ReadLineAsync(idle.Token);

            if (line is null || line.Length == 0)
            {
                if (hasPending)
                {
                    await answer.AddAsync(pending.ToString(), onTextDelta);
                    pending.Clear();
                    hasPending = false;
                }

                if (answer.Failure is not null) return answer.Failure;

                // [DONE] ends it; so does a finished answer whose usage has arrived (nothing else of
                // value follows it, and some providers keep the connection open after the last chunk).
                if (line is null || answer.Done || (answer.IsFinished && answer.HasUsage)) break;
                continue;
            }

            if (line[0] == ':') continue;

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line.AsSpan(5);
                if (value.Length > 0 && value[0] == ' ') value = value[1..];

                if (hasPending) pending.Append('\n');
                pending.Append(value);
                hasPending = true;
            }
        }

        // Neither the end marker nor a finish reason: the connection closed in the middle of the
        // answer. Whatever text arrived is not the answer, and presenting it as one would be the
        // defect this codebase keeps having to remove.
        if (!answer.Done && !answer.IsFinished)
            return AgentChatResponse.Failed(
                "The AI provider's answer was cut off before it finished.",
                "پاسخ ارائه‌دهندهٔ هوش مصنوعی پیش از پایان قطع شد.");

        return answer.Build();
    }

    /// <summary>
    /// A 4xx that says it is about streaming: the model or the gateway does not do it. Not an
    /// authentication or rate-limit refusal — those are about the person, and the same request without
    /// <c>stream</c> would be refused the same way.
    /// </summary>
    private static bool RefusesStreaming(HttpStatusCode status, string body)
    {
        var code = (int)status;
        if (code is < 400 or >= 500 or 401 or 403 or 408 or 429) return false;
        return body.Contains("stream", StringComparison.OrdinalIgnoreCase);
    }

    private AgentChatResponse WentSilent()
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(StreamIdleTimeout.TotalSeconds));
        return AgentChatResponse.Failed(
            $"The AI provider stopped sending the answer for {seconds} seconds.",
            $"ارائه‌دهندهٔ هوش مصنوعی {seconds} ثانیه است که پاسخ را ادامه نمی‌دهد.");
    }

    private static AgentChatResponse Dropped(Exception ex) =>
        AgentChatResponse.Failed(
            $"The AI provider's connection dropped before the answer finished: {ex.Message}",
            $"اتصال ارائه‌دهندهٔ هوش مصنوعی پیش از پایان پاسخ قطع شد: {ex.Message}");

    /// <summary>
    /// The answer as the chunks build it up: text, tool calls by index, and the last model, usage and
    /// finish reason seen. <see cref="Build"/> hands it to the same <c>Answer(...)</c> the whole-reply
    /// path uses.
    /// </summary>
    private sealed class StreamedAnswer
    {
        private readonly StringBuilder _text = new();
        private readonly SortedDictionary<int, PendingCall> _calls = new();
        private string? _model;
        private AgentChatUsage? _usage;

        public string? FinishReason { get; private set; }
        public bool Done { get; private set; }
        public AgentChatResponse? Failure { get; private set; }

        public bool IsFinished => FinishReason is not null;
        public bool HasUsage => _usage is not null;

        private sealed class PendingCall
        {
            public string Id = "";
            public string Name = "";
            public readonly StringBuilder Arguments = new();
        }

        public async Task AddAsync(string payload, Func<string, Task> onTextDelta)
        {
            payload = payload.Trim();
            if (payload == "[DONE]")
            {
                Done = true;
                return;
            }

            // A payload that is not JSON is not a chunk. If it was part of the answer, the end of the
            // stream (no finish reason) says so; guessing here would invent text.
            JsonDocument doc;
            try { doc = JsonDocument.Parse(payload); }
            catch (JsonException) { return; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;

                // OpenRouter reports a failure after the stream has begun as a chunk with an error.
                if (ErrorDetail(root) is { } error)
                {
                    Failure = Reported(error);
                    return;
                }

                if (StringOf(root, "model") is { Length: > 0 } model) _model = model;
                if (UsageOf(root) is { } usage) _usage = usage;

                if (!root.TryGetProperty("choices", out var choices)
                    || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0
                    || choices[0].ValueKind != JsonValueKind.Object)
                    return; // the usage chunk has an empty choices array

                var choice = choices[0];

                if (ErrorDetail(choice) is { } choiceError)
                {
                    Failure = Reported(choiceError);
                    return;
                }

                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    var piece = ContentOf(delta);
                    if (!string.IsNullOrEmpty(piece))
                    {
                        _text.Append(piece);
                        await onTextDelta(piece);
                    }

                    if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                        MergeToolCalls(toolCalls);
                }

                if (StringOf(choice, "finish_reason") is { } finish)
                {
                    FinishReason = finish;

                    // OpenRouter's "the upstream failed" marker, when it did not say why.
                    if (finish == "error")
                        Failure = AgentChatResponse.Failed(
                            "The AI provider stopped the answer with an error (finish reason: error).",
                            "ارائه‌دهندهٔ هوش مصنوعی پاسخ را با خطا متوقف کرد (finish reason: error).");
                }
            }
        }

        /// <summary>
        /// Tool-call fragments arrive keyed by <c>index</c>: the id and name once, the arguments in
        /// pieces. A gateway that numbers every parallel call 0 is told apart by id; one that sends no
        /// index at all, by id or by being the call that was last extended.
        /// </summary>
        private void MergeToolCalls(JsonElement array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var id = StringOf(item, "id");
                var function = item.TryGetProperty("function", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;

                var index = IndexOf(item, id);
                if (!_calls.TryGetValue(index, out var call)) _calls[index] = call = new PendingCall();

                if (!string.IsNullOrEmpty(id) && call.Id.Length == 0) call.Id = id;

                // The name is sent once; a provider that repeats it on every chunk must not double it.
                var name = StringOf(function, "name");
                if (!string.IsNullOrEmpty(name) && !string.Equals(name, call.Name, StringComparison.Ordinal))
                    call.Name += name;

                if (function.ValueKind == JsonValueKind.Object && function.TryGetProperty("arguments", out var args))
                {
                    switch (args.ValueKind)
                    {
                        case JsonValueKind.String:
                            call.Arguments.Append(args.GetString());
                            break;
                        case JsonValueKind.Null or JsonValueKind.Undefined:
                            break;
                        default: // an object sent whole, as some providers do
                            call.Arguments.Append(args.GetRawText());
                            break;
                    }
                }
            }
        }

        private int IndexOf(JsonElement item, string? id)
        {
            if (item.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out var n))
            {
                var differentCall = _calls.TryGetValue(n, out var existing)
                    && !string.IsNullOrEmpty(id) && existing.Id.Length > 0 && existing.Id != id;
                return differentCall ? NextIndex() : n;
            }

            if (!string.IsNullOrEmpty(id))
            {
                foreach (var (key, call) in _calls)
                    if (call.Id == id) return key;
                return NextIndex();
            }

            return _calls.Count == 0 ? 0 : _calls.Keys.Max();
        }

        private int NextIndex() => _calls.Count == 0 ? 0 : _calls.Keys.Max() + 1;

        public AgentChatResponse Build()
        {
            var calls = new List<AgentToolCall>();
            foreach (var call in _calls.Values)
            {
                // The same fallbacks the whole-reply path applies: an id so the call can be answered,
                // and {} for a tool that takes no arguments.
                var arguments = call.Arguments.ToString();
                calls.Add(new AgentToolCall(
                    call.Id.Length > 0 ? call.Id : $"call_{calls.Count + 1}",
                    call.Name,
                    string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments));
            }

            return Answer(_text.Length > 0 ? _text.ToString() : null, calls, _model, _usage, FinishReason);
        }
    }
}
