using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Nodes;

/// <summary>
/// Answers "what is this machine" from the node row rather than by asking the node.
///
/// <para>
/// The node already reports its inventory on every connect and its pressure on every heartbeat, so
/// a round trip here would buy nothing but latency — and would fail for an offline node, where the
/// last known facts are exactly what a panel wants to show.
/// </para>
/// </summary>
public sealed class NodeHostFacts(HarboraDbContext db)
{
    public async Task<HostInfo?> ForAsync(string nodeId, CancellationToken ct)
    {
        // IgnoreQueryFilters: the deployment pipeline runs as background work with no session, and
        // a filtered read would report every node as missing.
        var node = await db.Nodes.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(n => n.NodeId == nodeId, ct);

        if (node is null) return null;

        return new HostInfo(
            node.CpuCores,
            node.TotalMemoryBytes,
            node.TotalDiskBytes,
            node.FreeDiskBytes,
            node.ContainerRuntimeVersion,
            node.RunningWorkloads);
    }

    /// <summary>The node backing a scheduling target, or null when that server is not a v1 node.</summary>
    public async Task<Node?> ForServerAsync(Guid serverId, CancellationToken ct) =>
        await db.Nodes.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(n => n.ServerId == serverId, ct);
}
