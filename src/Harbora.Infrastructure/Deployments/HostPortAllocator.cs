using System.Net;
using System.Net.Sockets;
using Harbora.Data;
using Harbora.Infrastructure.Nodes;
using Harbora.Domain.Servers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Hands out and reclaims the host ports remote-node deployments publish on.
///
/// See <see cref="HostPortRange"/> for why a hash was not good enough. The reservation lives in the
/// database so it survives a restart, and a unique index on (server, port) is what actually prevents
/// two concurrent deploys from agreeing on the same number — a check-then-insert cannot.
/// </summary>
public sealed class HostPortAllocator(
    HarboraDbContext db, NodeIngressRegistry ingress, ILogger<HostPortAllocator> logger)
{
    /// <summary>Concurrent deploys race for the lowest free port; each loss removes one candidate.</summary>
    private const int MaxAttempts = 10;

    /// <summary>
    /// Reserves a port for this deployment's given replica, or returns the one it already holds — a
    /// retried deploy must not consume a second port. <paramref name="replicaIndex"/> is 0 for a
    /// deployment running exactly one replica — the ordinary case — and 1-based for replica 2 and
    /// beyond of a deployment running more than one; see <see cref="HostPortAllocation.ReplicaIndex"/>.
    /// </summary>
    public async Task<int> AllocateAsync(
        Guid serverId, Guid appId, int deploymentNumber, int replicaIndex, CancellationToken ct) =>
        (await AllocatePairAsync(serverId, appId, deploymentNumber, replicaIndex, ct)).NodePort;

    /// <summary>
    /// The reservation for this deployment's given replica, including the panel-side ingress port
    /// when one was recorded.
    ///
    /// <para>
    /// Separate from <see cref="AllocateAsync"/> only because most callers want the node's port and
    /// nothing else. The pair travels together: the panel binds its listener from
    /// <see cref="HostPortAllocation.IngressPort"/> after a restart, and a listener whose number
    /// moved would leave every route naming the old one pointing at nothing.
    /// </para>
    /// </summary>
    public async Task<PortReservation> AllocatePairAsync(
        Guid serverId, Guid appId, int deploymentNumber, int replicaIndex, CancellationToken ct)
    {
        var held = await db.HostPortAllocations
            .FirstOrDefaultAsync(a => a.ServerId == serverId && a.AppId == appId
                                      && a.DeploymentNumber == deploymentNumber
                                      && a.ReplicaIndex == replicaIndex, ct);
        if (held is not null) return new PortReservation(held.Port, held.IngressPort);

        var port = await ReserveAsync(serverId, appId, deploymentNumber, replicaIndex, ct);
        return new PortReservation(port, null);
    }

    /// <summary>
    /// Record the panel port bound for this deployment's given replica, so a restart can bind the
    /// same one again.
    /// </summary>
    public async Task RecordIngressPortAsync(
        Guid serverId, Guid appId, int deploymentNumber, int replicaIndex, int ingressPort, CancellationToken ct)
    {
        var row = await db.HostPortAllocations
            .FirstOrDefaultAsync(a => a.ServerId == serverId && a.AppId == appId
                                      && a.DeploymentNumber == deploymentNumber
                                      && a.ReplicaIndex == replicaIndex, ct);

        if (row is null || row.IngressPort == ingressPort) return;

        row.IngressPort = ingressPort;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Every reservation that named a panel ingress port, for rebinding at startup.</summary>
    public async Task<IReadOnlyList<HostPortAllocation>> IngressReservationsAsync(CancellationToken ct) =>
        await db.HostPortAllocations.AsNoTracking()
            .Where(a => a.IngressPort != null)
            .ToListAsync(ct);

    private async Task<int> ReserveAsync(
        Guid serverId, Guid appId, int deploymentNumber, int replicaIndex, CancellationToken ct)
    {
        // P7 (2026-08-17 app-environment-management design), the port-burn item: only meaningful for
        // a LOCAL server, where this process and the one about to publish the port are the same
        // machine — a bind test run here says something true about a port dockerd is about to
        // publish there. For a remote node the panel has no socket on that machine at all, so the
        // probe is skipped and the number is trusted the way it always was; that is the node-side
        // publish the spec says this item is not asking for.
        var isLocal = await db.Servers.AsNoTracking()
            .Where(s => s.Id == serverId).Select(s => (bool?)s.IsLocal).FirstOrDefaultAsync(ct) ?? false;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var taken = await db.HostPortAllocations.Where(a => a.ServerId == serverId)
                .Select(a => a.Port).ToListAsync(ct);

            if (NextViablePort(taken, isLocal) is not { } port)
                throw new InvalidOperationException(
                    $"Every host port between {HostPortRange.First} and {HostPortRange.Last} is in use on " +
                    "this node. Remove unused apps, or add another node.");

            db.HostPortAllocations.Add(new HostPortAllocation
            {
                ServerId = serverId, AppId = appId, DeploymentNumber = deploymentNumber,
                ReplicaIndex = replicaIndex, Port = port
            });

            try
            {
                await db.SaveChangesAsync(ct);
                return port;
            }
            catch (DbUpdateException)
            {
                // Another deploy took it between the read and the insert. The index is the authority;
                // drop the losing entity and look again.
                db.ChangeTracker.Clear();
                logger.LogDebug("Host port {Port} was taken concurrently on {Server}; retrying.", port, serverId);
            }
        }

        throw new InvalidOperationException(
            "Could not reserve a host port after repeated contention. Retry the deployment.");
    }

    /// <summary>
    /// The lowest port in range that is free per <see cref="HostPortAllocations"/> and, when
    /// <paramref name="probeLocally"/>, actually bindable — the same "try it, catch
    /// <see cref="SocketException"/>, advance" shape <see cref="NodeIngressRegistry.TryBind"/>
    /// already uses for the panel's own tunnelled listeners, applied here instead of trusting the
    /// database alone.
    ///
    /// <para>
    /// This is a live probe, not a persisted burn list, and deliberately so: it re-checks fresh on
    /// every allocation rather than remembering a port was bad last time, which is what makes it
    /// self-healing without a table of its own. The cost is a wasted bind attempt on a still-burned
    /// port every time this runs — a socket syscall, not a failed deploy, which is the trade this
    /// item exists to make. Before this, <c>NextFree</c> alone would hand back the same blocked port
    /// every single time, because nothing here had ever asked the OS.
    /// </para>
    /// </summary>
    private static int? NextViablePort(IReadOnlyCollection<int> taken, bool probeLocally)
    {
        var used = new HashSet<int>(taken);
        for (var port = HostPortRange.First; port <= HostPortRange.Last; port++)
        {
            if (used.Contains(port)) continue;
            if (probeLocally && IsBurned(port)) continue;
            return port;
        }
        return null;
    }

    /// <summary>
    /// Whether something this database does not know about already holds <paramref name="port"/>.
    /// Bind-then-release rather than a long-lived listener — Docker is the one that actually has to
    /// hold the port once a container starts, and this only has to prove the port was free a moment
    /// before that, the same TOCTOU trade every "check, then use" port probe makes.
    /// </summary>
    private static bool IsBurned(int port)
    {
        try
        {
            using var probe = new TcpListener(IPAddress.Any, port);
            probe.Start();
            probe.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    /// <summary>
    /// Frees every port this app holds on the node except the deployment that now serves traffic.
    ///
    /// Called after the cutover, for the same reason old containers are retired only then: releasing
    /// earlier would let another app take a port that is still carrying live traffic.
    /// </summary>
    public async Task ReleaseAllButAsync(Guid serverId, Guid appId, int keepDeploymentNumber, CancellationToken ct)
    {
        var before = await db.HostPortAllocations
            .CountAsync(a => a.ServerId == serverId && a.AppId == appId && a.DeploymentNumber != keepDeploymentNumber, ct);
        if (before == 0) return;

        await RemoveAsync(
            a => a.ServerId == serverId && a.AppId == appId && a.DeploymentNumber != keepDeploymentNumber, ct);

        logger.LogDebug("Released {Count} host port(s) for app {App}.", before, appId);
    }

    /// <summary>
    /// Frees every replica's reservation for one deployment — used when a deployment fails, so a
    /// failed deploy leaks nothing, whether it ever started one container or several.
    /// </summary>
    public async Task ReleaseAsync(Guid serverId, Guid appId, int deploymentNumber, CancellationToken ct) =>
        await RemoveAsync(a => a.ServerId == serverId && a.AppId == appId
                               && a.DeploymentNumber == deploymentNumber, ct);

    /// <summary>
    /// Frees the ports of every replica above <paramref name="keepReplicaCount"/> for one deployment —
    /// used when a running app's replica count is scaled down without a new deployment, so the
    /// containers <see cref="AppOperationsService"/> stops on the way down do not strand the ports they
    /// held.
    /// </summary>
    public async Task ReleaseReplicasAboveAsync(
        Guid serverId, Guid appId, int deploymentNumber, int keepReplicaCount, CancellationToken ct) =>
        await RemoveAsync(a => a.ServerId == serverId && a.AppId == appId
                               && a.DeploymentNumber == deploymentNumber
                               && a.ReplicaIndex > keepReplicaCount, ct);

    /// <summary>Frees everything an app holds — it is being deleted.</summary>
    public async Task ReleaseAppAsync(Guid appId, CancellationToken ct) =>
        await RemoveAsync(a => a.AppId == appId, ct);

    /// <summary>
    /// Loaded and removed rather than deleted in the database: a handful of rows per app, and it keeps
    /// the reservation lifecycle exercisable by the test suite's provider, which has no ExecuteDelete.
    /// </summary>
    private async Task RemoveAsync(
        System.Linq.Expressions.Expression<Func<HostPortAllocation, bool>> match, CancellationToken ct)
    {
        var rows = await db.HostPortAllocations.Where(match).ToListAsync(ct);
        if (rows.Count == 0) return;

        // Unbound here, with the row, because they are one reservation. Releasing the number without
        // closing the listener would leave the panel accepting requests for a container that is
        // gone, and closing it without releasing the number would leak it.
        foreach (var released in rows.Where(r => r.IngressPort is not null))
            ingress.Release(released.IngressPort!.Value);

        db.HostPortAllocations.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>What a deployment holds: the node's published port, and the panel's, when tunnelled.</summary>
public readonly record struct PortReservation(int NodePort, int? IngressPort);
