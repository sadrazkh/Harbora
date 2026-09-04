using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Monitoring;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Status;

/// <summary>One component row as the public page shows it — never the app's real slug or hostname.</summary>
public sealed record PublicComponentView(PublicAppState State, string DisplayName, MetricView Uptime30d);

/// <summary>One manual incident note, in the reader's language, with the other translation dropped —
/// the report picks the language once so the view never has to.</summary>
public sealed record PublicIncidentView(Guid Id, string Title, string? Body, DateTimeOffset StartedAt, DateTimeOffset? ResolvedAt);

/// <summary>
/// What an anonymous visitor to <c>status-{slug}.&lt;platform domain&gt;</c> is allowed to see, or
/// null when there is nothing to show — a disabled page and a workspace that does not exist answer
/// identically (see <see cref="StatusPageReport.BuildAsync"/>), so neither leaks which one it was.
/// </summary>
public sealed record PublicStatusPageView(
    string WorkspaceName, IReadOnlyList<PublicComponentView> Components, IReadOnlyList<PublicIncidentView> Incidents);

/// <summary>
/// Assembles the public status page for one workspace, and only ever that one — see the explicit
/// <c>WorkspaceId ==</c> predicate on every query below. This is the one place <see cref="StatusPageHealth"/>
/// and <c>LifecycleHistory</c> are asked on the status page's behalf; the anonymous controller calls
/// this and renders what it returns, never queries these tables itself.
///
/// <para>
/// <b>Anonymous, not sessionless-background.</b> The request that reaches this class has no signed-in
/// user and therefore no ambient workspace scope (<c>IWorkspaceScope</c> defaults to
/// <c>Guid.Empty</c>, deny-by-default) — so every query here uses <c>IgnoreQueryFilters()</c> and then
/// scopes explicitly by the one <see cref="Harbora.Domain.Identity.Workspace.Id"/> the host resolved
/// to, the identical shape <c>EventDispatcher</c> already established for background work with no
/// session of its own.
/// </para>
/// </summary>
public sealed class StatusPageReport(HarboraDbContext db, LifecycleHistory lifecycle, ISystemClock clock)
{
    /// <summary>
    /// Null when there is nothing an anonymous visitor may see: no workspace has this slug, it has
    /// been deleted, it has no status page, or the page exists but is not enabled. Every one of those
    /// is the same "not found" to the outside world — see <c>StatusPageController</c>.
    /// </summary>
    public async Task<PublicStatusPageView?> BuildAsync(string workspaceSlug, bool isFa, CancellationToken ct)
    {
        var workspace = await db.Workspaces.AsNoTracking()
            .Where(w => w.Slug == workspaceSlug && w.DeletedAt == null)
            .Select(w => new { w.Id, w.Name })
            .FirstOrDefaultAsync(ct);
        if (workspace is null) return null;

        var page = await db.StatusPages.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(p => p.WorkspaceId == workspace.Id, ct);
        // Opt-in only: no row, or a row nobody has switched on, reads exactly like no workspace at
        // all — the outside world is never told a page exists but is turned off.
        if (page is null || !page.IsEnabled) return null;

        var components = await db.StatusPageComponents.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.StatusPageId == page.Id && c.WorkspaceId == workspace.Id)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.CreatedAt)
            .ToListAsync(ct);

        var appIds = components.Select(c => c.AppId).ToList();
        var apps = await db.Apps.IgnoreQueryFilters().AsNoTracking()
            .Where(a => appIds.Contains(a.Id) && a.WorkspaceId == workspace.Id)
            .ToDictionaryAsync(a => a.Id, ct);

        // 2.1 (2026-09 market-gaps round two): the fix this whole class exists for now has a source.
        // Before this, PublicAppState came from App.Status alone — what Harbora believes it started,
        // never from anything that actually answered a request. IgnoreQueryFilters() + the explicit
        // WorkspaceId == below, the same shape every other read in this class already uses: this is an
        // anonymous request with no ambient scope, not a background sweep, so it is one workspace's own
        // checks, never every tenant's.
        var probes = await db.UptimeChecks.IgnoreQueryFilters().AsNoTracking()
            .Where(c => appIds.Contains(c.AppId) && c.WorkspaceId == workspace.Id
                        && c.IsEnabled && c.LastOutcome != null)
            .ToDictionaryAsync(c => c.AppId, c => c.LastOutcome!.Value, ct);

        var since = clock.UtcNow.AddDays(-30);
        var componentViews = new List<PublicComponentView>();
        foreach (var component in components)
        {
            // A component can outlive the app it points at (the app was deleted after being chosen) —
            // skipped rather than shown with a fabricated state for a resource that no longer exists.
            // The settings screen is where a dangling pick gets cleaned up; the public page just never
            // renders one.
            if (!apps.TryGetValue(component.AppId, out var app)) continue;

            var hasEverServed = app.ActiveDeploymentId.HasValue;
            var probeOutcome = probes.TryGetValue(app.Id, out var outcome) ? outcome : (UptimeCheckOutcome?)null;
            var state = StatusPageHealth.Resolve(app.Status, hasEverServed, app.MaintenanceMode, probeOutcome);

            var containerName = await ResolveContainerNameAsync(app, ct);
            var uptime = MetricDisplay.For(
                await lifecycle.UptimePercentAsync(app.ServerId, containerName, since, clock.UtcNow, ct), "%");

            componentViews.Add(new PublicComponentView(state, component.DisplayName, uptime));
        }

        var incidentRows = await db.StatusIncidents.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.StatusPageId == page.Id && i.WorkspaceId == workspace.Id)
            .OrderByDescending(i => i.StartedAt)
            .Take(50)
            .ToListAsync(ct);

        // Open first (ResolvedAt null sorts before any timestamp), newest within each group — the
        // "we know, we're on it" note a visitor is reading this page for belongs above the history.
        var incidents = incidentRows
            .OrderBy(i => i.ResolvedAt.HasValue)
            .ThenByDescending(i => i.StartedAt)
            .Select(i => new PublicIncidentView(
                i.Id,
                isFa ? i.TitleFa : i.TitleEn,
                isFa ? i.BodyFa : i.BodyEn,
                i.StartedAt,
                i.ResolvedAt))
            .ToList();

        return new PublicStatusPageView(workspace.Name, componentViews, incidents);
    }

    /// <summary>
    /// The exact rule <c>AppsController.Overview</c> and <c>MonitoringController</c> already use to
    /// name the container <c>LifecycleHistory</c> is asked about: the active deployment, falling back
    /// to the most recent one that succeeded, falling back to the legacy pre-numbered name. An app
    /// with neither (never deployed) still resolves to the legacy name — the same name those two call
    /// sites resolve to for the identical case — and <c>LifecycleHistory</c> itself is what reports
    /// "unknown" for it, because nothing was ever collected against a name nothing has run under. No
    /// special case is added here for that: inventing one would be a second opinion about what "never
    /// deployed" means, next to the one <c>LifecycleHistory</c> already holds. Copied rather than
    /// shared because both call sites are controller-local private methods with no seam to extend.
    /// </summary>
    private async Task<string> ResolveContainerNameAsync(App app, CancellationToken ct)
    {
        var latestDeployment = app.ActiveDeploymentId is { } activeId
            ? await db.Deployments.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == activeId && d.WorkspaceId == app.WorkspaceId, ct)
            : null;
        latestDeployment ??= await db.Deployments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.AppId == app.Id && d.WorkspaceId == app.WorkspaceId && d.Status == DeploymentStatus.Succeeded)
            .OrderByDescending(d => d.Number)
            .FirstOrDefaultAsync(ct);

        return latestDeployment is null
            ? DeploymentPlanning.LegacyContainerName(app.Slug)
            : DeploymentPlanning.ContainerName(app.WorkspaceId, app.Slug, latestDeployment.Number);
    }
}
