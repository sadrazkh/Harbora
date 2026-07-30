using Harbora.Data;
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
public sealed class HostPortAllocator(HarboraDbContext db, ILogger<HostPortAllocator> logger)
{
    /// <summary>Concurrent deploys race for the lowest free port; each loss removes one candidate.</summary>
    private const int MaxAttempts = 10;

    /// <summary>
    /// Reserves a port for this deployment, or returns the one it already holds — a retried deploy
    /// must not consume a second port.
    /// </summary>
    public async Task<int> AllocateAsync(Guid serverId, Guid appId, int deploymentNumber, CancellationToken ct)
    {
        var existing = await db.HostPortAllocations
            .FirstOrDefaultAsync(a => a.ServerId == serverId && a.AppId == appId
                                      && a.DeploymentNumber == deploymentNumber, ct);
        if (existing is not null) return existing.Port;

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
        var stale = await db.HostPortAllocations
            .Where(a => a.ServerId == serverId && a.AppId == appId && a.DeploymentNumber != keepDeploymentNumber)
            .ToListAsync(ct);
        if (stale.Count == 0) return;

        db.HostPortAllocations.RemoveRange(stale);
        await db.SaveChangesAsync(ct);
        logger.LogDebug("Released {Count} host port(s) for app {App}.", stale.Count, appId);
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

        db.HostPortAllocations.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
    }
}
