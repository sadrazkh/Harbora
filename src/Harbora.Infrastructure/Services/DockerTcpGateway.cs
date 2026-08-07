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
///
/// <para>
/// That single-server assumption is the constraint <see cref="DatabaseAccessService.CanOpenLocally"/>
/// is built around, and it is stated here now rather than assumed. The proxy publishes its port on
/// the panel's machine and reaches the database over a private network that only exists there, and
/// the address handed back is built from the panel's own root domain. Asked about a database on
/// another server this used to start a proxy here anyway, forwarding to a container name that
/// resolves to nothing, and return a connection string for it — so it refuses instead, in words.
/// </para>
/// </summary>
public sealed class DockerTcpGateway(
    HarboraDbContext db,
    IServerEngineFactory engines,
    ILogger<DockerTcpGateway> logger)
{
    /// <summary>
    /// Reserves a port, starts the proxy and returns where to connect — or null with a reason.
    /// </summary>
    public async Task<(GatewayEndpoint? Endpoint, string? Error)> OpenAsync(
        DatabaseAccessGrant grant, ManagedService service, string networkName, CancellationToken ct)
    {
        // Asked first, and answered as a refusal rather than an exception: by the time this is
        // called the login already exists on the database, and IssueAsync undoes it on a refusal.
        // A throw here would leave an account behind that nothing has a row for.
        IDockerEngine docker;
        try
        {
            docker = await engines.ResolveAsync(service.ServerId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not reach the server holding {Service} to open a gateway.", service.Name);
            return (null,
                $"The server holding '{service.Name}' could not be reached, so nothing was opened. {ex.Message}");
        }

        // By reference, and that is a contract rather than a coincidence: IServerEngineFactory.Local
        // is documented as the very instance ResolveAsync returns for the local server, and
        // ServerEngineIdentityTests pins it against the real factory. Without that, a factory handing
        // back a fresh engine for the local server would quietly refuse external access on every
        // single-server install — the only kind this feature currently works on.
        if (!ReferenceEquals(docker, engines.Local))
            return (null,
                $"'{service.Name}' does not run on this panel's own machine. External access publishes " +
                "a port here and forwards over a private network that only exists here, so the endpoint " +
                "would point at nothing. Nothing was opened. Reaching a database on another server is " +
                "not built yet.");

        // Only for PostgreSQL: MySQL speaks first on its own connections, so the same check would
        // reject every client. MariaDB's own TLS is negotiated after that greeting.
        var requireTls = service.TlsEnabled && service.Type == Harbora.Domain.Common.ManagedServiceType.PostgreSql;

        var config = TcpGatewayPlan.Config(
            service.ContainerName, service.InternalPort, grant.AllowedIps, requireTls);
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
    ///
    /// <para>
    /// Always on this machine, deliberately: the proxy container was only ever created here, so
    /// asking a server that may since have been removed — or that never had one — would turn a
    /// revoke into a failure and leave a published port open.
    /// </para>
    /// </summary>
    public Task CloseAsync(DatabaseAccessGrant grant, CancellationToken ct) =>
        SafeRemoveAsync(grant.TunnelId ?? TcpGatewayPlan.ContainerName(grant.Id), ct);

    private async Task SafeRemoveAsync(string containerName, CancellationToken ct)
    {
        try { await engines.Local.RemoveContainerAsync(containerName, force: true, ct); }
        catch (global::Docker.DotNet.DockerContainerNotFoundException)
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
