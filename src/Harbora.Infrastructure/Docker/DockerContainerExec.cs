using Docker.DotNet;
using Harbora.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Docker;

/// <summary>
/// A shell inside a local container, over docker's attached exec stream.
///
/// Thin on purpose. Everything about who may open this, what is run, how big it is and when it
/// closes lives in <see cref="Terminals.TerminalAccess"/> and in the endpoint; this only carries
/// bytes. The one piece of judgement here is what an ended stream looks like — docker signals it as
/// a read of zero with <c>EOF</c>, and treating that as "nothing right now" instead of "it is over"
/// leaves a session spinning against a shell that exited.
/// </summary>
/// <param name="stream">The attached exec stream.</param>
/// <param name="execId">The exec instance, which is what <c>/exec/{id}/resize</c> addresses.</param>
/// <param name="endpoint">
/// Where the daemon is, as <c>IDockerClient.Configuration.EndpointBaseUri</c> reports it. Only a
/// Unix socket can be resized — see <see cref="ResizeAsync"/>.
/// </param>
/// <param name="apiVersion">
/// The API version the resize is posted to; <see cref="DockerEngine"/> passes the one it pins for
/// <c>/build</c>, so the direct calls and the typed client cannot end up on different API surfaces.
/// </param>
/// <param name="logger">Where a resize that did not land is recorded — it is never thrown.</param>
/// <param name="resizeTimeout">How long one resize may take. Defaults to <see cref="DefaultResizeTimeout"/>.</param>
internal sealed class DockerContainerExec(
    MultiplexedStream stream,
    string execId,
    Uri? endpoint,
    string apiVersion,
    ILogger logger,
    TimeSpan? resizeTimeout = null)
    : IContainerExec
{
    /// <summary>
    /// A resize is a few bytes to a local socket. If the daemon has not answered in this long it is
    /// not going to, and nothing should be left waiting on it.
    /// </summary>
    internal static readonly TimeSpan DefaultResizeTimeout = TimeSpan.FromSeconds(5);

    // The browser sends a resize for every frame of a window drag, and the caller does not await
    // them. Left alone they would race each other to the daemon over separate connections, and
    // whichever landed last — not whichever was sent last — would be the size the shell kept. That
    // is the symptom this exists to fix, so they go one at a time, in order, and one that has been
    // overtaken by a newer size is not sent. Never disposed: it holds no wait handle, and a resize
    // arriving after the session ended must find it working, not throw.
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private int _latest;

    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        var scratch = new byte[buffer.Length];
        var result = await stream.ReadOutputAsync(scratch, 0, scratch.Length, ct);

        if (result.EOF) return 0;

        scratch.AsMemory(0, result.Count).CopyTo(buffer);
        return result.Count;
    }

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) =>
        stream.WriteAsync(data.ToArray(), 0, data.Length, ct);

    /// <summary>
    /// Tells the shell its window changed size: <c>POST /exec/{id}/resize</c>.
    ///
    /// <para><b>Why this posts by hand.</b> Docker.DotNet.Enhanced 3.131.1 (the fork this moved to
    /// for Docker 29 support) has no exec-resize call. <c>IExecOperations</c> is create, start and
    /// inspect; <c>IContainerOperations.ResizeContainerTtyAsync</c> posts to
    /// <c>/containers/{id}/resize</c>, which 404s for an exec id; and the request-building methods on
    /// <c>IDockerClient</c> are internal. The Docker Engine API still has the route, so this sends
    /// it through <see cref="DockerBuildTransport.CreateSocketHandler"/> — the same Unix-socket
    /// connection <c>/build</c> already uses. The initial size is unaffected: it travels as
    /// <c>ConsoleSize</c> on the exec create and start calls.</para>
    ///
    /// <para><b>A no-op for any endpoint that is not a Unix socket.</b> The socket connection is the
    /// only one there is here, and <see cref="DockerBuildTransport.Handles"/> is what says which
    /// endpoints it serves. A named pipe on a Windows development machine and a TCP daemon are
    /// deliberately left without one: a second transport for endpoints no deployment runs on is
    /// code nobody exercises. On those the shell keeps the size it opened with.</para>
    ///
    /// <para><b>One at a time, newest wins.</b> Resizes reach the daemon in the order they were made,
    /// and one that a newer call has overtaken while it waited is dropped rather than sent — see
    /// <c>_oneAtATime</c> for why the final size would otherwise be a matter of luck.</para>
    ///
    /// <para><b>It never fails the session.</b> A resize that does not land is a wrongly-drawn
    /// screen; letting it escape would turn that into a lost one. The caller does not await this, so
    /// anything thrown would surface nowhere useful anyway. Cancellation — the session ending while
    /// a resize is in flight — is expected and silent; every other failure is logged and swallowed.
    /// </para>
    /// </summary>
    public async Task ResizeAsync(uint columns, uint rows, CancellationToken ct)
    {
        if (!DockerBuildTransport.Handles(endpoint)) return;

        // Numbered before the first await, so the numbers follow the order the calls were made in.
        var mine = Interlocked.Increment(ref _latest);
        var held = false;

        try
        {
            await _oneAtATime.WaitAsync(ct).ConfigureAwait(false);
            held = true;

            // A newer size is already waiting behind this one, and it is the size the window has
            // now. Sending this first would only give the shell a size for a moment it is past.
            if (mine != Volatile.Read(ref _latest)) return;

            // The deadline is this token rather than HttpClient.Timeout, so a timeout and a session
            // ending are told apart below: only the second is silent. It starts once this resize has
            // its turn, so waiting behind another one does not spend it.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(resizeTimeout ?? DefaultResizeTimeout);

            using var handler = DockerBuildTransport.CreateSocketHandler(endpoint!);
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = ResizeRequest(apiVersion, execId, columns, rows);

            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Terminal resize for exec {Exec} refused by the daemon with {Status}; the shell keeps its previous size",
                    execId, (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The session ended first. Nothing is left to resize.
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Terminal resize for exec {Exec} failed: {Msg}; the shell keeps its previous size",
                execId, ex.Message);
        }
        finally
        {
            if (held) _oneAtATime.Release();
        }
    }

    /// <summary>
    /// The request a resize sends, and nothing else — no socket, no daemon — so the wire format is
    /// something a test can pin: <c>POST /v{apiVersion}/exec/{id}/resize?h={rows}&amp;w={columns}</c>.
    /// Docker's query is height first, which is the opposite of the order this method's callers pass
    /// them in.
    ///
    /// <para>The size goes through <see cref="Terminals.TerminalAccess.Size"/>, the same clamp the
    /// initial size gets, so a window reporting zero while a page loads is not sent to the daemon
    /// as zero. The host is a placeholder the socket connection ignores.</para>
    /// </summary>
    internal static HttpRequestMessage ResizeRequest(string apiVersion, string execId, uint columns, uint rows)
    {
        var (safeColumns, safeRows) = Terminals.TerminalAccess.Size(Saturate(columns), Saturate(rows));

        return new HttpRequestMessage(
            HttpMethod.Post,
            $"http://localhost/v{apiVersion}/exec/{Uri.EscapeDataString(execId)}/resize?h={safeRows}&w={safeColumns}");
    }

    /// <summary>
    /// <c>TerminalAccess.Size</c> takes an int; a uint above <see cref="int.MaxValue"/> must clamp
    /// to the top of the range, not wrap negative and clamp to the bottom.
    /// </summary>
    private static int Saturate(uint value) => (int)Math.Min(value, (uint)int.MaxValue);

    public ValueTask DisposeAsync()
    {
        stream.Dispose();
        return ValueTask.CompletedTask;
    }
}
