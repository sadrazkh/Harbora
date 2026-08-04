using Harbora.Data;
using Harbora.Domain.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Nodes;

/// <summary>
/// Rebinds every ingress listener the panel was holding before it restarted.
///
/// <para>
/// Traefik's routes name a panel port. Nothing rewrites them on a restart — they are configuration
/// for apps that are still deployed and still running — so a panel that came back without rebinding
/// those exact ports would leave every site on a tunnelled node down until somebody redeployed it.
/// The reservations are in the database precisely so this can be done from them.
/// </para>
///
/// <para>
/// Runs before any node has reconnected, which is fine and deliberate: a bound listener with no
/// tunnel behind it refuses connections, and a proxy reads a refusal as "upstream is down" and says
/// so. The alternative — waiting for the tunnel — would mean a window where Traefik points at a port
/// nothing has bound, which looks the same to the user and is harder to diagnose.
/// </para>
/// </summary>
public sealed class NodeIngressRebinder(
    IServiceScopeFactory scopeFactory,
    NodeIngressRegistry registry,
    ILogger<NodeIngressRebinder> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

            // IgnoreQueryFilters throughout: this runs at startup with no session, and a filtered
            // read would find nothing and silently rebind nothing at all.
            var reservations = await db.HostPortAllocations.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.IngressPort != null)
                .ToListAsync(ct);

            if (reservations.Count == 0) return;

            var nodes = await db.Nodes.IgnoreQueryFilters().AsNoTracking()
                .Where(n => n.ServerId != null)
                .ToDictionaryAsync(n => n.ServerId!.Value, n => n, ct);

            var bound = 0;

            foreach (var reservation in reservations)
            {
                if (!nodes.TryGetValue(reservation.ServerId, out var node))
                {
                    log.LogWarning(
                        "Ingress port {Port} was reserved for server {ServerId}, which no node backs any more; leaving it unbound.",
                        reservation.IngressPort, reservation.ServerId);
                    continue;
                }

                if (node.IngressMode != NodeIngressMode.Tunnel)
                {
                    // Somebody moved the node back to direct routing while the panel was down. The
                    // reservation is stale; the next deploy will rewrite the route.
                    log.LogInformation(
                        "Not rebinding ingress port {Port}: node {NodeId} is on direct routing now.",
                        reservation.IngressPort, node.NodeId);
                    continue;
                }

                try
                {
                    registry.Bind(node.NodeId, reservation.Port, reservation.IngressPort);
                    bound++;
                }
                catch (Exception e) when (e is InvalidOperationException or System.Net.Sockets.SocketException)
                {
                    log.LogError(e,
                        "Could not rebind ingress port {Port} for node {NodeId}; its apps stay unreachable until redeployed.",
                        reservation.IngressPort, node.NodeId);
                }
            }

            log.LogInformation("Rebound {Bound} of {Total} ingress listener(s) after restart.", bound, reservations.Count);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Never fatal. A panel that cannot rebind is a panel with some sites down; a panel that
            // refuses to start over it is a panel with everything down.
            log.LogError(e, "Ingress listeners could not be rebound; apps on tunnelled nodes may be unreachable.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
