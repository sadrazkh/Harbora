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
    ProjectDeletionService deletion,
    Harbora.Infrastructure.Services.ServiceUsageService usage,
    Harbora.Infrastructure.Security.ProjectAccessService access,
    EnvironmentCloner cloner,
    IAuditLogger audit,
    ICurrentUser currentUser) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private static bool IsFa =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    /// <summary>
    /// "2 apps: api, worker" — or past three, "5 apps: api, worker, cron and 2 more". Named, not
    /// merely counted: a refusal that only says how many are in the way gives nobody anything to act
    /// on, which is exactly what <c>ConfirmRemove</c> and the reserved-host refusals both refuse to
    /// do, and what the delete guard here did before P2 (2026-08-17 app-environment-management
    /// design) made the count worth naming.
    /// </summary>
    private string NamedList(IReadOnlyList<string> names, string kindEn, string kindFa)
    {
        const int shown = 3;
        var listed = names.Count > shown
            ? string.Join(IsFa ? "، " : ", ", names.Take(shown)) +
              (IsFa ? $" و {names.Count - shown} مورد دیگر" : $" and {names.Count - shown} more")
            : string.Join(IsFa ? "، " : ", ", names);

        return IsFa
            ? $"{names.Count} {kindFa}: {listed}"
            : $"{names.Count} {kindEn}{(names.Count == 1 ? "" : "s")}: {listed}";
    }

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
        // 5.1 (per-app grants, HARBORA-0035): a project that does not exist yet has no placement any
        // grant could name, so a member scoped to selected projects is refused here outright — the
        // same "a workspace-wide rule is out of reach of a scoped member" answer RoutesController's
        // own placement-less actions already give, applied to the one act that would otherwise hand
        // a scoped member a project nobody granted them.
        if (!await access.AllowsAsync(new ResourcePlacement(null, null), Capabilities.AppsCreate, ct))
            return NotFound();

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

        // 5.1: found by AppScopeCensusTests — a member scoped to other projects could add an
        // environment to a project nobody granted them.
        if (!await access.AllowsAsync(new ResourcePlacement(id, null), Capabilities.AppsCreate, ct))
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
            workspaceId: WorkspaceId,
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

    /// <summary>
    /// What deleting this project would destroy — every app and database in every one of its
    /// environments, named, plus what cascades with them. The same <see cref="ProjectRemovalPlan"/>
    /// <see cref="Delete"/> below reads before it does anything, so this page and that action can
    /// never end up looking at two different sets.
    /// </summary>
    [HttpGet("{id:guid}/delete")]
    [Authorize(Policy = Capabilities.AppsDelete)]
    public async Task<IActionResult> ConfirmDelete(Guid id, CancellationToken ct)
    {
        if (await access.VisibleProjectIdsAsync(ct) is { } visible && !visible.Contains(id))
            return NotFound();

        var plan = await deletion.PlanAsync(WorkspaceId, id, ct);
        if (plan is null) return NotFound();

        ViewData["Title"] = IsFa ? $"حذف {plan.Value.ProjectName}" : $"Delete {plan.Value.ProjectName}";
        ViewBag.Plan = plan.Value;
        return View();
    }

    [HttpPost("{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsDelete)]
    public async Task<IActionResult> Delete(Guid id, string? confirmName, CancellationToken ct)
    {
        // 5.1: found by AppScopeCensusTests. ConfirmDelete above only checks VISIBILITY (a Viewer
        // grant on this project can see the confirmation page, matching Details); this is the action
        // capability, so it is asked here the way every other mutation in this file asks it.
        if (!await access.AllowsAsync(new ResourcePlacement(id, null), Capabilities.AppsDelete, ct))
            return NotFound();

        var plan = await deletion.PlanAsync(WorkspaceId, id, ct);
        if (plan is null) return NotFound();

        // Refused while anything still lives here and nobody has typed the project's name back. This
        // is also the application-level half of what DeleteBehavior.Restrict backstops at the database
        // (P2, 2026-08-17 app-environment-management design): EnvironmentId is a required foreign key
        // now, so an unconfirmed delete that reached SaveChanges with a workload still attached would
        // fail as a raw constraint violation instead of the named refusal below. A typed, matching
        // confirmation is the only thing that turns this into the cascade ProjectDeletionService runs
        // — deleting a project is never a silent way to delete the apps and databases inside it.
        if (!plan.Value.IsConfirmed(confirmName))
        {
            var fragments = new List<string>();
            if (plan.Value.Apps.Count > 0)
                fragments.Add(NamedList(plan.Value.Apps.Select(a => a.Name).ToList(), "app", "اپ"));
            if (plan.Value.Databases.Count > 0)
                fragments.Add(NamedList(plan.Value.Databases.Select(d => d.Name).ToList(), "database", "دیتابیس"));
            var holds = string.Join(IsFa ? " و " : " and ", fragments);

            TempData["Error"] = IsFa
                ? $"این پروژه هنوز {holds} در خود دارد. آن‌ها را یکی‌یکی حذف کنید، یا برای حذف همه‌چیز با هم نام پروژه را در صفحه‌ی تأیید بنویسید."
                : $"This project still holds {holds}. Remove them one at a time, or delete everything " +
                  "at once by typing the project's name on the confirm page.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var outcome = await deletion.DeleteAsync(WorkspaceId, id, ct);

        if (!outcome.FullyDeleted)
        {
            // Not "Deleted" — this platform's defining defect class is a result that claims work it
            // did not do. Whatever DeleteAsync could not remove is named here, exactly the way the
            // refusal above names what blocked it in the first place.
            var fragments = new List<string>();
            if (outcome.RemainingApps.Count > 0) fragments.Add(NamedList(outcome.RemainingApps, "app", "اپ"));
            if (outcome.RemainingDatabases.Count > 0)
                fragments.Add(NamedList(outcome.RemainingDatabases, "database", "دیتابیس"));
            var holds = string.Join(IsFa ? " و " : " and ", fragments);

            TempData["Error"] = IsFa
                ? $"پروژه به‌طور کامل حذف نشد: {holds} هنوز باقی مانده‌اند. بقیه حذف شدند؛ دوباره تلاش کنید."
                : $"The project could not be fully deleted: {holds} are still there. Everything else was removed — try again.";
            return RedirectToAction(nameof(Details), new { id });
        }

        await audit.LogAsync("project.deleted", "project", id.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            workspaceId: WorkspaceId,
            metadataJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                apps = plan.Value.Apps.Count,
                databases = plan.Value.Databases.Count
            }),
            ct: ct);

        TempData["Message"] = IsFa
            ? $"«{outcome.ProjectName}» به همراه {plan.Value.Apps.Count} اپ و {plan.Value.Databases.Count} دیتابیس حذف شد."
            : $"'{outcome.ProjectName}' was deleted, along with {plan.Value.Apps.Count} app(s) and " +
              $"{plan.Value.Databases.Count} database(s).";
        return RedirectToAction(nameof(Index));
    }
}
