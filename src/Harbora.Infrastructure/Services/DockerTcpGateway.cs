using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Services;
using Harbora.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Services;

/// <summary>Where a client connects for one grant.</summary>
public sealed record GatewayEndpoint(string ContainerName, string Host, int Port);

/// <summary>
/// Opens and closes the public endpoint for an external database grant.
///
/// A proxy container per grant, publishing one port and forwarding to the service on its private
/// network. The database container is never published: it keeps no route of its own to the
/// internet, and closing a grant removes one small container rather than reconfiguring the thing
/// holding the customer's data.
///
/// This replaces the fake tunnel the node contract stood in for. It needs no node agent, because on
/// a single-server install the control plane already talks to the same Docker daemon the databases
/// run on — which was true the whole time the feature was documented as blocked.
/// </summary>
public sealed class DockerTcpGateway(
    HarboraDbContext db,
    IDockerEngine docker,
    ILogger<DockerTcpGateway> logger)
{
    /// <summary>
    /// Reserves a port, starts the proxy and returns where to connect — or null with a reason.
    /// </summary>
    public async Task<(GatewayEndpoint? Endpoint, string? Error)> OpenAsync(
        DatabaseAccessGrant grant, ManagedService service, string networkName, CancellationToken ct)
    {
        var config = TcpGatewayPlan.Config(service.ContainerName, service.InternalPort, grant.AllowedIps);
        if (config is null)
            return (null, "One of the allowed addresses could not be read. Nothing was opened.");

        // Ports in use by grants that are still open. Read unfiltered because the sweeper closing a
        // grant has no session, and a filtered read would report every port free and hand the same
        // number to two databases.
        var taken = await db.DatabaseAccessGrants.IgnoreQueryFilters()
            .Where(g => g.Id != grant.Id && g.GatewayPort != null
                        && (g.Status == DatabaseAccessStatus.Active || g.Status == DatabaseAccessStatus.Pending))
            .Select(g => g.GatewayPort!.Value)
            .ToListAsync(ct);

        if (TcpGatewayPlan.NextPort(taken) is not { } port)
            return (null, "Every external access port is in use. Close one before opening another.");

        var rootDomain = await db.Settings.IgnoreQueryFilters()
            .Where(s => s.Key == SettingKeys.PlatformRootDomain)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);

        var containerName = TcpGatewayPlan.ContainerName(grant.Id);

        try
        {
            // RunContainerAsync does not pull — every other caller pulls first, and this one did
            // not, so the very first grant on a fresh host failed with "No such image" after the
            // login had already been created. Idempotent, so the second grant costs nothing.
            await docker.PullImageAsync(TcpGatewayPlan.Image,
                new Progress<string>(line => logger.LogDebug("gateway image: {Line}", line)), ct);

            await docker.RunContainerAsync(new DockerRunRequest(
                Image: TcpGatewayPlan.Image,
                ContainerName: containerName,
                NetworkName: networkName,
                Env: new Dictionary<string, string> { [TcpGatewayPlan.ConfigVariable] = config },
                Labels: new Dictionary<string, string>
                {
                    ["harbora.role"] = "db-gateway",
                    ["harbora.grant"] = grant.Id.ToString(),
                    ["harbora.service"] = service.Id.ToString()
                },
                Volumes: [],
                ContainerPort: TcpGatewayPlan.ListenPort,
                MemoryLimitBytes: 64L * 1024 * 1024,
                CpuLimit: 0.25,
                HealthCheckPath: null,
                Command: TcpGatewayPlan.Entrypoint(),
                PublishToHostPort: port), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not start the database gateway for grant {Grant}.", grant.Id);

            // Removed rather than left running-but-unrecorded: a proxy nobody has a row for is a
            // published port nobody can find to close.
            await SafeRemoveAsync(containerName, ct);
            return (null, "The connection endpoint could not be opened. Nothing was left running.");
        }

        var host = TcpGatewayPlan.HostFor(rootDomain, service.Name, null);
        return (new GatewayEndpoint(containerName, host, port), null);
    }

    /// <summary>
    /// Closes the endpoint. Safe to call for a grant that never opened one, and safe to call twice —
    /// the sweeper and somebody pressing revoke can race.
    /// </summary>
    public Task CloseAsync(DatabaseAccessGrant grant, CancellationToken ct) =>
        SafeRemoveAsync(grant.TunnelId ?? TcpGatewayPlan.ContainerName(grant.Id), ct);

    private async Task SafeRemoveAsync(string containerName, CancellationToken ct)
    {
        try { await docker.RemoveContainerAsync(containerName, force: true, ct); }
        catch (Docker.DotNet.DockerContainerNotFoundException)
        {
            // Already gone is the outcome this wanted. Logging it as a failure — which it did —
            // raises an alarm about an open port on a grant that never opened one, and an alarm
            // that cries wolf is how the real one gets ignored.
        }
        catch (Exception ex)
        {
            // Anything else is loud. A gateway that will not go away is a port still open on a
            // grant the panel believes it has closed, which is the one failure this feature must
            // not have quietly.
            logger.LogError(ex, "The database gateway {Container} could not be removed.", containerName);
        }
    }
}
