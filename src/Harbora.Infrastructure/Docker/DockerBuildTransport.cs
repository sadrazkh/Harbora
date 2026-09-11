using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Docker.DotNet.Models;

namespace Harbora.Infrastructure.Docker;

/// <summary>
/// Posts one <c>/build</c> to the daemon over its Unix socket using .NET's own HTTP stack, instead
/// of the one bundled inside Docker.DotNet.
///
/// <para><b>Why this exists at all.</b> Docker.DotNet — and its Enhanced fork, which keeps the same
/// transport — talks to the socket through <c>Microsoft.Net.Http.Client.ManagedHandler</c>, a
/// minimal HTTP/1.1 implementation that writes the whole request body before it begins reading the
/// response. That was fine while the daemon buffered a build context and only answered once it had
/// all of it. Docker 29 answers immediately: the first <c>Step 1/23</c> lines come back while the
/// context is still going up. Nobody is draining them, the daemon's send buffer fills, it stops
/// being able to write, and the exchange dies — the client sees
/// <c>IOException: Unable to write data to the transport connection: Broken pipe</c> part-way
/// through <c>HttpContent.CopyToAsync</c>, with no build output and nothing naming a cause.</para>
///
/// <para>It reproduces exactly this way and no other. The same context, the same endpoint, the same
/// API version and the same socket build perfectly through <c>curl</c> — from the host and from
/// inside the panel's own container, with a Content-Length body and with a chunked one. The only
/// variable that changes the outcome is which client writes it, because curl reads and writes at
/// once and <c>ManagedHandler</c> does not.</para>
///
/// <para><b>Only the build path moves.</b> Every other call this platform makes is small and
/// request-then-response, which is the shape <c>ManagedHandler</c> handles correctly — so they stay
/// on Docker.DotNet, where the typed models and error handling already live. This covers the one
/// endpoint that streams in both directions at the same time.</para>
/// </summary>
internal static class DockerBuildTransport
{
    /// <summary>
    /// Whether this endpoint is one this transport can serve. Unix sockets only: a named pipe on
    /// Windows and a TCP daemon both go back through Docker.DotNet, because neither is where the
    /// failure lives and a second code path nobody exercises is a second code path that rots.
    /// </summary>
    public static bool Handles(Uri? endpoint) =>
        endpoint is not null
        && (endpoint.Scheme.Equals("unix", StringComparison.OrdinalIgnoreCase)
            || endpoint.Scheme.Equals("unix+http", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Streams <paramref name="context"/> to <c>/build</c> and reports every progress message the
    /// daemon sends back, as it sends it.
    /// </summary>
    /// <param name="apiVersion">
    /// Pinned rather than negotiated, and pinned to what Docker.DotNet itself asks for, so this
    /// transport and the rest of the client cannot end up speaking to two different API surfaces.
    /// </param>
    public static async Task BuildAsync(
        Uri endpoint,
        ImageBuildParameters parameters,
        Stream context,
        IProgress<JSONMessage> progress,
        string apiVersion,
        CancellationToken ct)
    {
        var socketPath = endpoint.LocalPath;

        // ConnectCallback is the whole point: SocketsHttpHandler does full-duplex properly, so the
        // response is drained while the body is still going out. The scheme on the request URI is
        // http because that is what is spoken over the socket; the host is a placeholder the
        // callback ignores.
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), token)
                        .ConfigureAwait(false);

                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },

            // A build is not a request with a deadline. npm install and dotnet publish inside a
            // Dockerfile take as long as they take, and the pipeline's own CancellationToken is what
            // is allowed to end this.
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
        };

        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"http://localhost/v{apiVersion}/build?{Query(parameters)}");

        request.Content = new StreamContent(context);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-tar");

        // ResponseHeadersRead, so the call returns as soon as the daemon has answered rather than
        // when the body is complete — without it the response would be buffered to the end and this
        // would report a finished build all at once, which is the progress log nobody can watch.
        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            throw new HttpRequestException(
                $"The daemon refused the build with {(int)response.StatusCode}: {Trim(body)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var lines = new StreamReader(stream, Encoding.UTF8);

        // One JSON object per line, which is what the daemon's build stream is. A line that does not
        // parse is skipped rather than thrown on: it is progress output, and failing a build over a
        // message we could not read would turn a cosmetic change in the daemon into an outage.
        while (await lines.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JSONMessage? message = null;

            try
            {
                message = JsonSerializer.Deserialize<JSONMessage>(line, Json);
            }
            catch (JsonException)
            {
                // Not ours to interpret.
            }

            if (message is not null) progress.Report(message);
        }
    }

    /// <summary>
    /// Docker's JSON is camelCase on the wire and the models are PascalCase, and the daemon sends
    /// fields this version of the models does not know about.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// The same query the typed client would have built — <c>t</c>, <c>dockerfile</c>,
    /// <c>buildargs</c>, <c>cachefrom</c>, <c>nocache</c>, <c>rm</c>, <c>forcerm</c> — so moving the
    /// transport does not quietly move the behaviour with it.
    /// </summary>
    private static string Query(ImageBuildParameters parameters)
    {
        var parts = new List<string>();

        foreach (var tag in parameters.Tags ?? [])
        {
            parts.Add($"t={Uri.EscapeDataString(tag)}");
        }

        if (!string.IsNullOrWhiteSpace(parameters.Dockerfile))
        {
            parts.Add($"dockerfile={Uri.EscapeDataString(parameters.Dockerfile)}");
        }

        if (parameters.BuildArgs is { Count: > 0 })
        {
            parts.Add($"buildargs={Uri.EscapeDataString(JsonSerializer.Serialize(parameters.BuildArgs))}");
        }

        if (parameters.CacheFrom is { Count: > 0 })
        {
            parts.Add($"cachefrom={Uri.EscapeDataString(JsonSerializer.Serialize(parameters.CacheFrom))}");
        }

        parts.Add($"nocache={(parameters.NoCache == true ? "1" : "0")}");
        parts.Add($"rm={(parameters.Remove == false ? "0" : "1")}");
        parts.Add($"forcerm={(parameters.ForceRemove == true ? "1" : "0")}");

        return string.Join('&', parts);
    }

    /// <summary>A daemon refusal is a sentence, not a page; this keeps it one.</summary>
    private static string Trim(string body) =>
        body.Length <= 400 ? body.Trim() : body[..400].Trim() + "…";
}
