using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Harbora.Tests.Fakes;

/// <summary>
/// Just enough of <see cref="IDockerClient"/> for <c>DockerEngine.BuildImageFromTarAsync</c>: a
/// configurable endpoint (a Unix-socket one makes the engine use its socket transport, anything else
/// the typed call), a typed build call that reports scripted messages, and an image inspect that
/// answers found or not-found. Every other member throws, so a test that starts depending on more says
/// so loudly instead of passing on a default.
///
/// <para>
/// This stands in for the Docker.DotNet client, not for the engine under test: the engine class is the
/// real one. What it cannot stand in for is a real daemon's behaviour — see the tests that use it for
/// what that leaves unproven.
/// </para>
/// </summary>
public sealed class ScriptedDockerClient
{
    private readonly JSONMessage[] _messages;
    private readonly bool _imageExists;

    /// <summary>Every image reference the engine asked the daemon to inspect, in order.</summary>
    public List<string> InspectedImages { get; } = [];

    /// <summary>When set, the inspect fails with this instead of answering.</summary>
    public Exception? InspectFailure { get; init; }

    /// <summary>How many times the typed build call was made — zero when the socket transport ran.</summary>
    public int TypedBuildCalls { get; private set; }

    public IDockerClient Client { get; }

    public ScriptedDockerClient(bool imageExists, params JSONMessage[] messages) : this(imageExists, null, messages)
    {
    }

    /// <summary>A client whose endpoint is a Unix socket — the engine then sends the build through its
    /// socket transport, to whatever is listening there, and only the inspect goes through this.</summary>
    public static ScriptedDockerClient OnSocket(Uri endpoint, bool imageExists) => new(imageExists, endpoint, []);

    private ScriptedDockerClient(bool imageExists, Uri? endpoint, JSONMessage[] messages)
    {
        _imageExists = imageExists;
        _messages = messages;

        var configuration = new DockerClientConfiguration(endpoint ?? new Uri("tcp://127.0.0.1:2375"));
        var images = DispatchStandIn.Create<IImageOperations>(Images);
        Client = DispatchStandIn.Create<IDockerClient>((method, _) => method.Name switch
        {
            "get_Configuration" => configuration,
            "get_Images" => images,
            _ => throw new NotSupportedException($"IDockerClient.{method.Name} is not scripted.")
        });
    }

    private object? Images(MethodInfo method, object?[] args)
    {
        switch (method.Name)
        {
            case "BuildImageFromDockerfileAsync":
                TypedBuildCalls++;
                var progress = args.OfType<IProgress<JSONMessage>>().Single();
                foreach (var message in _messages) progress.Report(message);
                return CompletedTask(method.ReturnType);

            case "InspectImageAsync":
                InspectedImages.Add((string)args[0]!);
                if (InspectFailure is not null) return Task.FromException<ImageInspectResponse>(InspectFailure);
                return _imageExists
                    ? Task.FromResult(new ImageInspectResponse { ID = "sha256:abc" })
                    : Task.FromException<ImageInspectResponse>(
                        new DockerImageNotFoundException(HttpStatusCode.NotFound, "No such image"));

            default:
                throw new NotSupportedException($"IImageOperations.{method.Name} is not scripted.");
        }
    }

    private static object CompletedTask(Type taskType)
    {
        if (taskType == typeof(Task)) return Task.CompletedTask;
        var result = taskType.GetGenericArguments()[0];
        return typeof(Task).GetMethod(nameof(Task.FromResult))!
            .MakeGenericMethod(result).Invoke(null, [null])!;
    }
}

/// <summary>A hand-rolled interface stand-in: this project has no mocking library, and the Docker.DotNet
/// interfaces are far too wide to implement by hand for the two members that matter.</summary>
public class DispatchStandIn : DispatchProxy
{
    private Func<MethodInfo, object?[], object?>? _handler;

    public static T Create<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = Create<T, DispatchStandIn>();
        ((DispatchStandIn)(object)proxy)._handler = handler;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        _handler!(targetMethod!, args ?? []);
}

/// <summary>
/// A stand-in for the Docker daemon on a Unix socket: accepts one connection, reads the whole
/// <c>/build</c> request (the engine's socket transport writes the context while it reads), and
/// answers 200 with the JSON message lines it was given. It is a script, not a daemon — it says
/// whatever it is told to, which is the point: a test decides what the "daemon" streams.
/// </summary>
public sealed class FakeUnixBuildDaemon : IAsyncDisposable
{
    private readonly Socket _listener;
    private readonly Task _serving;
    private readonly string _path;

    /// <summary>The request line the engine sent, e.g. <c>POST /v1.41/build?t=…</c>.</summary>
    public string? RequestLine { get; private set; }

    /// <summary>Set when serving the request threw, so a test failure names the cause.</summary>
    public Exception? ServeFailure { get; private set; }

    /// <summary>The endpoint to hand <see cref="ScriptedDockerClient.OnSocket"/>.</summary>
    public Uri Endpoint => new("unix://" + _path.Replace('\\', '/'));

    public FakeUnixBuildDaemon(params string[] jsonLines)
    {
        _path = Path.Combine(Path.GetTempPath(), $"hb-{Guid.NewGuid():N}.sock");
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_path));
        _listener.Listen(1);
        _serving = ServeAsync(jsonLines);
    }

    private async Task ServeAsync(string[] jsonLines)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var connection = await _listener.AcceptAsync(timeout.Token);
            await using var stream = new NetworkStream(connection, ownsSocket: false);

            var head = await ReadHeadAsync(stream, timeout.Token);
            RequestLine = head.Split("\r\n")[0];
            await DrainBodyAsync(stream, head, timeout.Token);

            var body = Encoding.UTF8.GetBytes(string.Join("\n", jsonLines) + "\n");
            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, timeout.Token);
            await stream.WriteAsync(body, timeout.Token);
            await stream.FlushAsync(timeout.Token);
            connection.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex)
        {
            ServeFailure = ex;
        }
    }

    private static async Task<string> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, ct) == 0) throw new EndOfStreamException("request ended inside its head");
            head.Append((char)one[0]);
        }
        return head.ToString();
    }

    /// <summary>Reads the request body so closing the socket never resets a client still writing.</summary>
    private static async Task DrainBodyAsync(Stream stream, string head, CancellationToken ct)
    {
        var headers = head.Split("\r\n").Skip(1)
            .Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);

        if (headers.TryGetValue("Content-Length", out var length))
        {
            await ReadExactlyAsync(stream, int.Parse(length), ct);
            return;
        }

        if (headers.TryGetValue("Transfer-Encoding", out var encoding) && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            while (true)
            {
                var size = Convert.ToInt32((await ReadLineAsync(stream, ct)).Trim(), 16);
                await ReadExactlyAsync(stream, size + 2, ct); // the chunk and its CRLF
                if (size == 0) return;
            }
        }
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one, ct) == 0) throw new EndOfStreamException("request ended inside a chunk header");
            if (one[0] == '\n') return line.ToString().TrimEnd('\r');
            line.Append((char)one[0]);
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[Math.Max(count, 1)];
        await stream.ReadExactlyAsync(buffer.AsMemory(0, count), ct);
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Dispose();
        await _serving;
        try { File.Delete(_path); } catch { /* temp file — best effort */ }
    }
}
