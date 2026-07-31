using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Projects;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Projects and their environments — the grouping the product was missing.
///
/// Everything here reads through the workspace, and every lookup is scoped by it. A project id in a
/// URL is the obvious thing to change by hand, so ownership is checked on the server for every action
/// rather than assumed from the link the user followed.
/// </summary>
[Authorize]
[Route("projects")]
public sealed class ProjectsController(
    HarboraDbContext db,
    ProjectService projects,
    ICurrentUser currentUser) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Projects";

        // A workspace created before projects existed, or one that has never had a project, gets one
        // here rather than showing an empty page that no button can fix.
        await projects.EnsureDefaultEnvironmentAsync(WorkspaceId, ct);

        var list = await db.Projects
            .Where(p => p.WorkspaceId == WorkspaceId)
            .OrderBy(p => p.Name)
            .Select(p => new ProjectSummary
            {
                Id = p.Id,
                Name = p.Name,
                Slug = p.Slug,
                Environments = p.Environments.Count,
                Services = db.Apps.Count(a => a.Environment!.ProjectId == p.Id),
                Databases = db.ManagedServices.Count(s => s.Environment!.ProjectId == p.Id),
                Unhealthy = db.Apps.Count(a => a.Environment!.ProjectId == p.Id
                                               && (a.Status == AppStatus.Crashed || a.Status == AppStatus.Failed))
            })
            .ToListAsync(ct);

        return View(list);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, Guid? environmentId, CancellationToken ct)
    {
        var vm = await LoadAsync(id, environmentId, ct);
        if (vm is null) return NotFound();

        ViewData["Title"] = vm.Project.Name;
        return View(vm);
    }

    /// <summary>
    /// Loads a project and one environment's contents. Scoped by workspace, so a project id in the
    /// URL — the obvious thing to change by hand — cannot reach another tenant's data.
    /// </summary>
    private async Task<ProjectDetailsViewModel?> LoadAsync(Guid id, Guid? environmentId, CancellationToken ct)
    {
        var project = await db.Projects
            .Include(p => p.Environments)
            .FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == WorkspaceId, ct);
        if (project is null) return null;

        var environments = project.Environments.OrderByDescending(e => e.IsDefault).ThenBy(e => e.Name).ToList();
        var selected = environments.FirstOrDefault(e => e.Id == environmentId) ?? environments.FirstOrDefault();

        return new ProjectDetailsViewModel
        {
            Project = project,
            Environments = environments,
            Selected = selected,
            // Variables and domains are loaded because the architecture view reads connections from
            // what each service is actually configured with.
            Services = selected is null
                ? []
                : await db.Apps.Where(a => a.EnvironmentId == selected.Id)
                    .Include(a => a.EnvironmentVariables)
                    .Include(a => a.Domains)
                    .OrderBy(a => a.Name).ToListAsync(ct),
            Databases = selected is null
                ? []
                : await db.ManagedServices.Where(s => s.EnvironmentId == selected.Id)
                    .OrderBy(s => s.Name).ToListAsync(ct)
        };
    }

    [HttpGet("{id:guid}/architecture")]
    public async Task<IActionResult> Architecture(Guid id, Guid? environmentId, CancellationToken ct)
    {
        var vm = await LoadAsync(id, environmentId, ct);
        if (vm is null) return NotFound();

        ViewData["Title"] = $"{vm.Project.Name} — architecture";
        return View(vm);
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsCreate)]
    public async Task<IActionResult> Create(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "A project needs a name.";
            return RedirectToAction(nameof(Index));
        }

        var (project, _) = await projects.CreateAsync(WorkspaceId, name, null, ct);
        return RedirectToAction(nameof(Details), new { id = project.Id });
    }

    [HttpPost("{id:guid}/environments")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsCreate)]
    public async Task<IActionResult> AddEnvironment(Guid id, string name, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == id && p.WorkspaceId == WorkspaceId, ct))
            return NotFound();

        var environment = await projects.AddEnvironmentAsync(WorkspaceId, id, name, ct);
        return RedirectToAction(nameof(Details), new { id, environmentId = environment.Id });
    }

    [HttpPost("{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsDelete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var project = await db.Projects.Include(p => p.Environments)
            .FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == WorkspaceId, ct);
        if (project is null) return NotFound();

        // Refused while anything still lives here. Deleting a project is not a way to delete apps and
        // databases — those have their own confirmations, and their own consequences.
        var ids = project.Environments.Select(e => e.Id).ToList();
        var services = await db.Apps.CountAsync(a => a.EnvironmentId != null && ids.Contains(a.EnvironmentId.Value), ct);
        var databases = await db.ManagedServices.CountAsync(s => s.EnvironmentId != null && ids.Contains(s.EnvironmentId.Value), ct);

        if (services + databases > 0)
        {
            TempData["Error"] =
                $"This project still holds {services} service(s) and {databases} database(s). " +
                "Remove them first — deleting a project will not delete them for you.";
            return RedirectToAction(nameof(Details), new { id });
        }

        db.Projects.Remove(project);
        await db.SaveChangesAsync(ct);
        TempData["Message"] = $"Project '{project.Name}' deleted.";
        return RedirectToAction(nameof(Index));
    }
}
