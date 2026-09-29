using System.Net.Sockets;
using System.Text;
using Docker.DotNet;
using FluentAssertions;
using Harbora.Infrastructure.Docker;
using Harbora.Infrastructure.Terminals;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Resizing a web terminal that is already open.
///
/// <para>
/// Docker.DotNet.Enhanced 3.131.1 has no exec-resize call, so resizing the browser window while a
/// session was open stopped reaching the shell — full-screen programs kept drawing for the size the
/// session opened with. <see cref="DockerContainerExec.ResizeAsync"/> now posts
/// <c>/exec/{id}/resize</c> itself, over the same Unix-socket connection <c>/build</c> uses.
/// </para>
///
/// <para>
/// There is no Docker on the machines these run on, so nothing here talks to a real daemon. The
/// request is pinned as a pure function; and where the socket itself matters, a listener on a real
/// Unix socket stands in for the daemon and records what arrives. What no test here can say is
/// whether a live daemon accepts that request and redraws the shell — that is for the CI lane with
/// Docker, and is reported as unverified until it has run there.
/// </para>
/// </summary>
public class DockerExecResizeTests
{
    private const string ApiVersion = "1.41";

    // --- The request, with no daemon and no socket -------------------------------------------

    [Fact]
    public void A_resize_is_a_post_to_the_exec_resize_route_with_rows_as_h_and_columns_as_w()
    {
        using var request = DockerContainerExec.ResizeRequest(ApiVersion, "abc123", columns: 120, rows: 40);

        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.AbsolutePath.Should().Be("/v1.41/exec/abc123/resize");
        request.RequestUri.Query.Should().Be("?h=40&w=120");
    }

    [Fact]
    public void The_api_version_is_the_one_it_is_given_rather_than_a_number_of_its_own()
    {
        using var request = DockerContainerExec.ResizeRequest("1.99", "abc123", 120, 40);

        request.RequestUri!.AbsolutePath.Should().StartWith("/v1.99/exec/");
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(1u, 1u)]
    [InlineData(120u, 40u)]
    [InlineData(100_000u, 100_000u)]
    public void The_size_is_clamped_by_TerminalAccess_Size_and_not_by_bounds_of_its_own(uint columns, uint rows)
    {
        var (safeColumns, safeRows) = TerminalAccess.Size((int)columns, (int)rows);

        using var request = DockerContainerExec.ResizeRequest(ApiVersion, "abc123", columns, rows);

        request.RequestUri!.Query.Should().Be($"?h={safeRows}&w={safeColumns}");
    }

    [Fact]
    public void A_size_too_large_for_an_int_clamps_to_the_top_of_the_range_rather_than_wrapping_to_the_bottom()
    {
        var (topColumns, topRows) = TerminalAccess.Size(int.MaxValue, int.MaxValue);

        using var request = DockerContainerExec.ResizeRequest(ApiVersion, "abc123", uint.MaxValue, uint.MaxValue);

        request.RequestUri!.Query.Should().Be($"?h={topRows}&w={topColumns}");
    }

    // --- A resize that cannot be sent is not sent, and is not an error -----------------------

