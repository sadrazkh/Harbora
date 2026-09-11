using Harbora.Application.Abstractions;
using Harbora.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Harbora.Domain.Networking;
using Harbora.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Services;

/// <summary>What a caller needs to open the tool, or why it cannot be opened.</summary>
/// <param name="Url">Where to go. Null on refusal.</param>
/// <param name="User">The generated username, shown once.</param>
/// <param name="Password">The generated password, shown once — never stored in the clear.</param>
/// <param name="Refusal">Why not, in words a person can act on.</param>
public sealed record AdminerResult(string? Url, string? User, string? Password, string? Refusal)
{
    public bool Ok => Url is not null;
}

/// <summary>
/// A throwaway web interface onto one database, for the times a person needs to look at a table.
///
/// Until now the only ways in were "open a port to the internet" or "have a client installed", so
/// the honest answer to "let me just check a row" was one of two bad ones.
///
/// The shape of the thing is the security argument:
/// <list type="bullet">
/// <item>It runs on the database's own private network, so it reaches that database and nothing
/// else — the same isolation every service already has.</item>
/// <item>It is published through Traefik behind basic-auth with a password generated per session
/// and shown once. Unauthenticated, the route is a 401, not a login form onto somebody's data.</item>
/// <item>It stops itself. A sweeper removes the container and the route after an hour, so somebody
/// closing the tab does not leave a standing exposure.</item>
/// <item>The database password is not pre-filled. Adminer's own form asks for it, and the operator
/// takes it from the connection panel where every other secret is revealed on demand.</item>
/// </list>
///
/// <para>
/// HARBORA-0059: this used to take an <c>IDockerEngine</c> directly — always this panel's own daemon
/// — so a database on another server got a session started here, on a network of that name which
/// does not exist on this machine, and a route naming a container this panel's Docker has never
/// heard of. Resolved through <see cref="IServerEngineFactory"/> now, the same seam
/// <see cref="DockerTcpGateway"/> uses for the same reason. And the seam buys the same refusal that
/// class already needed: Traefik reaches this container by name on this panel's own Docker networks,
/// exactly the way it reaches every other route, so a database on another machine cannot be
/// published this way at all yet — carrying the tool's traffic to another server needs a route
/// Harbora does not have. Refused before anything starts, rather than left running somewhere nothing
/// can reach it.
/// </para>
/// </summary>
public sealed class AdminerService(
    HarboraDbContext db,
    IServerEngineFactory engines,
    IProxyEngine proxy,
    ManagedServiceEngine services,
    ISecretProtector protector,
    ISystemClock clock,
    Microsoft.Extensions.Options.IOptions<Deployments.HarboraRuntimeOptions> options,
    ILogger<AdminerService> logger)
{
    public async Task<AdminerResult> OpenAsync(Guid serviceId, CancellationToken ct)
    {
        var service = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == serviceId, ct);
        if (service is null) return new(null, null, null, "That database no longer exists.");

        if (AdminerSession.DriverFor(service.Type) is not { } driver)
            return new(null, null, null, $"There is no web admin tool for {service.Type}.");

        var rootDomain = options.Value.RootDomain;
        if (string.IsNullOrWhiteSpace(rootDomain))
            return new(null, null, null, "The platform has no base domain, so the tool cannot be published.");

        // Resolved before anything is touched, and refused by name rather than attempted: a server
        // this cannot reach at all (no agent endpoint, no enrolled node, a revoked one) must not read
        // as "nothing happened" the way an unhandled exception would.
        IDockerEngine docker;
        try
        {
            docker = await engines.ResolveAsync(service.ServerId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not reach the server holding {Service} to open the admin tool.", service.Name);
            return new(null, null, null,
                $"The server holding '{service.Name}' could not be reached, so nothing was started. {ex.Message}");
        }

        // By reference against IServerEngineFactory.Local, the same contract DockerTcpGateway relies
        // on: this panel's proxy addresses a route's target by container name on its own Docker
        // networks, which only ever resolves to something on this machine. A database on another
        // server would get a route naming a container nothing here has ever heard of — a 502 with no
        // sign of why — so this is refused before a container is even started, not discovered after.
        if (!ReferenceEquals(docker, engines.Local))
            return new(null, null, null,
                $"'{service.Name}' does not run on this panel's own machine. The admin tool is " +
                "published through this panel's own proxy, which reaches a container by name on its " +
                "own Docker networks — a container on another server is not one of them, so the route " +
                "would point at nothing. Nothing was started. Reaching a database on another server " +
                "needs a published route Harbora does not have yet for this tool.");

        var host = $"admin-{service.Id:N}"[..Math.Min(24, 6 + 32)] + "." + rootDomain;
        var container = AdminerSession.ContainerName(service.Id);
        var network = await services.NetworkForAsync(service, ct);

        // A fresh credential every time it is opened. Rotating on reopen means a password read off
        // a screen an hour ago is already dead.
        var user = "admin";
        var password = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));

        try
        {
            // Idempotent: a second click replaces the session rather than colliding with it.
            await docker.RemoveContainerAsync(container, force: true, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException) { /* nothing to remove */ }

        try
        {
            await docker.RunContainerAsync(new DockerRunRequest(
                Image: AdminerSession.Image,
                ContainerName: container,
                NetworkName: network,
                Env: new Dictionary<string, string>
                {
                    // Pre-selects the driver and the host; the password is deliberately not here.
                    ["ADMINER_DEFAULT_SERVER"] = service.ContainerName,
                    ["ADMINER_DESIGN"] = "dracula"
                },
                Labels: new Dictionary<string, string>
                {
                    ["harbora.adminer"] = service.Id.ToString(),
                    ["harbora.adminer.started"] = clock.UtcNow.ToUnixTimeSeconds().ToString()
                },
                Volumes: [],
                ContainerPort: 8080,
                MemoryLimitBytes: 128L * 1024 * 1024,
                CpuLimit: 0.25,
                HealthCheckPath: null), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Could not start the admin tool for {Service}.", service.Name);
            return new(null, null, null, "The admin tool could not be started: " + e.Message);
        }

        var route = new Route
        {
            WorkspaceId = service.WorkspaceId,
            Host = host,
            TargetService = container,
            TargetPort = 8080,
            SslEnabled = true,
            RedirectHttpToHttps = true,
            BasicAuthEnabled = true,
            BasicAuthUsersEncrypted = protector.Protect(Proxy.Htpasswd.Line(user, password)),
            IsEnabled = true
        };
        db.Routes.Add(route);

        // Neither `SaveChangesAsync` nor `ApplyAllAsync` below is more of the setup than the other —
        // together they are the one step that can leave the row and the container disagreeing about
        // whether a session exists, and `ct` here is a WEB REQUEST's token, cancelled the instant the
        // operator navigates away. That can land inside the save, or after it has already committed
        // and while the apply is still being awaited, and the two look identical from here: either
        // way the container above is already running and the route may already be a real row, not
        // just a tracked one, so this is undone exactly like a returned failure is below rather than
        // left for `OperationCanceledException` to carry the enabled row out into the caller.
        ProxyApplyResult applied;
        try
        {
            await db.SaveChangesAsync(ct);
            applied = await proxy.ApplyAllAsync(service.WorkspaceId, ct);
        }
        catch (OperationCanceledException)
        {
            await WithdrawAsync(route, container, service.WorkspaceId, docker);
            throw;
        }

        if (!applied.Success)
        {
            // Something in this workspace's routing did not take — this row, or another one the
            // engine left out of the render, which is a refusal this workspace still owns. Either
            // way the honest answer is to take the session back rather than hand somebody a URL to
            // find out with, and then to publish again: the config may well have been written with
            // this route in it, and a router naming a container that is being removed here is a
            // 502 waiting for whoever else applies next.
            await WithdrawAsync(route, container, service.WorkspaceId, docker);
            return new(null, null, null,
                "The proxy configuration for this workspace was not applied, so the tool was not " +
                "published: " + applied.Error);
        }

        return new($"https://{host}/?{driver}=", user, password, null);
    }

    /// <summary>
    /// Takes an admin-tool session back: the route row, then the container, then a republish so a
    /// router that already read the removed route stops naming it.
    ///
    /// <para>
    /// None of this takes the caller's own token, for the reason DeploymentPipeline's failure path
    /// gives at length: this is not more of the work, it is the undoing of work that has already
    /// stopped, on <see cref="CancellationToken.None"/> throughout. The route removal and its save
    /// are left unguarded on purpose — if the database itself will not take the withdrawal, that is
    /// not a fact either caller below can paper over. The container removal and the republish are
    /// each wrapped instead: a cancellation escaping either would replace a refusal or a return the
    /// caller has already decided on with an exception it has to guess at, after the container has
    /// been asked to go and the route has already been withdrawn, so what already happened is the
    /// only accurate description left to give.
    /// </para>
    /// </summary>
    private async Task WithdrawAsync(Route route, string container, Guid workspaceId, IDockerEngine docker)
    {
        db.Routes.Remove(route);
        await db.SaveChangesAsync(CancellationToken.None);
        try { await docker.RemoveContainerAsync(container, force: true, CancellationToken.None); } catch { }
        try { await proxy.ApplyAllAsync(workspaceId, CancellationToken.None); }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not withdraw the admin tool's route after a failed apply.");
        }
    }

    /// <summary>
    /// Removes every session past its hour: the container first, then the route that pointed at it.
    /// Public so the sweep can be exercised directly rather than by waiting an hour and hoping.
    ///
    /// <para>
    /// Reads <see cref="IServerEngineFactory.Local"/> alone, deliberately, not every server this
    /// installation knows about: <see cref="OpenAsync"/> above now refuses to start a session
    /// anywhere else, so this panel's own daemon is the only place one can ever be running. Sweeping
    /// every server here would be scanning machines this feature can no longer put anything on.
    /// </para>
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var docker = engines.Local;
        var containers = await docker.ListContainersAsync("harbora.adminer", ct);
        var closed = 0;

        foreach (var c in containers)
        {
            if (!c.Labels.TryGetValue("harbora.adminer", out var raw) || !Guid.TryParse(raw, out var serviceId))
                continue;

            var startedAt = c.Labels.TryGetValue("harbora.adminer.started", out var stamp)
                            && long.TryParse(stamp, out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix)
                // A session whose label cannot be read is treated as expired: the safe reading of
                // "unknown" for something whose whole purpose is to be temporary.
                : DateTimeOffset.MinValue;

            if (!AdminerSession.Expired(startedAt, clock.UtcNow)) continue;

            try { await docker.RemoveContainerAsync(c.Id, force: true, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Could not stop an expired admin tool container.");
                continue;
            }

            var name = AdminerSession.ContainerName(serviceId);
            var routes = await db.Routes.IgnoreQueryFilters()
                .Where(r => r.TargetService == name).ToListAsync(ct);

            if (routes.Count > 0)
            {
                db.Routes.RemoveRange(routes);
                await db.SaveChangesAsync(ct);
                // The sweeper has no session; the engine's own read is unfiltered, which is what
                // keeps an expiring admin session from withdrawing the platform's routing with it.
                // No caller workspace either: nobody is waiting on this apply's answer, and the
                // routes it just removed could span several tenants.
                await proxy.ApplyAllAsync(null, ct);
            }

            closed++;
        }

        return closed;
    }
}

/// <summary>Stops admin sessions that outlived their hour.</summary>
public sealed class AdminerSweeper(
    IServiceScopeFactory scopeFactory,
    ILogger<AdminerSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var adminer = scope.ServiceProvider.GetRequiredService<AdminerService>();

                var closed = await adminer.SweepAsync(stoppingToken);
                if (closed > 0) logger.LogInformation("Closed {Count} expired admin session(s).", closed);
            }
            catch (Exception ex) { logger.LogError(ex, "Sweeping admin sessions failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
