using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Infrastructure.Networking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Re-joins the panel — and the proxy — to every tenant network a locally-placed, deployed app
/// depends on.
///
/// <para>
/// The only place that membership is normally created is <see cref="DeploymentPipeline"/>, at deploy
/// time (the local-server branch of its build, an imperative <c>docker network connect</c> for both
/// <c>ProxyContainerName</c> and <c>PanelContainerName</c>). That membership lives on the container,
/// not on <c>deploy/docker-compose.yml</c>, which declares the panel on the shared <c>harbora</c>
/// network only — the file says so at its own <c>harbora-panel</c> block ("it joins tenant networks
/// too"). The documented upgrade, <c>cd deploy &amp;&amp; docker compose up -d --build</c>
/// (<c>deploy/RUNBOOK.md</c>), rebuilds the panel's own image on every run and therefore recreates the
/// panel container — which drops every membership compose never wrote down. An app deployed before
/// that update is then unreachable from the panel, by its own container name, on its own network,
/// until it is deployed again. Cron and event calls are the only callers who notice between deploys —
/// <see cref="Functions.FunctionInvoker.ResolveAddressAsync"/> — and they fail with "Could not reach
/// the function app." for as long as nobody redeploys.
/// </para>
///
/// <para>
/// Re-attaching here, once on every boot, is self-healing: it needs nothing new from compose, costs
/// nothing when memberships already hold (<c>ConnectNetworkAsync</c> treats "already attached" as
/// success), and reuses the exact two calls the pipeline already makes rather than inventing a second
/// way to decide network membership. Scoped to apps placed on the <em>local</em> server only — a
/// remote node's apps are addressed by a published host port
/// (<see cref="Functions.FunctionInvoker.ResolveAddressAsync"/>), never by joining a Docker network on
/// this machine — and to apps that have actually been deployed at least once, since an app with no
/// active deployment has no running container to reach.
/// </para>
///
/// <para>
/// Registered ahead of <c>JobStartupGateOpener</c>, in the same group as
/// <see cref="DeploymentReconciler"/>: the job worker must not be let loose on a cron or event
/// invocation before the membership it depends on has been restored, or the very first call after a
/// restart would still fail the way this class exists to prevent.
/// </para>
/// </summary>
public sealed class PanelNetworkRebinder(
    IServiceScopeFactory scopeFactory,
    IOptions<HarboraRuntimeOptions> options,
    ILogger<PanelNetworkRebinder> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            var engines = scope.ServiceProvider.GetRequiredService<IServerEngineFactory>();
            var opt = options.Value;

            // Unfiltered: this runs at startup with no session, and a filtered read would find no
            // apps and silently rebind nothing at all — the exact trap this platform has fallen into
            // before with a background reader.
            var environmentIds = await db.Apps.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.ActiveDeploymentId != null)
                .Join(db.Servers.IgnoreQueryFilters().AsNoTracking().Where(s => s.IsLocal),
                    a => a.ServerId, s => s.Id, (a, _) => a.EnvironmentId)
                .Distinct()
                .ToListAsync(ct);

            if (environmentIds.Count == 0) return;

            var docker = engines.Local;
            var rebound = 0;
            foreach (var environmentId in environmentIds)
            {
                try
                {
                    var network = await EnvironmentNetworkResolver.ForAsync(db, environmentId, ct);
                    // Idempotent even if this is the very first boot after a restore and the network
                    // has not been created yet — matches the deploy-time path, which ensures it too.
                    await docker.EnsureNetworkAsync(network, ct);
                    await docker.ConnectNetworkAsync(opt.ProxyContainerName, network, ct);
                    await docker.ConnectNetworkAsync(opt.PanelContainerName, network, ct);
                    rebound++;
                }
                catch (Exception e)
                {
                    log.LogWarning(e,
                        "Could not rebind the panel to environment {EnvironmentId}'s network; its apps " +
                        "may be unreachable from the panel until they are next deployed.",
                        environmentId);
                }
            }

            log.LogInformation(
                "Rebound the panel to {Bound} of {Total} tenant network(s) after restart.",
                rebound, environmentIds.Count);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Never fatal. A panel that cannot rebind is a panel with some function apps unreachable
            // between deploys; a panel that refuses to start over it is a panel with everything down.
            log.LogError(e,
                "The panel's tenant network memberships could not be rebound; some apps may be " +
                "unreachable from the panel until they are next deployed.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
