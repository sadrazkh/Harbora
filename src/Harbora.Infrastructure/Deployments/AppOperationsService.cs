using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Container lifecycle + logs for an app, routed to the app's server engine.
///
/// <para>
/// The two verbs that leave a container running ask <see cref="IBillingGate"/> first, and they ask
/// it here rather than at the button. This is the single place an app's status is written, so a
/// second caller — the resume after a top-up already is one, and an admin tool or a recovery
/// command would be the next — cannot start an app without going past it. Stopping, deleting and
/// reading logs are not gated: a workspace with no balance must still be able to put things down
/// and take them away.
/// </para>
/// </summary>
public sealed class AppOperationsService(
    HarboraDbContext db,
    IServerEngineFactory engineFactory,
    IProxyEngine proxy,
    IBillingGate billing,
    HostPortAllocator hostPorts,
    ILogger<AppOperationsService> logger) : IAppOperationsService
{
    public async Task RestartAsync(Guid appId, CancellationToken ct)
    {
        await RefuseIfUnpaidAsync(appId, ct);
        var (app, docker, id) = await ResolveAsync(appId, ct);
        if (id is not null) await docker.RestartContainerAsync(id, ct);
        await SetStatusAsync(app, AppStatus.Running, ct);
    }

    public async Task StartAsync(Guid appId, CancellationToken ct)
    {
        await RefuseIfUnpaidAsync(appId, ct);
        var (app, docker, id) = await ResolveAsync(appId, ct);
        if (id is not null) await docker.RestartContainerAsync(id, ct); // restart also starts a stopped container
        await SetStatusAsync(app, AppStatus.Running, ct);
    }

    /// <summary>
    /// Refuses before the server engine is even resolved, and throws rather than returning quietly.
    ///
    /// <para>
    /// First, because <see cref="ResolveAsync"/> reaches the node to list its containers — asking a
    /// server about a workspace that may not start anything is work nobody is paying for, and on an
    /// unreachable node it turns a refusal into a timeout.
    /// </para>
    ///
    /// <para>
    /// Throwing, because a start route that returns without an exception and without starting
    /// anything is the exact shape this branch keeps finding: the status is written
    /// <c>Running</c>, the hourly tick bills the hour, and nothing is running. Every caller either
    /// surfaces the message — the panel shows it where a quota refusal is shown — or records it as a
    /// failure, which is what <c>BillingSuspension.ResumeAsync</c> does.
    /// </para>
    ///
    /// <para>
    /// The workspace is read unfiltered. These two verbs are reached from a request that has one and
    /// from the resume after a top-up, which has none; under the tenant filter that second caller
    /// finds no app and this would throw "Sequence contains no elements" instead of answering.
    /// </para>
    /// </summary>
    private async Task RefuseIfUnpaidAsync(Guid appId, CancellationToken ct)
    {
        var workspaceId = await db.Apps.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.Id == appId).Select(a => a.WorkspaceId).FirstOrDefaultAsync(ct);
        if (workspaceId == Guid.Empty) return; // No such app; ResolveAsync below says so properly.

        var mayStart = await billing.CanStartAsync(workspaceId, ct);
        if (!mayStart.Allowed) throw new InvalidOperationException(mayStart.Reason);
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

        // Drop this app's routes, then re-apply what the platform is left routing.
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
            // What is rendered is not scoped by this: the engine reads the platform's own routes,
            // unfiltered, so the sessionless callers above cannot narrow it to a tenant — or to
            // nothing. The workspace named here decides only what happens about a route that fails
            // validation and is therefore left out: whether this caller is told the apply failed,
            // and whether the route may be named when it is. It is the app just deleted, since that
            // is whose action this apply is.
            await proxy.ApplyAllAsync(app.WorkspaceId, ct);
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
