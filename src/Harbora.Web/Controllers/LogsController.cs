using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Searching across more than one app's logs — the thing the single-app Logs tab
/// (<c>AppsController.LogsData</c>) cannot do, because it only ever fetches one container's tail.
///
/// <para>
/// There is nowhere to search except that same fetched tail, fanned out over every app in scope:
/// nothing in this platform stores a running container's own stdout/stderr (the persisted
/// <c>DeploymentLog</c> table is the <i>build</i>'s log, a different stream with a different
/// lifecycle). <see cref="IAppOperationsService.SearchLogsAsync"/> does the fan-out and reports, per
/// app, how much it actually reached — that coverage is returned here rather than collapsed into a
/// single line count, so a search that only reached three of five apps says so.
/// </para>
///
/// <para>
/// <b>Tenant scoping happens twice here, deliberately.</b> <c>Project</c> and <c>Environment</c>
/// carry no global query filter (unlike <c>App</c>), so the explicit <c>WorkspaceId ==</c> predicate
/// below is the only thing standing between a project id typed into the URL and another workspace's
/// logs — the same reasoning <c>ProjectsController</c> already states for every lookup it does. The
/// app ids that get handed to <see cref="IAppOperationsService.SearchLogsAsync"/> are gathered from a
/// query filtered the same way, because that service resolves each id's server engine unfiltered by
/// workspace (documented on the interface) — ownership of the id list is this controller's job, not
/// the service's.
/// </para>
/// </summary>
[Authorize]
public sealed class LogsController(
    HarboraDbContext db,
    IAppOperationsService ops,
    Harbora.Infrastructure.Security.ProjectAccessService access,
    ICurrentUser currentUser) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private static bool IsFa =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    /// <summary>
    /// A project with a handful of environments and dozens of apps each is the platform's own
    /// ceiling (see the quota surfaces elsewhere), not a number worth making configurable — a search
    /// that fans out further than this is a search somebody meant to scope to one environment.
    /// </summary>
    private const int MaxAppsPerSearch = 100;

    private const int LinesPerApp = 200;

    [HttpGet("/projects/{id:guid}/logs")]
    public async Task<IActionResult> Search(Guid id, Guid? environmentId, CancellationToken ct)
    {
        var project = await LoadVisibleProjectAsync(id, ct);
        if (project is null) return NotFound();

        if (environmentId is { } scoped && !project.Environments.Any(e => e.Id == scoped))
            return NotFound();

        var apps = await db.Apps.AsNoTracking()
            .Where(a => a.WorkspaceId == WorkspaceId && a.Environment!.ProjectId == id
                        && (environmentId == null || a.EnvironmentId == environmentId))
            .OrderBy(a => a.Name)
            .ToListAsync(ct);

        ViewData["Title"] = IsFa ? $"جست‌وجوی لاگ‌ها — {project.Name}" : $"Search logs — {project.Name}";

        return View(new ProjectLogSearchViewModel
        {
            Project = project,
            Environments = project.Environments.OrderByDescending(e => e.IsDefault).ThenBy(e => e.Name).ToList(),
            SelectedEnvironmentId = environmentId,
            Apps = apps
        });
    }

    /// <summary>
    /// The search itself. Returns every app asked about in <c>coverage</c>, matched or not — the page
    /// reads that to say what the search actually covered, not just what it found.
    /// </summary>
    [HttpGet("/projects/{id:guid}/logs/search")]
    public async Task<IActionResult> SearchData(
        Guid id, Guid? environmentId, string? search, bool problems, int minutes, CancellationToken ct)
    {
        var project = await LoadVisibleProjectAsync(id, ct);
        if (project is null) return NotFound();

        if (environmentId is { } scoped && !project.Environments.Any(e => e.Id == scoped))
            return NotFound();

        // Vetted here, not inside the service: SearchLogsAsync resolves each id's server engine
        // unfiltered by workspace, exactly as GetLogsAsync already does for one app, so this query —
        // scoped to the caller's own workspace and to the project just confirmed visible — is what
        // keeps a cross-app search from ever reaching an id that does not belong to it.
        var appIds = await db.Apps.AsNoTracking()
            .Where(a => a.WorkspaceId == WorkspaceId && a.Environment!.ProjectId == id
                        && (environmentId == null || a.EnvironmentId == environmentId))
            .OrderBy(a => a.Name)
            .Select(a => a.Id)
            .Take(MaxAppsPerSearch)
            .ToListAsync(ct);

        if (appIds.Count == 0)
            return Json(new { appsSearched = 0, appsReached = 0, hits = Array.Empty<object>(), coverage = Array.Empty<object>() });

        var window = minutes > 0 ? TimeSpan.FromMinutes(minutes) : (TimeSpan?)null;
        var result = await ops.SearchLogsAsync(appIds, search, problems, window, LinesPerApp, ct);

        return Json(new
        {
            appsSearched = result.Coverage.Count,
            appsReached = result.Coverage.Count(c => c.Reached),
            hits = result.Hits.Select(h => new
            {
                appId = h.AppId,
                appName = h.AppName,
                line = h.Line,
                timestamp = h.Timestamp
            }),
            coverage = result.Coverage.Select(c => new
            {
                appId = c.AppId,
                appName = c.AppName,
                reached = c.Reached,
                reason = c.UnavailableReason,
                linesScanned = c.LinesScanned,
                timeWindowRequested = c.TimeWindowRequested,
                timeWindowHonored = c.TimeWindowHonored
            })
        });
    }

    /// <summary>
    /// The project, only if it belongs to this workspace and this member may see it — the same two
    /// checks <c>ProjectsController.LoadAsync</c> makes, repeated here because that method is private
    /// to a different controller and the rule must not depend on where it happens to be written.
    /// </summary>
    private async Task<Harbora.Domain.Projects.Project?> LoadVisibleProjectAsync(Guid id, CancellationToken ct)
    {
        var project = await db.Projects.Include(p => p.Environments)
            .FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == WorkspaceId, ct);
        if (project is null) return null;

        if (await access.VisibleProjectIdsAsync(ct) is { } visible && !visible.Contains(project.Id))
            return null;

        return project;
    }
}
