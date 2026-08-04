using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Identity;
using Harbora.NodeAgent.Runtime;
using Harbora.NodeAgent.State;
using Microsoft.Extensions.Logging;

namespace Harbora.NodeAgent.Tunnels;

/// <summary>
/// The node's single ingress tunnel: the path the control plane's proxy uses to reach apps on a
/// node it cannot dial.
///
/// <para>
/// A node's published host ports are bound on its own machine. On a routable fleet that is enough —
/// the panel opens a socket to <c>node:32017</c> and the route works. Behind NAT nothing outside can
/// open that socket, so a deploy succeeds, the container is healthy, and every request to the site
/// times out. This reverses the direction: the node dials the gateway, exactly as it already does to
/// publish a database, and the panel binds an internal port at its end.
/// </para>
///
/// <para>
/// One tunnel, not one per app. The gateway names the port on each <c>open</c>, so a new deployment
/// needs no new connection and no new registration — which matters, because a node with twenty apps
/// would otherwise hold twenty TLS connections open to say the same thing twenty times.
/// </para>
///
/// <para>
/// Off unless the control plane turns it on. The tunnel puts the panel on the path of every request
/// to every app on this node: right for a node that is otherwise unreachable, wrong for one that is
/// not, and not a choice a node should make for itself.
/// </para>
/// </summary>
public sealed class IngressTunnel(
    TunnelSupervisor tunnels,
    WorkloadRegistry workloads,
    JsonFileStore<NodeState> state,
    NodeIdentityStore identities,
    ILoggerFactory loggerFactory,
    ILogger<IngressTunnel> log)
{
    /// <summary>
    /// Bring the tunnel up or take it down, and remember the choice.
    ///
    /// <para>
    /// State is written before the tunnel is started rather than after: a node that came up, dialled
    /// out and then died before recording why would come back with its apps unreachable and no idea
    /// that anything was meant to be running.
    /// </para>
    /// </summary>
    public async Task<ConfigureIngressResult> ApplyAsync(bool enabled, string? gatewayUrl, CancellationToken ct)
    {
        var current = state.Load() ?? new NodeState();

        var gateway = gatewayUrl ?? current.TunnelGatewayUrl;

        if (enabled && string.IsNullOrWhiteSpace(gateway))
            return new ConfigureIngressResult
            {
                Enabled = false,
                Status = TunnelStatus.Failed,
                PublishedPorts = PublishedPorts(),
                LastError = NodeError.From(
                    NodeErrorCode.ValidationFailed,
                    "This node was not given a tunnel gateway address, so it has nothing to dial. " +
                    "Configure NodeAgent:TunnelGatewayUrl on the control plane, or send one with the command."),
            };

        state.Save(current with
        {
            IngressEnabled = enabled,
            TunnelGatewayUrl = gateway ?? current.TunnelGatewayUrl,
        });

        if (!enabled)
        {
            await tunnels.StopAsync(TunnelRegistration.IngressKey);

            log.LogInformation("Ingress tunnel disabled; this node's apps are reachable only at its own address now.");

            return new ConfigureIngressResult
            {
                Enabled = false,
                Status = TunnelStatus.Closed,
                PublishedPorts = PublishedPorts(),
                Detail = "ingress tunnel closed",
            };
        }

        var result = await StartAsync(gateway!, current.NodeId, ct);

        return new ConfigureIngressResult
        {
            Enabled = true,
            Status = result.Status,
            PublishedPorts = PublishedPorts(),
            Detail = result.Status == TunnelStatus.Connected
                ? $"serving {PublishedPorts().Count} published port(s)"
                : null,
            LastError = result.LastError,
        };
    }

    /// <summary>
    /// Re-open the tunnel at startup if the node was left with it on. Never throws: a node that
    /// cannot reach the gateway must still come up, take commands and report why.
    /// </summary>
    public async Task RestoreAsync(CancellationToken ct)
    {
        var current = state.Load();

        if (current is not { IngressEnabled: true }) return;

        if (string.IsNullOrWhiteSpace(current.TunnelGatewayUrl))
        {
            log.LogWarning(
                "Ingress is enabled on this node but no gateway address survived; apps here are unreachable " +
                "until the control plane re-sends ConfigureIngress.");
            return;
        }

        try
        {
            var restored = await StartAsync(current.TunnelGatewayUrl, current.NodeId, ct);

            log.LogInformation(
                "Restored the ingress tunnel after restart: {Status}, serving {Count} published port(s).",
                restored.Status, PublishedPorts().Count);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The supervisor keeps retrying underneath; this is only about not taking the agent down
            // with a gateway that happens to be restarting at the same moment.
            log.LogWarning(e, "Could not restore the ingress tunnel; the supervisor will keep trying.");
        }
    }

    private async Task<TunnelState> StartAsync(string gateway, string? nodeId, CancellationToken ct)
    {
        var identity = identities.Load()
            ?? throw new InvalidOperationException(
                "This node has no credential to authenticate an ingress tunnel with.");

        var registration = new TunnelRegistration
        {
            NodeId = nodeId ?? string.Empty,
            TunnelId = $"ingress-{nodeId ?? "unenrolled"}",
            Purpose = TunnelPurpose.Ingress,
            // No grant, and no tenant: an ingress tunnel serves whatever this node was told to run,
            // for whichever workspace owns it.
            GrantId = null,
            TenantId = string.Empty,
        };

        return await tunnels.StartAsync(
            gateway, identity, registration,
            new PublishedPortTargetResolver(workloads, loggerFactory.CreateLogger<PublishedPortTargetResolver>()),
            TimeSpan.FromSeconds(30), ct);
    }

    /// <summary>Every host port this node currently publishes — the whole of what the tunnel serves.</summary>
    public IReadOnlyList<int> PublishedPorts() => workloads.AllocatedPorts().Order().ToList();

    public bool Connected => tunnels.IngressConnected;

    public bool Enabled => state.Load() is { IngressEnabled: true };
}
