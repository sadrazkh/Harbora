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

        var mayStart = await billing.CanStartAsync(
            workspaceId, Domain.Billing.BilledResourceType.App, appId, ct);
        // QuotaRefusedException, not a plain InvalidOperationException built from mayStart.Reason:
        // both callers of Start/Restart have a request in hand, and this is the shape that lets them
        // pick mayStart.ReasonFa for it instead of always showing English on a panel that is
        // bilingual everywhere else.
        if (!mayStart.Allowed) throw new QuotaRefusedException(mayStart);
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

        // HARBORA-0033. Checked first, before the container is even resolved: a refusal here has to
        // leave the app exactly as it was — running, routed, its volumes untouched — not a container
        // gone and a Protected volume orphaned behind it. This is the one guard every caller of
        // DeleteAsync inherits for free: the panel's own app delete, PreviewEnvironmentService's
        // teardown, and ProjectDeletionService's cascade all end up here rather than talking to Docker
        // themselves (see this class's own remarks, and ProjectDeletionService's).
        if (removeVolumes) Storage.VolumeProtection.GuardAgainstDestroying(app.Volumes);

        var docker = await engineFactory.ResolveAsync(app.ServerId, ct);

        var id = await FindContainerIdAsync(docker, app.WorkspaceId, app.Slug, ct);
        if (id is not null) await docker.RemoveContainerAsync(id, force: true, ct);
        if (removeVolumes)
            foreach (var v in app.Volumes) await docker.RemoveVolumeAsync(v.Name, ct);

        // Drop this app's routes, then re-apply what the platform is left routing. Loaded and removed
        // rather than ExecuteDeleteAsync, for the reason HostPortAllocator.RemoveAsync (called two
        // lines down, for its own rows) already states: a handful per app, and it keeps this path
        // exercisable by the test suite's provider, which has no ExecuteDelete. Before this, no HTTP
        // test could drive a delete through this method at all — ProjectDeleteHttpTests's
        // confirmed-cascade coverage is what found it.
        var routes = await db.Routes.IgnoreQueryFilters().Where(r => r.AppId == appId).ToListAsync(ct);
        db.Routes.RemoveRange(routes);
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

    /// <summary>
    /// One app's fetched tail, searched — the unit every caller of <see cref="SearchLogsAsync"/>
    /// fans out over. A failure resolving the app or reaching its engine is reported through
    /// <see cref="AppLogCoverage"/> rather than thrown, so one unreachable app in a cross-app search
    /// never hides what the reachable ones found.
    /// </summary>
    public async Task<LogSearchResult> SearchLogsAsync(
        IReadOnlyList<Guid> appIds, string? text, bool problemsOnly, TimeSpan? window, int maxLinesPerApp,
        CancellationToken ct)
    {
        var cap = maxLinesPerApp <= 0 ? 200 : maxLinesPerApp;
        var windowRequested = window is not null;
        var hits = new List<LogSearchHit>();
        var coverage = new List<AppLogCoverage>();

        foreach (var appId in appIds)
        {
            App app;
            IDockerEngine docker;
            string? containerId;
            try
            {
                (app, docker, containerId) = await ResolveAsync(appId, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Resolving app {AppId} for a log search failed.", appId);
                coverage.Add(new AppLogCoverage(appId, appId.ToString(), false, ex.Message, 0, windowRequested, false));
                continue;
            }

            if (containerId is null)
            {
                coverage.Add(new AppLogCoverage(
                    app.Id, app.Name, false, "No container is running for this app.", 0, windowRequested, false));
                continue;
            }

            try
            {
                if (window is { } w)
                {
                    try
                    {
                        var timed = await docker.GetLogsSinceAsync(containerId, DateTimeOffset.UtcNow - w, cap, ct);
                        var matched = LogFilter.ApplyTimed(timed, text, problemsOnly);
                        hits.AddRange(matched.Select(m => new LogSearchHit(app.Id, app.Name, m.Text, m.Timestamp)));
                        coverage.Add(new AppLogCoverage(app.Id, app.Name, true, null, timed.Count, true, true));
                        continue;
                    }
                    catch (NotSupportedException)
                    {
                        // This app's host cannot attach real timestamps — fall through to the plain
                        // tail below rather than reporting a false empty result for a window that was
                        // never actually applied.
                    }
                }

                var raw = await docker.GetLogsAsync(containerId, cap, ct);
                var matchedLines = LogFilter.Apply(raw, text, problemsOnly);
                hits.AddRange(matchedLines.Select(m => new LogSearchHit(app.Id, app.Name, m, null)));
                coverage.Add(new AppLogCoverage(app.Id, app.Name, true, null, CountLines(raw), windowRequested, false));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Fetching logs for app {AppId} failed.", appId);
                coverage.Add(new AppLogCoverage(app.Id, app.Name, false, ex.Message, 0, windowRequested, false));
            }
        }

        return new LogSearchResult(hits, coverage);
    }

    private static int CountLines(string? raw) =>
        string.IsNullOrEmpty(raw) ? 0 : raw.Replace("\r\n", "\n").Split('\n').Length;

    // --- helpers ---

    /// <summary>
    /// The app, its server's engine, and the container currently serving it.
    ///
    /// <para>
    /// Read unfiltered, together with <see cref="SetStatusAsync"/> and never one without the other.
    /// Half this service's callers are asking about a workspace that is not their session's: the
    /// resume after a top-up is driven from the provider console, where the administrator's own
    /// workspace is the provider's, and the preview sweeper and the branch-deleted webhook have no
    /// session at all. Under the tenant filter every one of those found no app and threw "Sequence
    /// contains no elements" before reaching a node — so a customer who had just paid was told their
    /// services were coming back while each start failed on a database predicate.
    /// </para>
    ///
    /// <para>
    /// Unfiltering only this half would be worse than leaving both: the throw would become a filtered
    /// <c>ExecuteUpdate</c> in <see cref="SetStatusAsync"/> that matches no rows and reports success,
    /// which is the shape nobody sees. <c>BillingSuspension</c>'s remarks name three reads, not two:
    /// these, and <c>ManagedServiceEngine.StartAsync</c>/<c>StopAsync</c>. This is the app half. The
    /// database half is in that file, and it landed later — for a while this comment said the fix was
    /// finished when the two reads that bring a customer's <i>database</i> back were still filtered,
    /// so a top-up restored the apps and left the data layer they all need down.
    /// </para>
    ///
    /// <para>
    /// <b>Ownership is the caller's to check</b>, exactly as it already is for
    /// <see cref="DeleteAsync"/> just above: every request-bound entry point resolves the app against
    /// the caller's workspace before it gets here (<c>AppsController</c> asks <c>OwnsAsync</c> on
    /// stop, start and restart), and the sessionless callers are each bound to one workspace by the
    /// work they were queued for.
    /// </para>
    /// </summary>
    private async Task<(App App, IDockerEngine Docker, string? ContainerId)> ResolveAsync(Guid appId, CancellationToken ct)
    {
        var app = await db.Apps.IgnoreQueryFilters().FirstAsync(a => a.Id == appId, ct);
        var docker = await engineFactory.ResolveAsync(app.ServerId, ct);
        var id = await FindContainerIdAsync(docker, app.WorkspaceId, app.Slug, ct);
        return (app, docker, id);
    }

    private async Task<string?> FindContainerIdAsync(IDockerEngine docker, Guid workspaceId, string slug, CancellationToken ct)
    {
        // Containers are versioned (harbora-{workspace}-{slug}-{n}) for zero-downtime cutover, so
        // match by the app label rather than an exact name and prefer the running one — and by the
        // workspace label, so restart/stop/delete/logs never reach across a slug shared with a
        // stranger's workspace (the same legacy bridge RetireOldContainersAsync uses).
        var containers = await docker.ListContainersAsync(DeploymentPlanning.AppLabel, ct);
        var slugExclusive = !await db.Apps.IgnoreQueryFilters()
            .AnyAsync(a => a.Slug == slug && a.WorkspaceId != workspaceId, ct);
        return DeploymentPlanning.CurrentContainerId(containers, workspaceId, slug, slugExclusive);
    }

    /// <summary>
    /// Writes what the app is now doing — the single place an app's status is set.
    ///
    /// <para>
    /// Unfiltered, and the other half of <see cref="ResolveAsync"/>'s note. This is the dangerous
    /// half: <c>ExecuteUpdate</c> composes an <c>UPDATE</c> with the filter folded into its
    /// <c>WHERE</c>, so a caller in the wrong scope matches no rows, raises nothing, and returns as
    /// if it had worked. The app would be reported stopped while its container kept running and the
    /// hourly tick kept billing it at the running rate — which is the one outcome here that costs a
    /// customer money nobody can point at.
    /// </para>
    /// </summary>
    private async Task SetStatusAsync(App app, AppStatus status, CancellationToken ct)
    {
        await db.Apps.IgnoreQueryFilters().Where(a => a.Id == app.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, status), ct);
    }
}
