using Harbora.Application.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Proxy;

/// <summary>
/// HARBORA-0060: republishes the whole platform's routing exactly once, on every boot.
///
/// <para>
/// A crash between a route write and the apply that was meant to follow it — <c>WireProxyAsync</c>'s
/// own failure path is one way there, but any interrupted apply gets you to the same place — leaves an
/// enabled route row on the database side of a Traefik render that was never taken, naming a container
/// that is gone or missing entirely from the file Traefik is serving from. Every write path re-applies
/// afterwards, so the state heals the moment anybody next touches routing in that workspace. For a
/// workspace nobody is actively deploying to, that moment may never come. <see cref="Deployments.DeploymentReconciler"/>
/// settles deployments left in flight by the same kind of crash; it does not touch routing, on
/// purpose (see its own doc) — this class is the sibling that closes that gap, not an edit to it.
/// </para>
///
/// <para>
/// <b>Reuses the one rule that decides what an apply publishes; does not add a second.</b> Which
/// route rows survive an apply is decided in exactly one place — <see cref="TraefikProxyEngine"/>'s
/// validation, reached through <see cref="IProxyEngine.ApplyAllAsync"/> — and this class calls that,
/// unchanged, the same way <c>RoutesController</c> and every deploy path already do. A row an ordinary
/// apply would leave out (a bad port, a redirect with no target, …) is left out here for the identical
/// reason, because it is the identical code path; a boot-specific notion of "valid route" would only
/// ever be a second rule for the two to drift out of.
/// </para>
///
/// <para>
/// <c>callerWorkspaceId</c> is passed as <see langword="null"/>: a boot pass has no session and no
/// workspace of its own, so a route it must leave out is never attributed to it as a failure (see the
/// parameter's own doc on <see cref="IProxyEngine.ApplyAllAsync"/>) — every excluded route is still
/// named at Error inside the engine regardless of who called, which is where an operator finds it.
/// </para>
///
/// <para>
/// <b>Platform-wide by construction, not by anything added here.</b> The catalog behind the engine
/// already reads with <c>IgnoreQueryFilters()</c> (<see cref="RouteCatalog"/>) because the dynamic-config
/// file is one file for the whole install. A boot pass that touches zero rows on a platform that has
/// routes is that filter doing its job on a sessionless caller, not evidence there was nothing to
/// republish — the same trap already named against <see cref="Deployments.PanelNetworkRebinder"/> and
/// <see cref="Deployments.DeploymentReconciler.StampDeploymentJobsWithTheirAppAsync"/>.
/// </para>
///
/// <para>
/// <b>Ordering against migrate and seed.</b> <c>Harbora.Web/Program.cs</c> runs its migrate-then-seed
/// block synchronously, before <c>app.RunAsync()</c> is ever reached; hosted services only start once
/// the generic host starts, which happens inside that call. Registering this as a plain
/// <see cref="IHostedService"/> — the same shape as <see cref="Deployments.DeploymentReconciler"/> and
/// <see cref="Deployments.PanelNetworkRebinder"/> — is what gives it that guarantee for free. Nothing
/// here calls it any other way, so nothing here can run ahead of the schema or the seed data.
/// </para>
///
/// <para>
/// Never fails startup: a panel that cannot republish routing at boot is a panel with stale routes for
/// whichever workspace nobody has touched since the crash — the same reduced state it was already in
/// before this class existed. A panel that refuses to start over it turns that into an outage for
/// every workspace instead of the one still stale.
/// </para>
/// </summary>
public sealed class ProxyBootRepublisher(IProxyEngine engine, ILogger<ProxyBootRepublisher> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            var result = await engine.ApplyAllAsync(null, ct);
            // With no caller workspace, ApplyAllAsync can only report failure for a route that
            // belongs to nobody in particular from this call's point of view — see the parameter's
            // own doc. The detail already reached the server log at Error inside the engine; this is
            // only the boot-time marker that it happened at all.
            if (!result.Success)
                logger.LogWarning(
                    "Startup proxy republish completed with route(s) left out; see the error above " +
                    "for which ones and why: {Error}", result.Error);
            else
                logger.LogInformation("Republished the platform's routing on startup.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never fatal — see the class doc. Routing is left exactly as the crash left it, which is
            // the state every workspace nobody has redeployed to was already in.
            logger.LogError(ex, "Startup proxy republish failed; routing was left as it was found.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
