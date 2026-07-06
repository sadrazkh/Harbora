using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Web.ViewModels;
using Route = Harbora.Domain.Networking.Route;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Backs the visual route designer. The island edits routes client-side; these endpoints
/// render/validate the generated Traefik config on demand and persist+apply atomically.
/// Users never hand-edit proxy config.
/// </summary>
[Authorize]
[Route("routes")]
public sealed class RoutesController(
    HarboraDbContext db,
    IProxyEngine proxy,
    ICurrentUser currentUser,
    IAntiforgery antiforgery) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Routing";

        var routes = await db.Routes.Where(r => r.WorkspaceId == WorkspaceId)
            .OrderByDescending(r => r.Priority).ThenBy(r => r.Host)
            .Select(r => RouteDto.FromEntity(r)).ToListAsync(ct);

        // Targets = deployed apps, addressable by their container name.
        var targets = await db.Apps.Where(a => a.WorkspaceId == WorkspaceId)
            .Select(a => new RouteTargetDto(a.Name, "harbora-" + a.Slug, a.ContainerPort, a.Id))
            .ToListAsync(ct);

        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        ViewBag.RoutesJson = System.Text.Json.JsonSerializer.Serialize(routes, json);
        ViewBag.TargetsJson = System.Text.Json.JsonSerializer.Serialize(targets, json);
        ViewBag.Csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
        return View();
    }

    [HttpPost("preview")]
    [ValidateAntiForgeryToken]
    public IActionResult Preview([FromBody] List<RouteDto> routes)
    {
        var preview = proxy.Preview(routes.Select(d => d.ToTransientEntity()).ToList());
        return Json(new { format = preview.Format, content = preview.Content });
    }

    [HttpPost("validate")]
    [ValidateAntiForgeryToken]
    public IActionResult Validate([FromBody] List<RouteDto> routes)
    {
        var result = proxy.Validate(routes.Select(d => d.ToTransientEntity()).ToList());
        return Json(new { isValid = result.IsValid, errors = result.Errors, warnings = result.Warnings });
    }

    [HttpPost("save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save([FromBody] List<RouteDto> routes, CancellationToken ct)
    {
        // Validate before touching the DB so a bad config never persists.
        var validation = proxy.Validate(routes.Select(d => d.ToTransientEntity()).ToList());
        if (!validation.IsValid)
            return Json(new { saved = false, validation = new { validation.IsValid, validation.Errors, validation.Warnings } });

        var existing = await db.Routes.Where(r => r.WorkspaceId == WorkspaceId).ToListAsync(ct);
        var keptIds = routes.Where(r => r.Id.HasValue).Select(r => r.Id!.Value).ToHashSet();

        // Delete routes the user removed in the designer.
        db.Routes.RemoveRange(existing.Where(r => !keptIds.Contains(r.Id)));

        // Upsert the rest.
        foreach (var dto in routes)
        {
            var entity = dto.Id.HasValue ? existing.FirstOrDefault(r => r.Id == dto.Id) : null;
            if (entity is null)
            {
                entity = new Route { WorkspaceId = WorkspaceId };
                db.Routes.Add(entity);
            }
            dto.ApplyTo(entity);
        }
        await db.SaveChangesAsync(ct);

        var applyResult = await proxy.ApplyAsync(
            await db.Routes.Where(r => r.WorkspaceId == WorkspaceId && r.IsEnabled).ToListAsync(ct), ct);

        return Json(new
        {
            saved = true,
            apply = new { applyResult.Success, applyResult.Error, applyResult.RolledBack },
            validation = new { validation.IsValid, validation.Errors, validation.Warnings }
        });
    }
}
