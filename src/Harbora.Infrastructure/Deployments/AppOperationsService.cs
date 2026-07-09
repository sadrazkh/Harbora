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
        var app = await db.Apps.Include(a => a.Volumes).FirstOrDefaultAsync(a => a.Id == appId, ct);
        if (app is null) return;
        var docker = await engineFactory.ResolveAsync(app.ServerId, ct);

        var id = await FindContainerIdAsync(docker, app.Slug, ct);
        if (id is not null) await docker.RemoveContainerAsync(id, force: true, ct);
        if (removeVolumes)
            foreach (var v in app.Volumes) await docker.RemoveVolumeAsync(v.Name, ct);

        // Drop this app's routes, then re-apply the workspace's remaining routes.
        var workspaceId = app.WorkspaceId;
        await db.Routes.Where(r => r.AppId == appId).ExecuteDeleteAsync(ct);
        db.Apps.Remove(app); // cascades env vars, domains, deployments, volumes
        await db.SaveChangesAsync(ct);

        try
        {
            var routes = await db.Routes.Where(r => r.WorkspaceId == workspaceId && r.IsEnabled).ToListAsync(ct);
            await proxy.ApplyAsync(routes, ct);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Proxy re-apply after delete failed."); }
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
        var name = $"harbora-{slug}";
        var containers = await docker.ListContainersAsync("harbora.app", ct);
        return containers.FirstOrDefault(c => c.Name == name)?.Id;
    }

    private async Task SetStatusAsync(App app, AppStatus status, CancellationToken ct)
    {
        await db.Apps.Where(a => a.Id == app.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, status), ct);
    }
}
