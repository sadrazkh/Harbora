using System.Net.Sockets;
using Harbora.NodeAgent.Contracts;
using Microsoft.Extensions.Logging;

namespace Harbora.NodeAgent.Runtime;

public sealed record HealthOutcome(bool Healthy, string Detail);

/// <summary>
/// Decides whether a freshly-started container is actually serving.
///
/// <para>
/// HTTP and TCP probes run from the agent rather than from inside the container, which is what
/// lets a distroless image be health-checked at all — Docker's own HEALTHCHECK needs a shell and a
/// curl in the image, and the images most worth running have neither.
/// </para>
/// </summary>
public sealed class HealthProbe(
    IContainerRuntime runtime,
    TimeProvider clock,
    ILogger<HealthProbe> log,
    Func<Uri, TimeSpan, CancellationToken, Task<int?>>? httpProbe = null,
    Func<string, int, TimeSpan, CancellationToken, Task<bool>>? tcpProbe = null)
{
    private readonly Func<Uri, TimeSpan, CancellationToken, Task<int?>> _http = httpProbe ?? DefaultHttpProbe;
    private readonly Func<string, int, TimeSpan, CancellationToken, Task<bool>> _tcp = tcpProbe ?? DefaultTcpProbe;

    /// <summary>
    /// Poll until the container is healthy or the grace period runs out.
    ///
    /// <para>
    /// The start period is honoured before the first probe counts against the retry budget. A
    /// service that takes forty seconds to warm its cache is slow, not broken, and rolling it back
    /// for that would make the rollback the outage.
    /// </para>
    /// </summary>
    public async Task<HealthOutcome> WaitForHealthyAsync(
        ContainerSpec container, string containerId, string? network, TimeSpan grace, CancellationToken ct)
    {
        var probe = container.HealthCheck ?? new HealthCheckSpec();
        var deadline = clock.GetUtcNow() + grace;
        var startPeriod = TimeSpan.FromSeconds(probe.StartPeriodSeconds);
        var interval = TimeSpan.FromSeconds(Math.Max(1, probe.IntervalSeconds));
        var startedAt = clock.GetUtcNow();

        var failures = 0;
        var lastDetail = "no probe has run yet";

        while (clock.GetUtcNow() < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var inspected = await runtime.InspectAsync(containerId, ct);

            if (inspected is null)
                return new HealthOutcome(false, "the container disappeared while it was being health-checked");

            if (inspected.State is "exited" or "dead")
                // No amount of waiting fixes a container that has stopped. Failing now rather than
                // at the deadline turns a two-minute rollback into a two-second one.
                return new HealthOutcome(false, $"the container exited during startup (state: {inspected.State})");

            var withinStartPeriod = clock.GetUtcNow() - startedAt < startPeriod;

            var outcome = await RunProbeAsync(probe, inspected, container, network, ct);

            if (outcome.Healthy) return outcome;

            lastDetail = outcome.Detail;

            // Debug rather than warning: a probe failing during startup is the expected path, and
            // a warning per second would bury the one line that says the deploy actually failed.
            log.LogDebug(
                "Health probe for {Container} not satisfied yet ({Detail}); within start period: {Starting}.",
                container.Name, outcome.Detail, withinStartPeriod);

            if (!withinStartPeriod && ++failures >= probe.Retries)
                return new HealthOutcome(false, $"{failures} consecutive probe failures: {lastDetail}");

            await Task.Delay(interval, ct);
        }

        return new HealthOutcome(false, $"still not healthy after {grace.TotalSeconds:0}s: {lastDetail}");
    }

    private async Task<HealthOutcome> RunProbeAsync(
        HealthCheckSpec probe, RuntimeContainer inspected, ContainerSpec container, string? network, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, probe.TimeoutSeconds));

        switch (probe.Kind)
        {
            case HealthCheckKind.Http:
            {
                if (Address(inspected, network) is not { } host)
                    return new HealthOutcome(false, "the container has no reachable address yet");

                var port = probe.Port ?? container.Ports.FirstOrDefault()?.ContainerPort ?? 80;
                var uri = new Uri($"http://{Bracket(host)}:{port}{probe.Path ?? "/"}");

                var status = await _http(uri, timeout, ct);

                return status == probe.ExpectedStatus
                    ? new HealthOutcome(true, $"{uri} answered {status}")
                    : new HealthOutcome(false, $"{uri} answered {status?.ToString() ?? "nothing"}, expected {probe.ExpectedStatus}");
            }

            case HealthCheckKind.Tcp:
            {
                if (Address(inspected, network) is not { } host)
                    return new HealthOutcome(false, "the container has no reachable address yet");

                var port = probe.Port ?? container.Ports.FirstOrDefault()?.ContainerPort ?? 0;
                if (port <= 0) return new HealthOutcome(false, "a tcp health check needs a port");

                return await _tcp(host, port, timeout, ct)
                    ? new HealthOutcome(true, $"tcp {host}:{port} accepted a connection")
                    : new HealthOutcome(false, $"tcp {host}:{port} refused a connection");
            }

            case HealthCheckKind.Command:
            {
                if (probe.Command is not { Count: > 0 } argv)
                    return new HealthOutcome(false, "a command health check needs a command");

                var result = await runtime.ExecAsync(inspected.Id, argv, null, null, ct);

                return result.ExitCode == 0
                    ? new HealthOutcome(true, "the health command exited 0")
                    : new HealthOutcome(false, $"the health command exited {result.ExitCode}");
            }

            default:
            {
                // The weakest signal, and the honest one: with no probe configured, "running and
                // not restarting" is all the node actually knows.
                var running = inspected.State == "running" && inspected.Healthy != false;

                return running
                    ? new HealthOutcome(true, "the container is running")
                    : new HealthOutcome(false, $"the container is {inspected.State}");
            }
        }
    }

    /// <summary>The address the agent can reach the container on: its IP on the workload network.</summary>
    private static string? Address(RuntimeContainer container, string? network)
    {
        if (network is not null &&
            container.NetworkIpAddresses.TryGetValue(network, out var scoped) &&
            !string.IsNullOrWhiteSpace(scoped))
            return scoped;

        return container.NetworkIpAddresses.Values.FirstOrDefault(ip => !string.IsNullOrWhiteSpace(ip));
    }

    private static string Bracket(string host) => host.Contains(':') ? $"[{host}]" : host;

    private static async Task<int?> DefaultHttpProbe(Uri uri, TimeSpan timeout, CancellationToken ct)
    {
        // A fresh handler per probe: pooling to a container that is about to be replaced keeps a
        // socket open to an address the next release will reuse for something else.
        using var handler = new SocketsHttpHandler { ConnectTimeout = timeout };
        using var client = new HttpClient(handler) { Timeout = timeout };

        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            return (int)response.StatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<bool> DefaultTcpProbe(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await client.ConnectAsync(host, port, timeoutSource.Token);
            return true;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
