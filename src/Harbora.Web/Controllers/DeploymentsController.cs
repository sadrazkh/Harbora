using Harbora.Domain.Authorization;
﻿using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

[Authorize]
public sealed class DeploymentsController(
    HarboraDbContext db,
    Harbora.Infrastructure.Security.ProjectAccessService access,
    ICurrentUser currentUser) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Deployments";
        var deployments = await db.Deployments.Include(d => d.App)
            .Where(d => d.App!.WorkspaceId == WorkspaceId)
            .OrderByDescending(d => d.CreatedAt).Take(100).ToListAsync(ct);
        return View(deployments);
    }

    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var deployment = await db.Deployments
            .Include(d => d.App)
            .FirstOrDefaultAsync(d => d.Id == id && d.App!.WorkspaceId == WorkspaceId, ct);
        if (deployment is null) return NotFound();

        // The build log and the configuration diff belong to the app, so they follow its visibility.
        if (!await access.CanSeeAppAsync(deployment.AppId, ct)) return NotFound();

        // What changed since the version before this one — the question people actually ask after a
        // bad release, and one the history could not answer until deployments recorded their config.
        var previous = await db.Deployments
            .Where(d => d.AppId == deployment.AppId && d.Number < deployment.Number && d.ConfigJson != null)
            .OrderByDescending(d => d.Number)
            .FirstOrDefaultAsync(ct);

        var before = Harbora.Infrastructure.Deployments.DeploymentConfig.FromJson(previous?.ConfigJson);
        var after = Harbora.Infrastructure.Deployments.DeploymentConfig.FromJson(deployment.ConfigJson);

        ViewBag.ComparedWith = previous?.Number;
        ViewBag.ConfigChanges = Harbora.Infrastructure.Deployments.ConfigDiff.Between(before, after);
        ViewBag.ConfigIdentical = Harbora.Infrastructure.Deployments.ConfigDiff.AreIdentical(before, after);
        ViewBag.Config = after;

        return View(deployment);
    }

    /// <summary>Backfills already-persisted log lines before the SignalR stream takes over.</summary>
    [HttpGet("/deployments/{id:guid}/logs")]
    public async Task<IActionResult> Logs(Guid id, long after = -1, CancellationToken ct = default)
    {
        var deployment = await db.Deployments.AsNoTracking()
            .Where(d => d.Id == id && d.App!.WorkspaceId == WorkspaceId)
            .Select(d => new { d.AppId }).FirstOrDefaultAsync(ct);
        if (deployment is null) return NotFound();
        if (!await access.CanSeeAppAsync(deployment.AppId, ct)) return NotFound();

        var lines = await db.DeploymentLogs
            .Where(l => l.DeploymentId == id && l.Sequence > after)
            .OrderBy(l => l.Sequence)
            .Select(l => new { seq = l.Sequence, stream = l.Stream.ToString(), l.Message, ts = l.Timestamp })
            .ToListAsync(ct);

        return Json(lines);
    }
}
