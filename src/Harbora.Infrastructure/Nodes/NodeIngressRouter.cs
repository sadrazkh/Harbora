using Harbora.Data;
using Harbora.Domain.Nodes;
using Harbora.Domain.Servers;
using Harbora.Infrastructure.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Nodes;

/// <summary>Where the proxy should send this app's traffic, and whether it goes through a tunnel.</summary>
public sealed record UpstreamTarget(string Host, int Port, bool Tunnelled);

/// <summary>
/// Decides how the panel's proxy reaches an app on a remote server.
///
/// <para>
/// Two answers. On a node the panel can dial, the proxy goes straight to the machine at the port the
/// container publishes — one hop, no panel in the middle. On a node behind NAT nothing outside can
/// open that socket, so the proxy goes to a port the panel bound for exactly this deployment, and
/// the bytes travel back down the tunnel the node already dialled out.
/// </para>
///
/// <para>
/// The panel-side port is reserved with the node-side one and recorded next to it, because a restart
/// has to bind the same number again: the routes naming it are configuration for apps that never
/// stopped running, and nothing rewrites them.
/// </para>
/// </summary>
public sealed class NodeIngressRouter(
    HarboraDbContext db,
    NodeIngressRegistry registry,
    HostPortAllocator hostPorts,
    IOptions<NodeAgentControlPlaneOptions> nodeOptions,
    IOptions<HarboraRuntimeOptions> runtimeOptions,
    ILogger<NodeIngressRouter> log)
{
    private readonly NodeAgentControlPlaneOptions _node = nodeOptions.Value;
    private readonly HarboraRuntimeOptions _runtime = runtimeOptions.Value;

    public Task<UpstreamTarget> ResolveAsync(
        Server server, Guid appId, int deploymentNumber, int nodePort, CancellationToken ct) =>
        ResolveAsync(server, appId, deploymentNumber, replicaIndex: 0, nodePort, ct);

    /// <summary>Replica form — see <see cref="HostPortAllocation.ReplicaIndex"/> for what 0 means.</summary>
    public async Task<UpstreamTarget> ResolveAsync(
        Server server, Guid appId, int deploymentNumber, int replicaIndex, int nodePort, CancellationToken ct)
    {
        var direct = new UpstreamTarget(server.Hostname, nodePort, Tunnelled: false);

        // IgnoreQueryFilters: deployments run as background work with no session, and a filtered
        // read would report every node as absent — silently routing a tunnelled node directly.
        var node = await db.Nodes.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(n => n.ServerId == server.Id, ct);

        if (node is null || node.IngressMode != NodeIngressMode.Tunnel) return direct;

        var panelPort = registry.Bind(
            node.NodeId, nodePort,
            (await hostPorts.AllocatePairAsync(server.Id, appId, deploymentNumber, replicaIndex, ct)).IngressPort);

        await hostPorts.RecordIngressPortAsync(server.Id, appId, deploymentNumber, replicaIndex, panelPort, ct);

        if (!registry.IsConnected(node.NodeId))
            // Not fatal here: the health check that follows goes through this same path and will
            // fail the deploy properly, with a diagnosis, rather than on a guess made this early.
            log.LogWarning(
                "Node {NodeId} routes through its ingress tunnel, and that tunnel is not connected. " +
                "The health check for this deployment is about to fail.",
                node.NodeId);

        return new UpstreamTarget(IngressHost, panelPort, Tunnelled: true);
    }

    /// <summary>
    /// What Traefik should target. The panel's own container name by default, which is how the proxy
    /// already reaches the panel for health probes — so no new networking has to be arranged.
    /// </summary>
    public string IngressHost =>
        _node.IngressHost is { Length: > 0 } configured ? configured : _runtime.PanelContainerName;
}
