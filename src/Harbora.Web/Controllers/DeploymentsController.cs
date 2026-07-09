using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

[Authorize]
public sealed class DeploymentsController(HarboraDbContext db, ICurrentUser currentUser) : Controller
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
        return View(deployment);
    }

    /// <summary>Backfills already-persisted log lines before the SignalR stream takes over.</summary>
    [HttpGet("/deployments/{id:guid}/logs")]
    public async Task<IActionResult> Logs(Guid id, long after = -1, CancellationToken ct = default)
    {
        var owns = await db.Deployments.AnyAsync(d => d.Id == id && d.App!.WorkspaceId == WorkspaceId, ct);
        if (!owns) return NotFound();

        var lines = await db.DeploymentLogs
            .Where(l => l.DeploymentId == id && l.Sequence > after)
            .OrderBy(l => l.Sequence)
            .Select(l => new { seq = l.Sequence, stream = l.Stream.ToString(), l.Message, ts = l.Timestamp })
            .ToListAsync(ct);

        return Json(lines);
    }
}
