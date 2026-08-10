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
    Harbora.Infrastructure.Services.ServiceUsageService usage,
    Harbora.Infrastructure.Security.ProjectAccessService access,
    EnvironmentCloner cloner,
    IAuditLogger audit,
    ICurrentUser currentUser) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private static bool IsFa =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Projects";

        // A workspace created before projects existed, or one that has never had a project, gets one
        // here rather than showing an empty page that no button can fix.
        await projects.EnsureDefaultEnvironmentAsync(WorkspaceId, ct);

        var query = db.Projects.Where(p => p.WorkspaceId == WorkspaceId);

        // Only the projects this person has been put on. Null means every project — the common
        // case, and not the same thing as an empty list.
        if (await access.VisibleProjectIdsAsync(ct) is { } visible)
            query = query.Where(p => visible.Contains(p.Id));

        var list = await query
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

        // Typing the id into the URL is the obvious thing to try, so the page checks rather than
        // relying on the list not linking to it.
        if (await access.VisibleProjectIdsAsync(ct) is { } visible && !visible.Contains(project.Id))
            return null;

        var environments = project.Environments.OrderByDescending(e => e.IsDefault).ThenBy(e => e.Name).ToList();
        var selected = environments.FirstOrDefault(e => e.Id == environmentId) ?? environments.FirstOrDefault();

        var vm = new ProjectDetailsViewModel
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

        // Worked out here, where the protector is: a connection string is stored encrypted, so the
        // page cannot answer "what is this connected to?" by reading the value itself.
        vm.Connections = usage.ConnectionsFor(vm.Services, vm.Databases.Select(d => d.ContainerName));
        return vm;
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

        try
        {
            var (project, _) = await projects.CreateAsync(WorkspaceId, name, null, ct);
            return RedirectToAction(nameof(Details), new { id = project.Id });
        }
        catch (QuotaRefusedException ex)
        {
            TempData["Error"] = IsFa ? ex.ReasonFa ?? ex.Message : ex.Message;
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("{id:guid}/environments")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsCreate)]
    public async Task<IActionResult> AddEnvironment(Guid id, string name, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == id && p.WorkspaceId == WorkspaceId, ct))
            return NotFound();

        try
        {
            var environment = await projects.AddEnvironmentAsync(WorkspaceId, id, name, ct);
            return RedirectToAction(nameof(Details), new { id, environmentId = environment.Id });
        }
        catch (QuotaRefusedException ex)
        {
            TempData["Error"] = IsFa ? ex.ReasonFa ?? ex.Message : ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }
    }

    /// <summary>
    /// Copies an environment: the same applications and databases, with new names, new database
    /// passwords, empty volumes and none of the original's domains.
    ///
    /// The plan is worked out and shown before anything is created — a page that starts making
    /// eleven containers and stops at the eighth is worse than one that says no.
    /// </summary>
    [HttpGet("{id:guid}/environments/{environmentId:guid}/clone")]
    [Authorize(Policy = Capabilities.AppsCreate)]
    public async Task<IActionResult> CloneEnvironment(
        Guid id, Guid environmentId, string? name, CancellationToken ct)
    {
        var environment = await db.Environments.Include(e => e.Project)
            .FirstOrDefaultAsync(e => e.Id == environmentId && e.ProjectId == id
                                      && e.WorkspaceId == WorkspaceId, ct);
        if (environment is null) return NotFound();

        if (!await access.AllowsAsync(new ResourcePlacement(id, environmentId), Capabilities.AppsCreate, ct))
            return NotFound();

        var desired = string.IsNullOrWhiteSpace(name) ? $"{environment.Name} copy" : name.Trim();

        ViewData["Title"] = IsFa ? "کپی محیط" : "Copy environment";
        ViewBag.Source = environment;
        ViewBag.DesiredName = desired;
        ViewBag.Plan = await cloner.PlanAsync(WorkspaceId, environmentId, desired, ct);
        return View();
    }

    [HttpPost("{id:guid}/environments/{environmentId:guid}/clone")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsCreate)]
    public async Task<IActionResult> CreateClone(
        Guid id, Guid environmentId, string name, CancellationToken ct)
    {
        if (!await db.Environments.AnyAsync(e => e.Id == environmentId && e.ProjectId == id
                                                 && e.WorkspaceId == WorkspaceId, ct))
            return NotFound();

        if (!await access.AllowsAsync(new ResourcePlacement(id, environmentId), Capabilities.AppsCreate, ct))
            return NotFound();

        var outcome = await cloner.CloneAsync(WorkspaceId, environmentId, name ?? "", ct);

        if (!outcome.Ok)
        {
            TempData["Error"] = outcome.Reason;
            return RedirectToAction(nameof(CloneEnvironment), new { id, environmentId, name });
        }

        await audit.LogAsync("environment.cloned", "environment", outcome.EnvironmentId!.Value.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            metadataJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                from = environmentId,
                apps = outcome.Plan!.Apps.Count,
                databases = outcome.Plan.Services.Count
            }),
            ct: ct);

        TempData["Message"] = IsFa
            ? $"«{outcome.Plan.EnvironmentName}» ساخته شد. رمز دیتابیس‌ها تازه است و والیوم‌ها خالی‌اند."
            : $"{outcome.Plan.EnvironmentName} was created. The databases have new passwords and the volumes are empty.";

        return RedirectToAction(nameof(Details), new { id, environmentId = outcome.EnvironmentId });
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