    [Theory]
    [InlineData("npipe://./pipe/docker_engine")]
    [InlineData("tcp://127.0.0.1:1")]
    [InlineData("http://127.0.0.1:1")]
    [InlineData(null)]
    public async Task A_resize_against_an_endpoint_that_is_not_a_unix_socket_does_nothing_and_does_not_throw(
        string? endpoint)
    {
        var log = new RecordingLogger();
        await using var exec = NewExec(endpoint is null ? null : new Uri(endpoint), log);

        var act = () => exec.ResizeAsync(120, 40, CancellationToken.None);

        await act.Should().NotThrowAsync();

        // Nothing was attempted: an attempt against these addresses would have failed and said so.
        log.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_resize_whose_request_cannot_connect_does_not_throw_and_says_so()
    {
        var log = new RecordingLogger();
        await using var exec = NewExec(UnixEndpoint(NewSocketPath()), log);

        var act = () => exec.ResizeAsync(120, 40, CancellationToken.None);

        await act.Should().NotThrowAsync();
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_resize_the_daemon_refuses_does_not_throw_and_says_so()
    {
        // An exec that has already exited answers with an error status; that is a redraw that did
        // not happen, not a reason to close a terminal.
        using var daemon = new FakeDaemon(status: 404);
        var log = new RecordingLogger();
        await using var exec = NewExec(daemon.Endpoint, log);

        var act = () => exec.ResizeAsync(120, 40, CancellationToken.None);

        await act.Should().NotThrowAsync();
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains("404"));
    }

    [Fact]
    public async Task A_resize_the_daemon_never_answers_gives_up_in_time_and_does_not_throw()
    {
        using var daemon = new FakeDaemon(status: null);
        var log = new RecordingLogger();
        await using var exec = NewExec(daemon.Endpoint, log, timeout: TimeSpan.FromMilliseconds(200));

        var act = () => exec.ResizeAsync(120, 40, CancellationToken.None);

        await act.Should().NotThrowAsync();

        // A timeout is a failure worth a line, unlike the session ending.
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_resize_cancelled_before_it_starts_does_not_throw()
    {
        var log = new RecordingLogger();
        await using var exec = NewExec(UnixEndpoint(NewSocketPath()), log);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => exec.ResizeAsync(120, 40, cancelled.Token);

        await act.Should().NotThrowAsync();
        log.Entries.Should().BeEmpty("the session ending is expected, and nothing was lost");
    }

    [Fact]
    public async Task A_session_ending_while_a_resize_is_in_flight_does_not_throw()
    {
        using var daemon = new FakeDaemon(status: null);
        var log = new RecordingLogger();
        await using var exec = NewExec(daemon.Endpoint, log);
        using var session = new CancellationTokenSource();

        var resize = exec.ResizeAsync(120, 40, session.Token);

        // The request has reached the daemon, which is now sitting on it.
        await daemon.RequestHead.WaitAsync(TimeSpan.FromSeconds(10));
        await session.CancelAsync();

        var act = () => resize.WaitAsync(TimeSpan.FromSeconds(10));

        await act.Should().NotThrowAsync();
        log.Entries.Should().BeEmpty("the session ending is expected, and nothing was lost");
    }

    // --- The socket, against a listener standing in for the daemon ---------------------------

    [Fact]
    public async Task A_resize_reaches_the_daemons_socket_as_exactly_the_request_the_api_defines()
    {
        using var daemon = new FakeDaemon(status: 201);
        var log = new RecordingLogger();
        await using var exec = NewExec(daemon.Endpoint, log, execId: "9f8e7d6c");

        await exec.ResizeAsync(columns: 132, rows: 43, CancellationToken.None);

        var head = await daemon.RequestHead.WaitAsync(TimeSpan.FromSeconds(10));
        head.Split("\r\n")[0].Should().Be("POST /v1.41/exec/9f8e7d6c/resize?h=43&w=132 HTTP/1.1");
        log.Entries.Should().BeEmpty("a resize the daemon accepted is not worth a line");
    }

    [Fact]
    public async Task A_resize_is_clamped_on_the_wire_too()
    {
        using var daemon = new FakeDaemon(status: 201);
        await using var exec = NewExec(daemon.Endpoint, new RecordingLogger());
        var (safeColumns, safeRows) = TerminalAccess.Size(0, 0);

        await exec.ResizeAsync(0, 0, CancellationToken.None);

        var head = await daemon.RequestHead.WaitAsync(TimeSpan.FromSeconds(10));
        head.Split("\r\n")[0].Should().Be($"POST /v1.41/exec/abc123/resize?h={safeRows}&w={safeColumns} HTTP/1.1");
    }

    // --- Harness -----------------------------------------------------------------------------

    private static DockerContainerExec NewExec(
        Uri? endpoint, RecordingLogger log, string execId = "abc123", TimeSpan? timeout = null) =>
        new(new MultiplexedStream(new MemoryStream(), multiplexed: false),
            execId, endpoint, ApiVersion, log, timeout);

    // Short, because a Unix socket path is limited to about a hundred bytes and the temp directory
    // on a build machine can already be long.
    private static string NewSocketPath() =>
        Path.Combine(Path.GetTempPath(), $"hr-{Guid.NewGuid():N}"[..11] + ".sock");

    // The endpoint form Docker.DotNet reports for a daemon on a socket: unix:///path.
    private static Uri UnixEndpoint(string socketPath) =>
        new("unix://" + (socketPath.StartsWith('/') ? "" : "/") + socketPath.Replace('\\', '/'));

    /// <summary>
    /// Stands in for the daemon on a real Unix socket: accepts one connection, records the request
    /// it sends, and answers with <paramref name="status"/> — or, when that is null, holds the
    /// connection open and never answers, which is a daemon that has hung.
    /// </summary>
    private sealed class FakeDaemon : IDisposable
    {
        private readonly Socket _listener;
        private readonly string _path = NewSocketPath();
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource<string> _requestHead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeDaemon(int? status)
        {
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(_path));
            _listener.Listen(1);

            _ = ServeAsync(status);
        }

        public Uri Endpoint => UnixEndpoint(_path);

        /// <summary>The request line and headers, as received.</summary>
        public Task<string> RequestHead => _requestHead.Task;

        private async Task ServeAsync(int? status)
        {
            try
            {
                using var connection = await _listener.AcceptAsync(_stop.Token);

                var buffer = new byte[4096];
                var head = new StringBuilder();

                while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await connection.ReceiveAsync(buffer, SocketFlags.None, _stop.Token);
                    if (read == 0) break;
                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                _requestHead.TrySetResult(head.ToString());

                if (status is { } code)
                {
                    var reply = $"HTTP/1.1 {code} Reply\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await connection.SendAsync(Encoding.ASCII.GetBytes(reply), SocketFlags.None, _stop.Token);
                }
                else
                {
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // The test finished with the daemon still up; nothing to report.
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Dispose();
            _stop.Dispose();
            try { File.Delete(_path); } catch { /* temp file — best effort */ }
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
