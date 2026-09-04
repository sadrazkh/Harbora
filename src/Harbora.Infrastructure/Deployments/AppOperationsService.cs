using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Logging;
using Harbora.Domain.Networking;
using Harbora.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    ILogger<AppOperationsService> logger,
    ISystemClock? clock = null,
    IEventPublisher? events = null,
    IOptions<HarboraRuntimeOptions>? runtimeOptions = null,
    // 2.2 (2026-09 log-retention plan), same shape and same reason as the three above: a 12th
    // trailing optional parameter rather than required, so the five existing positional-construction
    // test files keep compiling for a feature they were never testing. Only SetLogRetentionAsync's
    // day-count clamp reads this — there is no pre-removal flush in this class (see DeleteAsync's own
    // remark on why one here would be pointless; DeploymentPipeline.RetireOldContainersAsync is
    // where that flush actually lives, because that is the call where the app survives).
    IOptions<Harbora.Infrastructure.Logging.LogIngestionOptions>? logIngestionOptions = null) : IAppOperationsService
{
    // Defaulted rather than required, the same shape ManagedServiceEngine's own trailing
    // IEventPublisher? already uses: five existing test files construct this type positionally
    // (AppsControllerLogSearchTests, LogSearchTests x2, LogsControllerTenancyTests, WalletServiceTests)
    // to exercise Restart/Stop/Delete/log search, none of which this touches and none of which cares
    // about maintenance mode. A required 9th/10th/11th positional parameter would have broken every
    // one of them for a feature they never use; SetMaintenanceModeAsync is the only method that ever
    // reads these three, and DI always supplies real ones in production.
    private readonly HarboraRuntimeOptions _runtime = runtimeOptions?.Value ?? new HarboraRuntimeOptions();
    private readonly Harbora.Infrastructure.Logging.LogIngestionOptions _logRetention =
        logIngestionOptions?.Value ?? new Harbora.Infrastructure.Logging.LogIngestionOptions();

    /// <summary>
    /// The most persisted rows one search will look at for one app within its window — bounded for
    /// the same reason <c>LogsController.LinesPerApp</c> bounds the live tail: an unbounded scan over
    /// a long-retention app's full history would make one search's own cost scale with how much
    /// history it kept, exactly the runaway this feature has to avoid causing anywhere it touches.
    /// </summary>
    private const int PersistedScanCap = 2000;

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

    /// <inheritdoc/>
    public async Task<MaintenanceToggleResult> SetMaintenanceModeAsync(
        Guid appId, bool enabled, string? messageEn, string? messageFa, CancellationToken ct)
    {
        // Unfiltered for the same reason ResolveAsync/SetStatusAsync are: BillingSuspension's own
        // resume path and any future sessionless caller must be able to reach this, not only a
        // request bound to the app's own workspace. Ownership stays the caller's to check — every
        // request-bound entry point (AppsController) already asks CanTouchAppAsync before this runs.
        var app = await db.Apps.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == appId, ct);
        if (app is null)
            return MaintenanceToggleResult.Failed("No such app.");

        var routes = await db.Routes.IgnoreQueryFilters().Where(r => r.AppId == appId).ToListAsync(ct);

        // Captured before either branch below mutates anything, so a failed apply can put every row
        // back to exactly what it said on the way in — the same "capture before overwrite" shape
        // DeploymentPipeline.WireProxyAsync's own RouteRevert uses, just kept until the apply below
        // is known to have worked rather than only until this method returns.
        var undo = routes.Select(r => (
            Route: r, r.TargetService, r.TargetPort, r.ExtraUpstreamsJson, r.LoadBalancerHealthCheckPath,
            r.MaintenanceRedirected, r.SavedTargetService, r.SavedTargetPort,
            r.SavedExtraUpstreamsJson, r.SavedLoadBalancerHealthCheckPath)).ToList();

        if (enabled)
        {
            foreach (var r in routes)
            {
                // Only the first toggle-on saves the real upstream. A second enable while already on
                // (the toggle re-submitted, or a message edited) must not save the panel's own
                // address as if it were the app's real one — that would make the app unreachable
                // forever once maintenance is turned back off.
                if (!r.MaintenanceRedirected)
                {
                    r.SavedTargetService = r.TargetService;
                    r.SavedTargetPort = r.TargetPort;
                    r.SavedExtraUpstreamsJson = r.ExtraUpstreamsJson;
                    r.SavedLoadBalancerHealthCheckPath = r.LoadBalancerHealthCheckPath;
                }
                r.TargetService = _runtime.PanelContainerName;
                r.TargetPort = _runtime.PanelHttpPort;
                // Cleared, not carried over: RouteUpstreams.All renders every extra upstream as a
                // second loadBalancer server, and the app's old replica addresses are meaningless
                // (and possibly gone) once the target is the panel. LoadBalancerHealthCheckPath would
                // otherwise keep polling the app's own health path against the panel, which answers
                // nothing at it.
                r.ExtraUpstreamsJson = null;
                r.LoadBalancerHealthCheckPath = null;
                r.MaintenanceRedirected = true;
            }
        }
        else
        {
            foreach (var r in routes.Where(r => r.MaintenanceRedirected))
            {
                r.TargetService = r.SavedTargetService ?? r.TargetService;
                r.TargetPort = r.SavedTargetPort ?? r.TargetPort;
                r.ExtraUpstreamsJson = r.SavedExtraUpstreamsJson;
                r.LoadBalancerHealthCheckPath = r.SavedLoadBalancerHealthCheckPath;
                r.MaintenanceRedirected = false;
                r.SavedTargetService = null;
                r.SavedTargetPort = null;
                r.SavedExtraUpstreamsJson = null;
                r.SavedLoadBalancerHealthCheckPath = null;
            }
        }

        await db.SaveChangesAsync(ct);

        var result = await proxy.ApplyAllAsync(app.WorkspaceId, ct);
        if (!result.Success)
        {
            // Put every row back exactly as `undo` captured it, then re-publish from the reverted
            // rows — DeploymentPipeline.WireProxyAsync's own "revert, then re-apply" shape, because a
            // refused apply may already have changed the live file (the engine drops only the routes
            // that failed validation, not the whole apply) and between our save and this failure any
            // other caller's apply could have published what we just wrote.
            foreach (var (route, targetService, targetPort, extraUpstreamsJson, healthCheckPath,
                     maintenanceRedirected, savedTargetService, savedTargetPort,
                     savedExtraUpstreamsJson, savedHealthCheckPath) in undo)
            {
                route.TargetService = targetService;
                route.TargetPort = targetPort;
                route.ExtraUpstreamsJson = extraUpstreamsJson;
                route.LoadBalancerHealthCheckPath = healthCheckPath;
                route.MaintenanceRedirected = maintenanceRedirected;
                route.SavedTargetService = savedTargetService;
                route.SavedTargetPort = savedTargetPort;
                route.SavedExtraUpstreamsJson = savedExtraUpstreamsJson;
                route.SavedLoadBalancerHealthCheckPath = savedHealthCheckPath;
            }
            await db.SaveChangesAsync(ct);

            try { await proxy.ApplyAllAsync(app.WorkspaceId, CancellationToken.None); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not re-apply the proxy config after a failed maintenance-mode toggle.");
            }

            return MaintenanceToggleResult.Failed(ProxyDiagnosis.ExplainApplyFailure(result));
        }

        // Only now — after the proxy is known to have accepted the new routing — does the
        // customer-visible flag change. See App.MaintenanceMode's own doc for why.
        app.MaintenanceMode = enabled;
        app.MaintenanceMessage = enabled ? NullIfBlank(messageEn) : null;
        app.MaintenanceMessageFa = enabled ? NullIfBlank(messageFa) : null;
        app.MaintenanceSince = enabled ? (clock?.UtcNow ?? DateTimeOffset.UtcNow) : null;
        await db.SaveChangesAsync(ct);

        // Best-effort, never a reason a toggle that already worked reads as failed — the same rule
        // DeploymentPipeline and BackupEngine already apply at their own equivalent seams.
        if (events is not null)
            await events.PublishAsync(
                app.WorkspaceId, enabled ? EventKind.MaintenanceOn : EventKind.MaintenanceOff,
                new Dictionary<string, string> { ["app"] = app.Name }, ct);

        return MaintenanceToggleResult.Ok;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <inheritdoc/>
    public async Task<RateLimitToggleResult> SetRateLimitAsync(
        Guid appId, bool enabled, int average, int burst, CancellationToken ct)
    {
        // Validated before anything is touched — the same "refuse before the DB" shape the route
        // designer's own save gate uses, so a bad number never reaches a route row, let alone a
        // deployment's render.
        if (enabled)
        {
            if (!Domain.Apps.AppRateLimitPolicy.IsValidAverage(average))
                return RateLimitToggleResult.Failed(
                    $"Requests per minute must be between {Domain.Apps.AppRateLimitPolicy.MinAverage} " +
                    $"and {Domain.Apps.AppRateLimitPolicy.MaxAverage}.");
            if (!Domain.Apps.AppRateLimitPolicy.IsValidBurst(burst))
                return RateLimitToggleResult.Failed(
                    $"Burst allowance must be between {Domain.Apps.AppRateLimitPolicy.MinBurst} " +
                    $"and {Domain.Apps.AppRateLimitPolicy.MaxBurst}.");
        }

        // Unfiltered for the same reason SetMaintenanceModeAsync's own read is: ownership is the
        // caller's to check, and every request-bound entry point (AppsController) already asks
        // CanTouchAppAsync before this runs.
        var app = await db.Apps.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == appId, ct);
        if (app is null)
            return RateLimitToggleResult.Failed("No such app.");

        var routes = await db.Routes.IgnoreQueryFilters().Where(r => r.AppId == appId).ToListAsync(ct);

        // Captured before anything is mutated, so a failed apply can put every row back to exactly
        // what it said on the way in — DeploymentPipeline.WireProxyAsync's own RouteRevert shape,
        // kept until the apply below is known to have worked rather than only until this method
        // returns.
        var undo = routes.Select(r => (r.RateLimitEnabled, r.RateLimitAverage, r.RateLimitBurst)).ToList();

        foreach (var r in routes)
        {
            r.RateLimitEnabled = enabled;
            // Only overwritten while turning it on (or reconfiguring while already on). Turning it
            // off must leave the last-configured numbers in place rather than stamping in whatever the
            // disable request happened to carry (typically zeros) — so switching it back on later
            // starts from what was there, not from scratch.
            if (enabled)
            {
                r.RateLimitAverage = average;
                r.RateLimitBurst = burst;
            }
        }
        await db.SaveChangesAsync(ct);

        var result = await proxy.ApplyAllAsync(app.WorkspaceId, ct);
        if (!result.Success)
        {
            // Put every row back exactly as `undo` captured it, then re-publish from the reverted
            // rows — the same reasoning SetMaintenanceModeAsync's own failure path gives: a refused
            // apply may already have changed the live file, and between our save and this failure any
            // other caller's apply could have published what we just wrote.
            for (var i = 0; i < routes.Count; i++)
            {
                routes[i].RateLimitEnabled = undo[i].RateLimitEnabled;
                routes[i].RateLimitAverage = undo[i].RateLimitAverage;
                routes[i].RateLimitBurst = undo[i].RateLimitBurst;
            }
            await db.SaveChangesAsync(ct);

            try { await proxy.ApplyAllAsync(app.WorkspaceId, CancellationToken.None); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not re-apply the proxy config after a failed rate-limit toggle.");
            }

            return RateLimitToggleResult.Failed(ProxyDiagnosis.ExplainApplyFailure(result));
        }

        // Only now — after the proxy is known to have accepted the new routing — does the
        // customer-visible flag change. See App.RateLimitEnabled's own doc for why.
        app.RateLimitEnabled = enabled;
        if (enabled)
        {
            app.RateLimitAverage = average;
            app.RateLimitBurst = burst;
        }
        await db.SaveChangesAsync(ct);

        return RateLimitToggleResult.Ok;
    }

    /// <inheritdoc/>
    public async Task<LogRetentionResult> SetLogRetentionAsync(Guid appId, int days, CancellationToken ct)
    {
        // Refused before the DB, the same shape SetRateLimitAsync's own bounds check uses.
        if (days < 0)
            return LogRetentionResult.Failed("Retention days cannot be negative.");
        if (days > _logRetention.MaxRetentionDays)
            return LogRetentionResult.Failed(
                $"Retention cannot exceed {_logRetention.MaxRetentionDays} days.");

        // Unfiltered for the same reason SetMaintenanceModeAsync's own read is: ownership is the
        // caller's to check, and every request-bound entry point (AppsController) already asks
        // CanTouchAppAsync before this runs.
        var app = await db.Apps.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == appId, ct);
        if (app is null) return LogRetentionResult.Failed("No such app.");

        var now = clock?.UtcNow ?? DateTimeOffset.UtcNow;

        if (days == 0)
        {
            // Turning it off deletes what is already stored, immediately, rather than leaving it to
            // rot unreachable — SearchLogsAsync only ever looks at persisted rows while
            // LogRetentionDays > 0, so an orphaned row would sit on disk forever answering nothing.
            // An operator turning a disk-costing feature off is asking for the disk back.
            var doomed = db.AppLogLines.IgnoreQueryFilters().Where(l => l.AppId == appId);
            if (db.Database.IsRelational())
                await doomed.ExecuteDeleteAsync(ct);
            else
            {
                var rows = await doomed.ToListAsync(ct);
                db.AppLogLines.RemoveRange(rows);
            }

            app.LogRetentionDays = 0;
            app.LogRetentionEnabledAt = null;
            app.LogRetentionBudgetCapped = false;
        }
        else
        {
            // Only stamped on the 0 → positive transition: reconfiguring an already-enabled app's day
            // count must not reset the "since" the budget-capped signal measures against, or every
            // edit would look like a brand-new app with no history yet.
            if (app.LogRetentionDays <= 0) app.LogRetentionEnabledAt = now;
            app.LogRetentionDays = days;
        }

        await db.SaveChangesAsync(ct);
        return LogRetentionResult.Ok;
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

        // 2.2 (2026-09 log-retention plan): deliberately NO pre-removal log flush here, unlike
        // DeploymentPipeline.RetireOldContainersAsync's own. This method removes the App row itself a
        // few lines down, and AppLogLine cascades on that FK — so anything flushed here would be
        // deleted again inside this very call, before anything could ever read it. The flush belongs
        // only where the app SURVIVES and only its container is replaced.
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
        // 5.1 (per-app grants, HARBORA-0035): ProjectGrant carries no FK to cascade this the way
        // DatabaseAccessGrant does off ManagedServiceId — it has none of any kind, by design (see
        // HarboraDbContext's own remarks by the DbSet). Left behind, a grant naming a deleted app
        // is a permission that grants nothing and a row nobody can ever explain; loaded and removed
        // rather than ExecuteDeleteAsync for the same reason the routes just above are.
        var appGrants = await db.ProjectGrants.IgnoreQueryFilters().Where(g => g.AppId == appId).ToListAsync(ct);
        db.ProjectGrants.RemoveRange(appGrants);
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
        var now = DateTimeOffset.UtcNow;
        var hits = new List<LogSearchHit>();
        var coverage = new List<AppLogCoverage>();

        foreach (var appId in appIds)
        {
            // Looked up once, independent of whether the live engine can be reached — the persisted
            // store (2.2, 2026-09 log-retention plan), when this app has retention configured, must
            // still answer a search when the node itself is down, which is exactly the moment "why
            // did it crash" matters most. A bare "no such app" name mirrors what the old code
            // effectively said on a resolve failure, for an id that turns out not to exist at all.
            var appRow = await db.Apps.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == appId, ct);
            var appName = appRow?.Name ?? appId.ToString();

            var liveReached = false;
            string? liveReason = appRow is null ? "No such app." : null;
            var liveLinesScanned = 0;
            var windowHonored = false;

            if (appRow is not null)
            {
                try
                {
                    var docker = await engineFactory.ResolveAsync(appRow.ServerId, ct);
                    var containerId = await FindContainerIdAsync(docker, appRow.WorkspaceId, appRow.Slug, ct);

                    if (containerId is null)
                    {
                        liveReason = "No container is running for this app.";
                    }
                    else if (window is { } w)
                    {
                        try
                        {
                            var timed = await docker.GetLogsSinceAsync(containerId, now - w, cap, ct);
                            var matched = LogFilter.ApplyTimed(timed, text, problemsOnly);
                            hits.AddRange(matched.Select(m => new LogSearchHit(appRow.Id, appRow.Name, m.Text, m.Timestamp)));
                            liveReached = true; liveLinesScanned = timed.Count; windowHonored = true;
                        }
                        catch (NotSupportedException)
                        {
                            // This app's host cannot attach real timestamps — fall through to the
                            // plain tail below rather than reporting a false empty result for a
                            // window that was never actually applied.
                            var raw = await docker.GetLogsAsync(containerId, cap, ct);
                            var matchedLines = LogFilter.Apply(raw, text, problemsOnly);
                            hits.AddRange(matchedLines.Select(m => new LogSearchHit(appRow.Id, appRow.Name, m, null)));
                            liveReached = true; liveLinesScanned = CountLines(raw); windowHonored = false;
                        }
                    }
                    else
                    {
                        var raw = await docker.GetLogsAsync(containerId, cap, ct);
                        var matchedLines = LogFilter.Apply(raw, text, problemsOnly);
                        hits.AddRange(matchedLines.Select(m => new LogSearchHit(appRow.Id, appRow.Name, m, null)));
                        liveReached = true; liveLinesScanned = CountLines(raw); windowHonored = false;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Fetching logs for app {AppId} failed.", appId);
                    liveReason = ex.Message;
                }
            }

            // The persisted store, merged in alongside the live tail rather than instead of it — a
            // line still in the live tail may also already be persisted, and both halves have to
            // contribute to what "how far back did this reach" reports.
            DateTimeOffset? reachedBackTo = null;
            var persistedReached = false;
            var persistedLinesScanned = 0;
            var retentionEnabled = appRow is { LogRetentionDays: > 0 };
            var budgetCapped = false;

            if (appRow is { LogRetentionDays: > 0 })
            {
                var since = window is { } w2 ? now - w2 : now.AddDays(-appRow.LogRetentionDays);
                var rows = await db.AppLogLines.IgnoreQueryFilters().AsNoTracking()
                    .Where(l => l.AppId == appId && l.Timestamp >= since)
                    .OrderByDescending(l => l.Timestamp)
                    .Take(PersistedScanCap)
                    .ToListAsync(ct);

                persistedReached = true;
                persistedLinesScanned = rows.Count;
                if (rows.Count > 0) reachedBackTo = rows[^1].Timestamp; // list is newest-first; last is oldest

                // Stored newest-first (the query above); LogFilter.ApplyTimed's continuation-line
                // grouping needs chronological order to attach a stack trace's frames to the line
                // that introduced them.
                var timedAscending = rows.AsEnumerable().Reverse()
                    .Select(r => new TimedLogLine(r.Timestamp, r.Text)).ToList();
                var persistedMatched = LogFilter.ApplyTimed(timedAscending, text, problemsOnly);
                hits.AddRange(persistedMatched.Select(m => new LogSearchHit(appId, appName, m.Text, m.Timestamp)));

                budgetCapped = appRow.LogRetentionBudgetCapped;
            }

            var reached = liveReached || persistedReached;
            coverage.Add(new AppLogCoverage(
                appId, appName, reached,
                reached ? null : liveReason,
                liveLinesScanned + persistedLinesScanned,
                windowRequested, windowHonored,
                reachedBackTo, retentionEnabled, budgetCapped));
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
