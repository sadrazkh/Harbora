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
    /// Reserves a port for this deployment, or returns the one it already holds — a retried deploy
    /// must not consume a second port.
    /// </summary>
    public async Task<int> AllocateAsync(Guid serverId, Guid appId, int deploymentNumber, CancellationToken ct) =>
        (await AllocatePairAsync(serverId, appId, deploymentNumber, ct)).NodePort;

    /// <summary>
    /// The reservation for this deployment, including the panel-side ingress port when one was
    /// recorded.
    ///
    /// <para>
    /// Separate from <see cref="AllocateAsync"/> only because most callers want the node's port and
    /// nothing else. The pair travels together: the panel binds its listener from
    /// <see cref="HostPortAllocation.IngressPort"/> after a restart, and a listener whose number
    /// moved would leave every route naming the old one pointing at nothing.
    /// </para>
    /// </summary>
    public async Task<PortReservation> AllocatePairAsync(
        Guid serverId, Guid appId, int deploymentNumber, CancellationToken ct)
    {
        var held = await db.HostPortAllocations
            .FirstOrDefaultAsync(a => a.ServerId == serverId && a.AppId == appId
                                      && a.DeploymentNumber == deploymentNumber, ct);
        if (held is not null) return new PortReservation(held.Port, held.IngressPort);

        var port = await ReserveAsync(serverId, appId, deploymentNumber, ct);
        return new PortReservation(port, null);
    }

    /// <summary>
    /// Record the panel port bound for this deployment, so a restart can bind the same one again.
    /// </summary>
    public async Task RecordIngressPortAsync(
        Guid serverId, Guid appId, int deploymentNumber, int ingressPort, CancellationToken ct)
    {
        var row = await db.HostPortAllocations
            .FirstOrDefaultAsync(a => a.ServerId == serverId && a.AppId == appId
                                      && a.DeploymentNumber == deploymentNumber, ct);

        if (row is null || row.IngressPort == ingressPort) return;

        row.IngressPort = ingressPort;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Every reservation that named a panel ingress port, for rebinding at startup.</summary>
    public async Task<IReadOnlyList<HostPortAllocation>> IngressReservationsAsync(CancellationToken ct) =>
        await db.HostPortAllocations.AsNoTracking()
            .Where(a => a.IngressPort != null)
            .ToListAsync(ct);

    private async Task<int> ReserveAsync(Guid serverId, Guid appId, int deploymentNumber, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var taken = await db.HostPortAllocations.Where(a => a.ServerId == serverId)
                .Select(a => a.Port).ToListAsync(ct);

            if (HostPortRange.NextFree(taken) is not { } port)
                throw new InvalidOperationException(
                    $"Every host port between {HostPortRange.First} and {HostPortRange.Last} is in use on " +
                    "this node. Remove unused apps, or add another node.");

            db.HostPortAllocations.Add(new HostPortAllocation
            {
                ServerId = serverId, AppId = appId, DeploymentNumber = deploymentNumber, Port = port
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

    /// <summary>Frees one reservation — used when a deployment fails, so a failed deploy leaks nothing.</summary>
    public async Task ReleaseAsync(Guid serverId, Guid appId, int deploymentNumber, CancellationToken ct) =>
        await RemoveAsync(a => a.ServerId == serverId && a.AppId == appId
                               && a.DeploymentNumber == deploymentNumber, ct);

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
