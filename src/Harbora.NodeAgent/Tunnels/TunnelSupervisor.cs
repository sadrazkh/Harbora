using System.Collections.Concurrent;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Identity;
using Harbora.NodeAgent.Observability;
using Harbora.NodeAgent.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.NodeAgent.Tunnels;

/// <summary>
/// Keeps a tunnel up for every active grant, and takes it down the moment the grant ends.
///
/// <para>
/// Reconnection uses the same backoff-with-jitter as the control channel, for the same reason: a
/// gateway restart drops every tunnel on every node at once. Where it differs is what a failure
/// means — a tunnel that will not come back leaves a customer without database access, but it does
/// not leave anything exposed, so retrying forever is the safe default.
/// </para>
/// </summary>
public sealed class TunnelSupervisor(
    IOptions<NodeAgentOptions> options,
    ITunnelConnectionFactory connections,
    ILocalDialer dialer,
    NodeMetrics metrics,
    TimeProvider clock,
    ILoggerFactory loggerFactory,
    ILogger<TunnelSupervisor> log)
{
    private readonly ConcurrentDictionary<string, Running> _tunnels = new(StringComparer.Ordinal);
    private readonly ReconnectPolicy _reconnect = new(options.Value.Reconnect);

    private sealed record Running(GatewayTunnel Tunnel, CancellationTokenSource Cancellation, Task Loop);

    public int ActiveCount => _tunnels.Count(t => t.Value.Tunnel.State.Status == TunnelStatus.Connected);

    public TunnelState? StateFor(string key) =>
        _tunnels.TryGetValue(key, out var running) ? running.Tunnel.State : null;

    public IReadOnlyList<TunnelState> All() => _tunnels.Values.Select(t => t.Tunnel.State).ToList();

    /// <summary>
    /// Every tunnel under the key both ends name it with — a grant id, or <c>ingress</c>. The state
    /// alone cannot be matched across two observations: an ingress tunnel's <c>TunnelId</c> is
    /// derived from the node id, and a grant's is derived from the grant, so the key is the only
    /// name that is stable and unique for both.
    /// </summary>
    public IReadOnlyDictionary<string, TunnelState> ByKey() =>
        _tunnels.ToDictionary(t => t.Key, t => t.Value.Tunnel.State, StringComparer.Ordinal);

    /// <summary>Whether the node's single ingress tunnel is up right now.</summary>
    public bool IngressConnected =>
        StateFor(TunnelRegistration.IngressKey) is { Status: TunnelStatus.Connected };

    /// <summary>
    /// Start a tunnel for a grant and wait until it is published or has failed.
    ///
    /// <para>
    /// Waiting matters: the caller has to return a public endpoint to the control plane, and an
    /// endpoint reported before the gateway allocated it would be a lie the customer then tries to
    /// connect to.
    /// </para>
    /// </summary>
    public async Task<TunnelState> StartAsync(
        string gatewayUrl, NodeIdentity identity, TunnelRegistration registration, ITunnelTargetResolver targets,
        TimeSpan readyTimeout, CancellationToken ct)
    {
        await StopAsync(registration.Key);

        var gateway = ParseGateway(gatewayUrl);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tunnel = new GatewayTunnel(connections, dialer, clock, loggerFactory.CreateLogger<GatewayTunnel>());

        var loop = Task.Run(() => SuperviseAsync(tunnel, gateway, identity, registration, targets, cancellation.Token), CancellationToken.None);

        _tunnels[registration.Key] = new Running(tunnel, cancellation, loop);

        var deadline = clock.GetUtcNow() + readyTimeout;

        while (clock.GetUtcNow() < deadline)
        {
            var state = tunnel.State;

            if (state is { Status: TunnelStatus.Connected }) { Report(); return state; }
            if (state is { Status: TunnelStatus.Failed }) { Report(); return state; }

            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }

        Report();

        return tunnel.State with
        {
            LastError = NodeError.From(NodeErrorCode.TunnelUnavailable,
                $"The gateway did not publish the tunnel within {readyTimeout.TotalSeconds:0}s.", retryable: true),
        };
    }

    /// <summary>Close a tunnel by its key. Returns quietly when there is not one.</summary>
    public async Task StopAsync(string key)
    {
        if (!_tunnels.TryRemove(key, out var running)) return;

        await running.Cancellation.CancelAsync();

        // Bounded: a tunnel stuck on a socket must not hold up a revocation.
        await running.Loop.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
            .ContinueWith(_ => { }, TaskScheduler.Default);

        await running.Tunnel.DisposeAsync();
        running.Cancellation.Dispose();

        Report();
        log.LogInformation("Tunnel {Key} closed.", key);
    }

    public async Task StopAllAsync()
    {
        foreach (var key in _tunnels.Keys.ToList()) await StopAsync(key);
    }

    private async Task SuperviseAsync(
        GatewayTunnel tunnel, Uri gateway, NodeIdentity identity,
        TunnelRegistration registration, ITunnelTargetResolver targets, CancellationToken ct)
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            var delay = _reconnect.Delay(++attempt);

            if (delay > TimeSpan.Zero)
            {
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { return; }
            }

            try
            {
                await tunnel.RunAsync(gateway, identity, registration, targets, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // This loop runs detached, so anything that escapes here is never observed by
                // anyone: the tunnel would stop retrying and go on reporting whatever state it was
                // last in. Recording it and looping is what makes "retrying forever is the safe
                // default" true rather than aspirational.
                log.LogError(e, "Tunnel {Key} failed unexpectedly; retrying.", registration.Key);
            }

            if (ct.IsCancellationRequested) return;

            // A gateway that refuses the registration is answering about the grant, not about the
            // network. Retrying would hammer it with a request it has already declined.
            if (tunnel.State.LastError?.Code == NodeErrorCode.TunnelRejected)
            {
                log.LogWarning("Gateway rejected tunnel {Key}; not retrying.", registration.Key);
                return;
            }

            if (tunnel.State.Status == TunnelStatus.Connected) attempt = 0;

            Report();
        }
    }

    private void Report()
    {
        metrics.ActiveTunnels(ActiveCount);

        foreach (var status in Enum.GetValues<TunnelStatus>())
            metrics.TunnelStatus(status, _tunnels.Count(t => t.Value.Tunnel.State.Status == status));
    }

    /// <summary>
    /// The gateway URL may arrive as <c>host:port</c> or as a URI. Normalising here rather than at
    /// each call site keeps a control plane's formatting choice from becoming a connection bug.
    /// </summary>
    internal static Uri ParseGateway(string gatewayUrl)
    {
        if (Uri.TryCreate(gatewayUrl, UriKind.Absolute, out var absolute) && absolute.Port > 0)
            return absolute;

        var parts = gatewayUrl.Split(':');

        if (parts.Length == 2 && int.TryParse(parts[1], out var port))
            return new Uri($"tcp://{parts[0]}:{port}");

        throw new ArgumentException($"'{gatewayUrl}' is not a usable TCP gateway address.", nameof(gatewayUrl));
    }
}
