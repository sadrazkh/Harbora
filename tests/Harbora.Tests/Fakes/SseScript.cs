using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Harbora.Tests.Fakes;

/// <summary>
/// A server-sent-events response whose body arrives the way a real one does: in pieces, at the
/// provider's pace, cut wherever the network cuts it — mid-line, mid-event, even in the middle of a
/// multi-byte character — and sometimes never finishing. The REAL client reads it through a real
/// <c>HttpClient</c>, so what is exercised is its incremental parser and its timeouts, not a string.
///
/// <para>
/// Build one per request (a body can be read once), usually inside the handler's responder.
/// </para>
/// </summary>
public sealed class SseScript
{
    private readonly List<Func<CancellationToken, Task<byte[]?>>> _steps = [];

    /// <summary>One network read carrying <paramref name="text"/>.</summary>
    public SseScript Send(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _steps.Add(_ => Task.FromResult<byte[]?>(bytes));
        return this;
    }

    /// <summary>
    /// <paramref name="text"/> in several reads, cut at the given <em>byte</em> offsets of its UTF-8
    /// encoding — which can fall inside a line, inside a JSON string, or inside one Persian letter.
    /// </summary>
    public SseScript SendCutAt(string text, params int[] byteOffsets)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var from = 0;
        foreach (var cut in byteOffsets.Append(bytes.Length))
        {
            var part = bytes[from..cut];
            from = cut;
            _steps.Add(_ => Task.FromResult<byte[]?>(part));
        }

        return this;
    }

    /// <summary>The provider pausing before its next read.</summary>
    public SseScript Wait(TimeSpan delay)
    {
        _steps.Add(async ct =>
        {
            await Task.Delay(delay, ct);
            return Array.Empty<byte>();
        });
        return this;
    }

    /// <summary>The provider going quiet and staying quiet, until the reader gives up.</summary>
    public SseScript Hang()
    {
        _steps.Add(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        });
        return this;
    }

    /// <summary>The connection dying mid-body.</summary>
    public SseScript Fail(Exception exception)
    {
        _steps.Add(_ => throw exception);
        return this;
    }

    public HttpResponseMessage ToResponse(HttpStatusCode status = HttpStatusCode.OK, string contentType = "text/event-stream")
    {
        var content = new StreamContent(new ScriptedStream(_steps));
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(status) { Content = content };
    }

    // ---- events ----------------------------------------------------------------------------

    /// <summary>
    /// Persian goes onto the wire as UTF-8, as OpenRouter sends it — not as Unicode escapes, which would
    /// hide the case where a network read ends inside a letter.
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions Raw = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Event(string json) => $"data: {json}\n\n";

    public const string Done = "data: [DONE]\n\n";

    /// <summary>A comment line, as OpenRouter sends while it waits on an upstream.</summary>
    public static string Comment(string text = "OPENROUTER PROCESSING") => $": {text}\n\n";

    /// <summary>One chunk of a streamed reply: a piece of text and/or a finish reason.</summary>
    public static string Chunk(string? text = null, string? finish = null, string model = "vendor/model:free", bool first = false)
    {
        var delta = new JsonObject();
        if (first) delta["role"] = "assistant";
        if (text is not null) delta["content"] = text;

        return new JsonObject
        {
            ["id"] = "gen-1",
            ["model"] = model,
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish })
        }.ToJsonString(Raw);
    }

    /// <summary>The usage-only chunk that follows the last one: <c>choices</c> is empty.</summary>
    public static string UsageChunk(long prompt, long completion, string model = "vendor/model:free") =>
        new JsonObject
        {
            ["id"] = "gen-1",
            ["model"] = model,
            ["choices"] = new JsonArray(),
            ["usage"] = new JsonObject { ["prompt_tokens"] = prompt, ["completion_tokens"] = completion, ["total_tokens"] = prompt + completion }
        }.ToJsonString(Raw);

    /// <summary>A fragment of a tool call: id and name on the first, argument text on the rest.</summary>
    public static string ToolFragment(int? index, string? id = null, string? name = null, string? arguments = null,
        string? finish = null, string model = "vendor/model:free")
    {
        var delta = new JsonObject();

        // A bare finish chunk has an empty delta, as a real one does — not an empty tool call.
        if (index is not null || id is not null || name is not null || arguments is not null)
        {
            var function = new JsonObject();
            if (name is not null) function["name"] = name;
            if (arguments is not null) function["arguments"] = arguments;

            var call = new JsonObject();
            if (index is not null) call["index"] = index;
            if (id is not null) call["id"] = id;
            call["type"] = "function";
            call["function"] = function;
            delta["tool_calls"] = new JsonArray(call);
        }

        return new JsonObject
        {
            ["id"] = "gen-1",
            ["model"] = model,
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish })
        }.ToJsonString(Raw);
    }

    private sealed class ScriptedStream(IReadOnlyList<Func<CancellationToken, Task<byte[]?>>> steps) : Stream
    {
        private int _next;
        private byte[] _current = [];
        private int _offset;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (_offset >= _current.Length)
            {
                if (_next >= steps.Count) return 0;

                var chunk = await steps[_next++](ct);
                if (chunk is null) return 0;

                _current = chunk;
                _offset = 0;
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("The client must read this body asynchronously.");

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
