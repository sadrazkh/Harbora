using System.Diagnostics;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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

    /// <summary>
    /// "stream" as a word, not as the tail of "upstream" or "downstream" — gateway errors say
    /// "Upstream provider returned error" all the time, and that is not a refusal to stream.
    /// </summary>
    private static readonly Regex StreamWord = new(
        @"(?<![a-z_])stream", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private async Task<AgentChatResponse> StreamAsync(
        Uri url, Target target, JsonObject body, Func<string, Task> onTextDelta, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();

        // Added to a copy: the whole-reply body (the fallback below) must not carry them.
        var streamBody = (JsonObject)body.DeepClone();
        streamBody["stream"] = true;
        // OpenRouter always sends usage in the last chunk; OpenAI only on request.
        streamBody["stream_options"] = new JsonObject { ["include_usage"] = true };

        var streamed = await TryStreamAsync(url, target, streamBody, onTextDelta, ct);
        if (streamed is not null) return streamed;

        // The provider said it will not stream. Once, without — the answer is the same either way,
        // and the person still gets the text, in one piece. It gets what is left of the one overall
        // budget the call has, not a second one: the refusal already spent some of it.
        var left = RequestTimeout - Stopwatch.GetElapsedTime(started);
        if (left <= TimeSpan.Zero) return TimedOut();

        var whole = await SendWholeAsync(url, target, body, ct, left);
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
                    await Deliver(onTextDelta, whole.Message.Content);
                return whole;
            }

            started = true;
            await using var stream = await response.Content.ReadAsStreamAsync(budget.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await ReadStreamAsync(reader, answer, onTextDelta, budget.Token);
        }
        catch (CallbackFault fault)
        {
            // The caller's own callback failed — most often because the browser it was writing to
            // left. That is not something the provider did, and none of the handlers below may turn
            // it into a timeout or a dropped connection. It goes back as the exception it was.
            ExceptionDispatchInfo.Capture(fault.InnerException!).Throw();
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our overall budget (HttpClient's own timeout lands here too). The silence limit is
            // judged where the read happens; anything else cancelled is a timeout too, not a silence.
            // Once the model has said it is finished the answer is whole; only the usage line can be
            // missing, and a finished answer is not thrown away for that.
            return answer.IsFinished ? answer.Build() : TimedOut();
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
        StreamReader reader, StreamedAnswer answer, Func<string, Task> onTextDelta, CancellationToken budget)
    {
        var pending = new StringBuilder();
        var hasPending = false;

        while (true)
        {
            var (line, silent) = await NextLineAsync(reader, budget);
            if (silent) return answer.IsFinished ? answer.Build() : WentSilent();

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
    /// One line, or the news that the stream has been silent for <see cref="StreamIdleTimeout"/>. The
    /// timer exists only around the read: it is armed here and gone when the line has arrived, so the
    /// time the caller's callback takes to show it is never counted as the provider's silence. Any line
    /// at all, a comment included, is a sign of life.
    /// </summary>
    private async Task<(string? Line, bool Silent)> NextLineAsync(StreamReader reader, CancellationToken budget)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(budget);
        idle.CancelAfter(StreamIdleTimeout);

        try
        {
            return (await reader.ReadLineAsync(idle.Token), false);
        }
        catch (OperationCanceledException) when (idle.IsCancellationRequested && !budget.IsCancellationRequested)
        {
            // Our timer and nothing else: not the overall budget, not the caller.
            return (null, true);
        }
    }

    /// <summary>
    /// A 4xx that says it is about streaming: the model or the gateway does not do it. Not an
    /// authentication or rate-limit refusal — those are about the person, and the same request without
    /// <c>stream</c> would be refused the same way. Judged on what the provider's <em>error</em> says,
    /// as a word: a body that merely quotes the request (<c>"stream":true</c>) or an "Upstream provider
    /// returned error" is not a refusal to stream.
    /// </summary>
    private static bool RefusesStreaming(HttpStatusCode status, string body)
    {
        var code = (int)status;
        if (code is < 400 or >= 500 or 401 or 403 or 408 or 429) return false;

        string? said;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            // OpenAI's { error: { message } }, or the bare { message } / { detail } some servers send.
            said = root.ValueKind == JsonValueKind.Object
                ? ErrorDetail(root) ?? StringOf(root, "message") ?? StringOf(root, "detail")
                : null;
        }
        catch (JsonException)
        {
            // A plain-text refusal is its own explanation.
            said = body;
        }

        return said is not null && StreamWord.IsMatch(said);
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
    /// A fault in the caller's <c>onTextDelta</c>, carried through this client's own handlers
    /// untouched. Its type is private on purpose: nothing a handler filters on can match it.
    /// </summary>
    private sealed class CallbackFault(Exception inner) : Exception(inner.Message, inner);

    /// <summary>Hands one piece of text to the caller's callback, marking anything it throws as the caller's.</summary>
    private static async Task Deliver(Func<string, Task> onTextDelta, string text)
    {
        try
        {
            await onTextDelta(text);
        }
        catch (Exception ex)
        {
            throw new CallbackFault(ex);
        }
    }

    /// <summary>
    /// The answer as the chunks build it up: text, tool calls, and the last model, usage and finish
    /// reason seen. <see cref="Build"/> hands it to the same <c>Answer(...)</c> the whole-reply path uses.
    /// </summary>
    private sealed class StreamedAnswer
    {
        private readonly StringBuilder _text = new();
        private readonly SortedDictionary<int, PendingCall> _calls = new();       // by creation order
        private readonly Dictionary<int, int> _newestForWireIndex = new();        // wire index -> key in _calls
        private int _created;
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
                        await Deliver(onTextDelta, piece);
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
        /// pieces. A gateway that numbers every parallel call 0 is told apart by id, and the fragments
        /// that follow a new id (which carry none) belong to the newest call under that index. One that
        /// sends no index at all is followed by id, or by being the call that was last extended.
        /// </summary>
        private void MergeToolCalls(JsonElement array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var id = StringOf(item, "id");
                var function = item.TryGetProperty("function", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;

                var call = _calls[KeyFor(item, id)];

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

        /// <summary>Which pending call this fragment belongs to, creating it when it is the first of its call.</summary>
        private int KeyFor(JsonElement item, string? id)
        {
            if (item.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out var wire))
            {
                if (!_newestForWireIndex.TryGetValue(wire, out var key)) return Create(wire);

                // A different id under the same wire index is a different call — the one it names if
                // that is already known, otherwise a new one.
                var existing = _calls[key];
                if (!string.IsNullOrEmpty(id) && existing.Id.Length > 0 && existing.Id != id)
                    return KeyOfId(id) ?? Create(wire);

                return key;
            }

            if (!string.IsNullOrEmpty(id)) return KeyOfId(id) ?? Create(null);

            return _calls.Count == 0 ? Create(null) : _calls.Keys.Max();
        }

        private int? KeyOfId(string id)
        {
            foreach (var (key, call) in _calls)
                if (call.Id == id) return key;
            return null;
        }

        private int Create(int? wireIndex)
        {
            var key = _created++;
            _calls[key] = new PendingCall();
            if (wireIndex is { } wire) _newestForWireIndex[wire] = key;
            return key;
        }

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
