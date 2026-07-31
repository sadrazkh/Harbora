using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Deployments;

/// <summary>Container lifecycle + logs for an app, routed to the app's server engine.</summary>
public sealed class AppOperationsService(
    HarboraDbContext db,
    IServerEngineFactory engineFactory,
    IProxyEngine proxy,
    HostPortAllocator hostPorts,
    ILogger<AppOperationsService> logger) : IAppOperationsService
{
    public async Task RestartAsync(Guid appId, CancellationToken ct)
    {
        var (app, docker, id) = await ResolveAsync(appId, ct);
        if (id is not null) await docker.RestartContainerAsync(id, ct);
        await SetStatusAsync(app, AppStatus.Running, ct);
    }

    public async Task StartAsync(Guid appId, CancellationToken ct)
    {
        var (app, docker, id) = await ResolveAsync(appId, ct);
        if (id is not null) await docker.RestartContainerAsync(id, ct); // restart also starts a stopped container
        await SetStatusAsync(app, AppStatus.Running, ct);
    }

    public async Task StopAsync(Guid appId, CancellationToken ct)
    {
        var (app, docker, id) = await ResolveAsync(appId, ct);
        if (id is not null) await docker.StopContainerAsync(id, ct);
        await SetStatusAsync(app, AppStatus.Stopped, ct);
    }

    public async Task DeleteAsync(Guid appId, bool removeVolumes, CancellationToken ct)
    {
        // Deleting is also driven by the preview sweeper and by branch-deleted webhooks, neither of
        // which has a session. Under the tenant filter those callers found nothing and returned
        // quietly, so the container kept running while the caller logged a removal. Ownership is the
        // caller's to check — the controller does, and a webhook is bound to one repository.
        var app = await db.Apps.IgnoreQueryFilters().Include(a => a.Volumes)
            .FirstOrDefaultAsync(a => a.Id == appId, ct);
        if (app is null) return;
        var docker = await engineFactory.ResolveAsync(app.ServerId, ct);

        var id = await FindContainerIdAsync(docker, app.Slug, ct);
        if (id is not null) await docker.RemoveContainerAsync(id, force: true, ct);
        if (removeVolumes)
            foreach (var v in app.Volumes) await docker.RemoveVolumeAsync(v.Name, ct);

        // Drop this app's routes, then re-apply the workspace's remaining routes.
        var workspaceId = app.WorkspaceId;
        await db.Routes.IgnoreQueryFilters().Where(r => r.AppId == appId).ExecuteDeleteAsync(ct);
        // Host-port reservations hang off the server, not the app, so nothing cascades them away.
        // Left behind they would retire a port from the node permanently, once per deleted app.
        await hostPorts.ReleaseAppAsync(appId, ct);
        db.Apps.Remove(app); // cascades env vars, domains, deployments, volumes
        await db.SaveChangesAsync(ct);

        // A preview owns the environment it was created in, so deleting it by hand from the panel
        // must clean up the same things the branch-deleted webhook does. Otherwise every preview
        // anybody removes themselves leaves an empty environment behind for ever.
        if (app.PreviewOfAppId is not null && app.EnvironmentId is { } environmentId)
            await RemoveEmptyPreviewEnvironmentAsync(environmentId, ct);

        try
        {
            // Unfiltered on purpose, and the dangerous one: filtered, a sessionless caller reads an
            // empty set and hands the proxy "this workspace has no routes", withdrawing every route
            // the tenant has. The workspace is pinned explicitly in the predicate instead.
            var routes = await db.Routes.IgnoreQueryFilters()
                .Where(r => r.WorkspaceId == workspaceId && r.IsEnabled).ToListAsync(ct);
            await proxy.ApplyAsync(routes, ct);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Proxy re-apply after delete failed."); }
    }

    /// <summary>
    /// Drops a preview's environment once nothing is left in it. Emptiness is the whole condition:
    /// a preview environment holds one service, but somebody may have added another, and that one is
    /// not ours to delete.
    /// </summary>
    private async Task RemoveEmptyPreviewEnvironmentAsync(Guid environmentId, CancellationToken ct)
    {
        if (await db.Apps.IgnoreQueryFilters().AnyAsync(a => a.EnvironmentId == environmentId, ct)) return;
        if (await db.ManagedServices.IgnoreQueryFilters().AnyAsync(s => s.EnvironmentId == environmentId, ct)) return;

        var environment = await db.Environments.IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.Id == environmentId, ct);
        if (environment is null || environment.IsDefault) return;

        db.Environments.Remove(environment);
        await db.SaveChangesAsync(ct);
    }

    public async Task<string> GetLogsAsync(Guid appId, int tail, CancellationToken ct)
    {
        var (_, docker, id) = await ResolveAsync(appId, ct);
        if (id is null) return string.Empty;
        try { return await docker.GetLogsAsync(id, tail <= 0 ? 200 : tail, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Fetching logs failed."); return $"(logs unavailable: {ex.Message})"; }
    }

    // --- helpers ---

    private async Task<(App App, IDockerEngine Docker, string? ContainerId)> ResolveAsync(Guid appId, CancellationToken ct)
    {
        var app = await db.Apps.FirstAsync(a => a.Id == appId, ct);
        var docker = await engineFactory.ResolveAsync(app.ServerId, ct);
        var id = await FindContainerIdAsync(docker, app.Slug, ct);
        return (app, docker, id);
    }

    private static async Task<string?> FindContainerIdAsync(IDockerEngine docker, string slug, CancellationToken ct)
    {
        // Containers are versioned (harbora-{slug}-{n}) for zero-downtime cutover, so match by the
        // app label rather than an exact name and prefer the running one.
        var containers = await docker.ListContainersAsync(DeploymentPlanning.AppLabel, ct);
        return DeploymentPlanning.CurrentContainerId(containers, slug);
    }

    private async Task SetStatusAsync(App app, AppStatus status, CancellationToken ct)
    {
        await db.Apps.Where(a => a.Id == app.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, status), ct);
    }
}
