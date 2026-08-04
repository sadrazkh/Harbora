using Harbora.Data;
using Harbora.Domain.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Nodes;

/// <summary>
/// Marks nodes offline when they stop heartbeating.
///
/// <para>
/// A node's own disconnect handler covers the tidy case. This covers the untidy one: a machine that
/// lost power, or whose network vanished, leaves a socket that looks open from here and a node row
/// that says Online forever. The scheduler would keep placing work on a server that is not there.
/// </para>
/// </summary>
public sealed class NodeHeartbeatMonitor(
    IServiceScopeFactory scopeFactory,
    NodeChannelRegistry registry,
    IOptions<NodeAgentControlPlaneOptions> options,
    TimeProvider clock,
    ILogger<NodeHeartbeatMonitor> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                // Never fatal: a sweep that throws must not stop the next one, or a node that went
                // away once would read as online forever.
                log.LogError(e, "Node heartbeat sweep failed; retrying in {Interval}.", Interval);
            }
        }
    }

    /// <summary>One pass. Returns how many nodes were marked offline.</summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

        var heartbeat = TimeSpan.FromSeconds(options.Value.HeartbeatIntervalSeconds);
        var now = clock.GetUtcNow();

        // IgnoreQueryFilters: a sweeper has no session, and a filtered read here would find nothing
        // and report a clean sweep while every stale node stayed marked online.
        var live = await db.Nodes.IgnoreQueryFilters()
            .Where(n => n.Status == NodeStatus.Online || n.Status == NodeStatus.Draining)
            .ToListAsync(ct);

        var marked = 0;

        foreach (var node in live)
        {
            // A node connected to this instance is online whatever its last heartbeat says: the
            // socket is the stronger signal, and a slow heartbeat is not a missing node.
            if (registry.IsConnected(node.NodeId)) continue;

            if (!node.IsStale(now, heartbeat)) continue;

            node.Status = NodeStatus.Offline;
            node.Health = "unknown";
            node.DisconnectedAt ??= now;
            marked++;

            log.LogWarning(
                "Node {NodeId} ({Name}) has not been heard from since {Last:u}; marking it offline.",
                node.NodeId, node.Name, node.LastHeartbeatAt);
        }

        if (marked > 0) await db.SaveChangesAsync(ct);

        return marked;
    }
}
