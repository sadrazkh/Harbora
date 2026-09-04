using System.Diagnostics;
using Harbora.Application.Abstractions;

namespace Harbora.Infrastructure.Monitoring;

/// <summary>
/// The real <see cref="IUptimeProbe"/> — an HTTP GET from this process against the app's own public
/// domain, exactly the vantage point <c>CertificateWatcher</c> already probes TLS from. See
/// <c>UptimeCheck</c>'s own doc for what running from the panel (rather than from each node) cannot
/// detect.
/// </summary>
public sealed class HttpUptimeProbe : IUptimeProbe
{
    public async Task<UptimeProbeResult> ProbeAsync(
        Uri url, int expectedStatus, string? bodyContains, TimeSpan timeout, CancellationToken ct)
    {
        // A fresh handler per probe, the same reasoning HealthProbe.DefaultHttpProbe gives: pooling to
        // a target between checks keeps sockets open for no benefit a once-a-minute probe ever collects.
        using var handler = new SocketsHttpHandler { ConnectTimeout = timeout, AllowAutoRedirect = true };
        using var client = new HttpClient(handler) { Timeout = timeout };

        // Enforced independently of HttpClient.Timeout: that property alone has, in practice, still let
        // a hung DNS resolution or a stalled TLS handshake outlive it on some runtimes. A linked token
        // cancelled on our own clock is what actually guarantees this method returns, which is the one
        // promise UptimeChecker depends on to keep moving to the next app.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token);
            var elapsed = stopwatch.ElapsedMilliseconds;
            var status = (int)response.StatusCode;

            if (status != expectedStatus)
                return new UptimeProbeResult(ProbeOutcome.Down, status, elapsed,
                    $"{url} answered {status}, expected {expectedStatus}.");

            if (!string.IsNullOrEmpty(bodyContains))
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                if (!body.Contains(bodyContains, StringComparison.Ordinal))
                    return new UptimeProbeResult(ProbeOutcome.Down, status, elapsed,
                        $"{url} answered {status}, but the body did not contain the expected text.");
            }

            return new UptimeProbeResult(ProbeOutcome.Up, status, elapsed, $"{url} answered {status}.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own CancelAfter fired, not the caller's shutdown token — a real, reportable timeout,
            // not an exception UptimeChecker should let escape. Kept distinct in the Detail text from a
            // refused connection below, so the history says which of the two actually happened.
            return new UptimeProbeResult(ProbeOutcome.Down, null, stopwatch.ElapsedMilliseconds,
                $"{url} did not answer within {timeout.TotalSeconds:0}s (timed out).");
        }
        catch (OperationCanceledException)
        {
            // The caller's own token was cancelled (process shutdown) — this is not a fact about the
            // target at all, and must propagate rather than being recorded as a check result.
            throw;
        }
        catch (HttpRequestException ex)
        {
            return new UptimeProbeResult(ProbeOutcome.Down, null, stopwatch.ElapsedMilliseconds,
                $"{url} refused the connection: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Anything else is not a fact about the target — a bug in this method, an unexpected
            // exception shape from the transport. UptimeCheckOutcome.CouldNotRun's own doc: this must
            // never be recorded as Down, which would blame the app for a question the checker itself
            // failed to ask.
            return new UptimeProbeResult(ProbeOutcome.CouldNotRun, null, stopwatch.ElapsedMilliseconds,
                $"the check itself failed before it could ask {url}: {ex.Message}");
        }
    }
}
