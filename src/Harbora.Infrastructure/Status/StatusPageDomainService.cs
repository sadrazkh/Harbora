using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Networking;
using Harbora.Infrastructure.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Status;

/// <summary>What an attempt to change the status page's own routing came back with.</summary>
public sealed record StatusPageDomainResult(bool Success, string? Error)
{
    public static readonly StatusPageDomainResult Ok = new(true, null);
}

/// <summary>
/// Makes a workspace's status page genuinely reachable through Traefik — the platform subdomain
/// (<c>status-{Workspace.Slug}.&lt;platform root domain&gt;</c>) and, once attached, a customer's own
/// custom domain (sub-project 8, 2026-08-20 platform-options plan).
///
/// <para>
/// <b>The same writer, not a second one.</b> Both hosts become an ordinary <see cref="Route"/> whose
/// <c>TargetService</c>/<c>TargetPort</c> point at this panel container — the exact fields
/// <c>DeploymentPipeline.WireProxyAsync</c> and <c>AppOperationsService.SetMaintenanceModeAsync</c>
/// already treat as "the live upstream", and the exact same <see cref="IProxyEngine.ApplyAllAsync"/>
/// that publishes every other route on the platform. A "target = status page instead of app
/// container" variant of the one flow, not a parallel Traefik writer that could drift from it.
/// </para>
///
/// <para>
/// <b>Immediate, and honest about failure.</b> There is no deployment step to piggy-back a Route's
/// creation on the way an app's domain waits for the next deploy — attaching (or enabling) applies
/// right away, the same "immediate operational act" sub-project 5's maintenance toggle established.
/// A failed apply undoes what this call just added and republishes from the reverted rows, the same
/// "capture before overwrite, put it back on failure" shape <c>AppOperationsService</c> uses — so a
/// caller can never read success back for a route that never actually got written.
/// </para>
/// </summary>
public sealed class StatusPageDomainService(
    HarboraDbContext db, IProxyEngine proxy, IOptions<HarboraRuntimeOptions> runtimeOptions)
{
    private readonly HarboraRuntimeOptions _runtime = runtimeOptions.Value;

    /// <summary>
    /// Ensures a <see cref="Route"/> exists for the platform subdomain and publishes it. Idempotent —
    /// called every time the page is enabled, including a re-enable, so it finds and refreshes an
    /// existing row rather than creating a second one.
    /// </summary>
    public async Task<StatusPageDomainResult> EnsurePlatformRouteAsync(
        Guid workspaceId, string host, CancellationToken ct)
    {
        var route = await db.Routes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Host == host && r.AppId == null, ct);
        var isNew = route is null;
        if (route is null)
        {
            route = new Route { WorkspaceId = workspaceId, Host = host };
            db.Routes.Add(route);
        }

        route.TargetService = _runtime.PanelContainerName;
        route.TargetPort = _runtime.PanelHttpPort;
        route.SslEnabled = true;
        route.RedirectHttpToHttps = true;
        route.IsEnabled = true;

        return await SaveApplyOrRevertAsync(workspaceId, isNew ? [route] : [], ct, route);
    }

    /// <summary>Removes the platform-subdomain route (enable is what recreates it) and publishes.</summary>
    public async Task RemovePlatformRouteAsync(Guid workspaceId, string host, CancellationToken ct)
    {
        var routes = await db.Routes.IgnoreQueryFilters()
            .Where(r => r.Host == host && r.AppId == null).ToListAsync(ct);
        if (routes.Count == 0) return;

        db.Routes.RemoveRange(routes);
        await db.SaveChangesAsync(ct);
        await proxy.ApplyAllAsync(workspaceId, ct);
    }

    /// <summary>
    /// Attaches <paramref name="host"/> as the status page's one custom domain: a <see cref="DomainName"/>
    /// row exactly like an app's (<see cref="DomainName.StatusPageId"/> set instead of
    /// <see cref="DomainName.AppId"/>) plus a <see cref="Route"/> pointed at this panel, published
    /// together. The caller is expected to have already checked uniqueness and the reserved-host
    /// rules — the same split <c>AppsController.AddDomain</c> uses between "is this typed host even
    /// allowed" (the controller, so it can give a bilingual reason) and "make it live" (here).
    /// </summary>
    public async Task<StatusPageDomainResult> AttachCustomDomainAsync(
        Guid workspaceId, Guid statusPageId, string host, CancellationToken ct)
    {
        var domain = new DomainName
        {
            Host = host, StatusPageId = statusPageId, SslEnabled = true, ForceHttps = true
        };
        db.Domains.Add(domain);

        var route = new Route
        {
            WorkspaceId = workspaceId, Host = host,
            TargetService = _runtime.PanelContainerName, TargetPort = _runtime.PanelHttpPort,
            SslEnabled = true, RedirectHttpToHttps = true, IsEnabled = true
        };
        db.Routes.Add(route);

        await db.SaveChangesAsync(ct);
        var result = await proxy.ApplyAllAsync(workspaceId, ct);
        if (result.Success) return StatusPageDomainResult.Ok;

        // Neither row was ever going to serve if the other's apply refused it — an attach is one
        // atomic act from the customer's side, so it undoes as one, the same "put it back, then
        // re-publish from what was put back" shape AppOperationsService.SetMaintenanceModeAsync uses.
        db.Domains.Remove(domain);
        db.Routes.Remove(route);
        await db.SaveChangesAsync(ct);
        try { await proxy.ApplyAllAsync(workspaceId, CancellationToken.None); }
        catch (Exception) { /* best-effort republish of the reverted state; the DB is already correct */ }

        return new StatusPageDomainResult(false, result.Error);
    }

    /// <summary>Detaches the status page's custom domain, if it has one: the <see cref="DomainName"/>
    /// row, its <see cref="Route"/>, and a republish — proving removal means asserting the router is
    /// gone from the rendered config, not merely that the database row was deleted.</summary>
    public async Task RemoveCustomDomainAsync(Guid workspaceId, Guid statusPageId, CancellationToken ct)
    {
        var domain = await db.Domains.IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.StatusPageId == statusPageId, ct);
        if (domain is null) return;

        var routes = await db.Routes.IgnoreQueryFilters()
            .Where(r => r.Host == domain.Host && r.AppId == null).ToListAsync(ct);

        db.Domains.Remove(domain);
        db.Routes.RemoveRange(routes);
        await db.SaveChangesAsync(ct);
        await proxy.ApplyAllAsync(workspaceId, ct);
    }

    /// <summary>
    /// Saves, applies, and — only when the apply fails and <paramref name="rollbackIfNew"/> named a
    /// freshly-added route — removes that row again and republishes from what is left, so an "enable"
    /// that could not actually reach Traefik never leaves a route behind that only the database knows
    /// about. An existing route that merely changed (a re-enable refreshing the same row) is left as
    /// it was before this call for the caller to retry; there is nothing "new" to undo.
    /// </summary>
    private async Task<StatusPageDomainResult> SaveApplyOrRevertAsync(
        Guid workspaceId, IReadOnlyList<Route> rollbackIfNew, CancellationToken ct, Route route)
    {
        await db.SaveChangesAsync(ct);
        var result = await proxy.ApplyAllAsync(workspaceId, ct);
        if (result.Success) return StatusPageDomainResult.Ok;

        if (rollbackIfNew.Count > 0)
        {
            db.Routes.Remove(route);
            await db.SaveChangesAsync(ct);
            try { await proxy.ApplyAllAsync(workspaceId, CancellationToken.None); }
            catch (Exception) { /* best-effort; the DB is already correct */ }
        }

        return new StatusPageDomainResult(false, result.Error);
    }
}
