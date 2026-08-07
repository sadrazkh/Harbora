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
/// </summary>
public sealed class AdminerService(
    HarboraDbContext db,
    IDockerEngine docker,
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
        await db.SaveChangesAsync(ct);

        var applied = await proxy.ApplyAllAsync(service.WorkspaceId, ct);

        if (!applied.Success)
        {
            // The route did not take, so the tool is unreachable — say so and clean up rather than
            // hand somebody a URL that will not answer.
            db.Routes.Remove(route);
            await db.SaveChangesAsync(ct);
            try { await docker.RemoveContainerAsync(container, force: true, ct); } catch { }
            return new(null, null, null, "The proxy did not accept the temporary route: " + applied.Error);
        }

        return new($"https://{host}/?{driver}=", user, password, null);
    }

    /// <summary>
    /// Removes every session past its hour: the container first, then the route that pointed at it.
    /// Public so the sweep can be exercised directly rather than by waiting an hour and hoping.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
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
