using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Infrastructure.Monitoring;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 2.1 (2026-09 market-gaps round two): <see cref="HttpUptimeProbe"/> against real loopback sockets —
/// no fake stands in for the transport here, because the one behaviour this sub-project's brief calls
/// out by name — "do not let a slow or hanging target stall the checker" — is a property of the real
/// timeout plumbing, not of anything a mock can prove. Each server is a bare <see cref="TcpListener"/>
/// speaking just enough raw HTTP to control exactly what <see cref="HttpUptimeProbe"/> sees.
/// </summary>
public class HttpUptimeProbeTests
{
    private readonly HttpUptimeProbe _probe = new();

    [Fact]
    public async Task A_matching_status_is_reported_as_up()
    {
        await using var server = await RawHttpServer.StartAsync(req => (200, "ok"));

        var result = await _probe.ProbeAsync(server.Url, 200, null, TimeSpan.FromSeconds(5), default);

        result.Outcome.Should().Be(ProbeOutcome.Up);
        result.HttpStatus.Should().Be(200);
        result.LatencyMs.Should().NotBeNull();
    }

    [Fact]
    public async Task A_mismatched_status_is_reported_as_down_not_could_not_run()
    {
        await using var server = await RawHttpServer.StartAsync(req => (503, "unavailable"));

        var result = await _probe.ProbeAsync(server.Url, 200, null, TimeSpan.FromSeconds(5), default);

        result.Outcome.Should().Be(ProbeOutcome.Down);
        result.HttpStatus.Should().Be(503);
        result.Detail.Should().Contain("503").And.Contain("200");
    }

    [Fact]
    public async Task A_body_that_does_not_contain_the_required_text_is_down()
    {
        await using var server = await RawHttpServer.StartAsync(req => (200, "<html>maintenance</html>"));

        var result = await _probe.ProbeAsync(server.Url, 200, "Welcome back", TimeSpan.FromSeconds(5), default);

        result.Outcome.Should().Be(ProbeOutcome.Down);
        result.HttpStatus.Should().Be(200, "the status matched — only the body match failed");
    }

    [Fact]
    public async Task A_body_that_does_contain_the_required_text_is_up()
    {
        await using var server = await RawHttpServer.StartAsync(req => (200, "<html>Welcome back!</html>"));

        var result = await _probe.ProbeAsync(server.Url, 200, "Welcome back", TimeSpan.FromSeconds(5), default);

        result.Outcome.Should().Be(ProbeOutcome.Up);
    }

    [Fact]
    public async Task A_refused_connection_is_down_not_could_not_run()
    {
        // Bind to get a genuinely free loopback port, then release it before probing — nothing is
        // listening there any more, so the OS answers with an immediate refusal rather than a hang.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var result = await _probe.ProbeAsync(
            new Uri($"http://127.0.0.1:{port}/"), 200, null, TimeSpan.FromSeconds(5), default);

        result.Outcome.Should().Be(ProbeOutcome.Down, "a refused connection is a real, observed fact about the target");
        result.HttpStatus.Should().BeNull();
    }

    /// <summary>
    /// The brief's own words: "do not let a slow or hanging target stall the checker. Timeouts, and a
    /// failure to time out is itself a reportable state." A server that accepts the connection and then
    /// says nothing forever is exactly the target this proves the probe does not wait out.
    /// </summary>
    [Fact]
    public async Task A_hanging_target_times_out_rather_than_stalling_the_checker()
    {
        await using var server = await RawHttpServer.StartHangingAsync();
        var timeout = TimeSpan.FromMilliseconds(300);

        var stopwatch = Stopwatch.StartNew();
        var result = await _probe.ProbeAsync(server.Url, 200, null, timeout, default);
        stopwatch.Stop();

        result.Outcome.Should().Be(ProbeOutcome.Down);
        result.Detail.Should().Contain("timed out", "the history must say this was a timeout, not a generic failure");
        // Generous upper bound against a slow CI box — the point is "did not hang", not a tight SLA.
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "a hanging target must not stall the checker past its own configured timeout");
    }

    /// <summary>A minimal raw HTTP/1.1 server over one <see cref="TcpListener"/> connection at a time —
    /// enough to control exactly what <see cref="HttpUptimeProbe"/> sees, without pulling in a full
    /// ASP.NET test host for what is, per request, three lines of protocol.</summary>
    private sealed class RawHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public Uri Url { get; }

        private RawHttpServer(TcpListener listener, Task loop)
        {
            _listener = listener;
            _loop = loop;
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Url = new Uri($"http://127.0.0.1:{port}/");
        }

        public static Task<RawHttpServer> StartAsync(Func<string, (int Status, string Body)> respond)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var cts = new CancellationTokenSource();

            async Task LoopAsync()
            {
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        using var client = await listener.AcceptTcpClientAsync(cts.Token);
                        using var stream = client.GetStream();
                        var request = await ReadRequestLineAsync(stream, cts.Token);
                        var (status, body) = respond(request);
                        await WriteResponseAsync(stream, status, body, cts.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            }

            var server = new RawHttpServer(listener, Task.Run(LoopAsync));
            return Task.FromResult(server);
        }

        /// <summary>Accepts a connection and then never answers — the server this test file exists for.</summary>
        public static Task<RawHttpServer> StartHangingAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var cts = new CancellationTokenSource();

            async Task LoopAsync()
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync(cts.Token);
                    // Hold the connection open, silently, until the test disposes this server.
                    await Task.Delay(Timeout.Infinite, cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            }

            var server = new RawHttpServer(listener, Task.Run(LoopAsync));
            return Task.FromResult(server);
        }

        private static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[4096];
            var read = await stream.ReadAsync(buffer, ct);
            return Encoding.ASCII.GetString(buffer, 0, read);
        }

        private static async Task WriteResponseAsync(NetworkStream stream, int status, string body, CancellationToken ct)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var reason = status switch { 200 => "OK", 503 => "Service Unavailable", _ => "Status" };
            var header =
                $"HTTP/1.1 {status} {reason}\r\n" +
                $"Content-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "Connection: close\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(headerBytes, ct);
            await stream.WriteAsync(bodyBytes, ct);
            await stream.FlushAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try { await _loop; } catch { /* loop's own cancellation is expected */ }
        }
    }
}
