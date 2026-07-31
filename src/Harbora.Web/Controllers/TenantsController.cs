using System.Text.RegularExpressions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Provider console: manage the customer workspaces (tenants) hosted on this platform — create
/// them, assign a plan, suspend/resume, and manage their members. Restricted to Owners/Admins.
/// </summary>
[Authorize(Policy = Capabilities.TenantsManage)]
[Route("tenants")]
public sealed partial class TenantsController(HarboraDbContext db, IPasswordHasher hasher, IQuotaService quota) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Tenants";

        var workspaces = await db.Workspaces.OrderByDescending(w => w.IsDefault).ThenBy(w => w.Name).ToListAsync(ct);
        var plans = await db.Plans.Where(p => p.IsEnabled).OrderBy(p => p.MonthlyPrice).ToListAsync(ct);
        var planName = plans.ToDictionary(p => p.Id, p => p.Name);

        // This page IS the cross-tenant view, so it opts out of the workspace filters explicitly.
        var appCounts = await db.Apps.IgnoreQueryFilters().GroupBy(a => a.WorkspaceId).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct);
        var svcCounts = await db.ManagedServices.IgnoreQueryFilters().GroupBy(s => s.WorkspaceId).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct);
        var memCounts = await db.WorkspaceMembers.IgnoreQueryFilters().GroupBy(m => m.WorkspaceId).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct);

        var vm = new TenantsPageViewModel { Plans = plans };
        foreach (var w in workspaces)
        {
            vm.Tenants.Add(new TenantRow(
                w.Id, w.Name, w.Slug, w.IsDefault, w.PlanId,
                w.PlanId is { } pid && planName.TryGetValue(pid, out var n) ? n : "Default",
                memCounts.GetValueOrDefault(w.Id), appCounts.GetValueOrDefault(w.Id), svcCounts.GetValueOrDefault(w.Id),
                w.IsSuspended));
        }
        return View(vm);
    }

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string name, string slug, Guid? planId, CancellationToken ct)
    {
        slug = Slugify(string.IsNullOrWhiteSpace(slug) ? name : slug);
        if (await db.Workspaces.AnyAsync(w => w.Slug == slug, ct))
        {
            TempData["Error"] = "A workspace with this slug already exists.";
            return RedirectToAction(nameof(Index));
        }

        db.Workspaces.Add(new Workspace
        {
            Name = string.IsNullOrWhiteSpace(name) ? slug : name,
            Slug = slug,
            PlanId = planId,
            IsDefault = false
        });
        await db.SaveChangesAsync(ct);
        TempData["Message"] = $"Tenant '{slug}' created.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/plan")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignPlan(Guid id, Guid? planId, CancellationToken ct)
    {
        await db.Workspaces.Where(w => w.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.PlanId, planId), ct);
        TempData["Message"] = "Plan updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/suspend")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Suspend(Guid id, bool suspended, CancellationToken ct)
    {
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws is null) return NotFound();
        if (ws.IsDefault) { TempData["Error"] = "The provider workspace cannot be suspended."; return RedirectToAction(nameof(Index)); }
        ws.IsSuspended = suspended;
        await db.SaveChangesAsync(ct);
        TempData["Message"] = suspended ? "Tenant suspended." : "Tenant resumed.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Limits a person to the projects they have been granted, or lifts the limit again. Off by
    /// default and reversible: turning it off restores exactly the access they had before.
    /// </summary>
    [HttpPost("{id:guid}/members/{userId:guid}/scope")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.TenantsManage)]
    public async Task<IActionResult> SetScope(Guid id, Guid userId, bool scoped, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return NotFound();

        // An administrator is never scoped — administering a workspace you can only see half of is
        // not administering it — so saying so is better than a switch that silently does nothing.
        if (user.Role is SystemRole.Owner or SystemRole.Admin && scoped)
        {
            TempData["Error"] = "An owner or admin is not limited to projects; change their role first.";
            return RedirectToAction(nameof(Details), new { id });
        }

        user.ScopedToProjects = scoped;
        await db.SaveChangesAsync(ct);
        TempData["Message"] = scoped
            ? "Limited to the projects granted below."
            : "This person can reach every project in the workspace again.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/members/{userId:guid}/grants")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.TenantsManage)]
    public async Task<IActionResult> AddGrant(
        Guid id, Guid userId, Guid projectId, Guid? environmentId, SystemRole role, CancellationToken ct)
    {
        if (!await db.Projects.IgnoreQueryFilters().AnyAsync(p => p.Id == projectId && p.WorkspaceId == id, ct))
            return NotFound();

        // The environment has to belong to the project it is being granted within, or the grant
        // would name a pair that never occurs and quietly match nothing.
        if (environmentId is { } e
            && !await db.Environments.IgnoreQueryFilters().AnyAsync(x => x.Id == e && x.ProjectId == projectId, ct))
            return NotFound();

        var existing = await db.ProjectGrants.IgnoreQueryFilters().FirstOrDefaultAsync(
            g => g.WorkspaceId == id && g.UserId == userId
                 && g.ProjectId == projectId && g.EnvironmentId == environmentId, ct);

        // Replaced rather than added twice: two grants for the same place would leave which one
        // applies down to ordering.
        if (existing is not null) existing.Role = role;
        else
            db.ProjectGrants.Add(new Harbora.Domain.Authorization.ProjectGrant
            {
                WorkspaceId = id, UserId = userId, ProjectId = projectId,
                EnvironmentId = environmentId, Role = role
            });

        await db.SaveChangesAsync(ct);
        TempData["Message"] = "Access granted.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/grants/{grantId:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.TenantsManage)]
    public async Task<IActionResult> RemoveGrant(Guid id, Guid grantId, CancellationToken ct)
    {
        var grant = await db.ProjectGrants.IgnoreQueryFilters()
            .FirstOrDefaultAsync(g => g.Id == grantId && g.WorkspaceId == id, ct);
        if (grant is null) return NotFound();

        db.ProjectGrants.Remove(grant);
        await db.SaveChangesAsync(ct);
        TempData["Message"] = "Access removed.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws is null) return NotFound();
        ViewData["Title"] = ws.Name;

        // Platform admin acting on another workspace: scoping to their own would return nothing.
        var rows = await db.WorkspaceMembers.IgnoreQueryFilters().Where(m => m.WorkspaceId == id)
            .Join(db.Users, m => m.UserId, u => u.Id,
                  (m, u) => new { u.Id, u.Email, u.DisplayName, Role = u.Role, u.IsActive, u.ScopedToProjects })
            .OrderBy(m => m.Email).ToListAsync(ct);

        // Grants, written out as sentences: a permission nobody can read is a permission nobody
        // audits, and this screen is where an audit would start.
        var projects = await db.Projects.IgnoreQueryFilters().Where(p => p.WorkspaceId == id)
            .Include(p => p.Environments).ToListAsync(ct);
        var projectName = projects.ToDictionary(p => p.Id, p => p.Name);
        var environmentName = projects.SelectMany(p => p.Environments).ToDictionary(e => e.Id, e => e.Name);

        var grants = await db.ProjectGrants.IgnoreQueryFilters().Where(g => g.WorkspaceId == id).ToListAsync(ct);

        var members = rows.Select(r => new TenantMember(r.Id, r.Email, r.DisplayName, r.Role.ToString(), r.IsActive)
        {
            ScopedToProjects = r.ScopedToProjects,
            Grants = grants.Where(g => g.UserId == r.Id)
                .Select(g => (g.Id, Harbora.Domain.Authorization.ProjectAccess.Describe(
                    g,
                    projectName.GetValueOrDefault(g.ProjectId, "(deleted project)"),
                    g.EnvironmentId is { } e ? environmentName.GetValueOrDefault(e, "(deleted environment)") : null)))
                .ToList()
        }).ToList();

        ViewBag.Projects = projects;

        var now = DateTimeOffset.UtcNow;
        var period = new DateOnly(now.Year, now.Month, 1);
        var metered = await db.UsageRecords.AsNoTracking().FirstOrDefaultAsync(r => r.WorkspaceId == ws.Id && r.Period == period, ct);

        return View(new TenantDetailsViewModel
        {
            WorkspaceId = ws.Id, Name = ws.Name, Slug = ws.Slug, IsDefault = ws.IsDefault, Suspended = ws.IsSuspended,
            Usage = await quota.GetUsageAsync(ws.Id, ct),
            MemoryGbHours = metered?.MemoryGbHours ?? 0,
            CpuCoreHours = metered?.CpuCoreHours ?? 0,
            AppCountPeak = metered?.AppCountPeak ?? 0,
            PeriodLabel = period.ToString("yyyy-MM"),
            Members = members
        });
    }

    /// <summary>Add a customer user to the workspace (create the account if the email is new).</summary>
    [HttpPost("{id:guid}/members")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddMember(Guid id, string email, string? displayName, string? password, WorkspaceRole role, CancellationToken ct)
    {
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws is null) return NotFound();

        email = (email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email))
        {
            TempData["Error"] = "Email is required.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
        {
            if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            {
                TempData["Error"] = "A temporary password (min 8 chars) is required for a new user.";
                return RedirectToAction(nameof(Details), new { id });
            }
            user = new User
            {
                Email = email,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? email : displayName,
                PasswordHash = hasher.Hash(password),
                Role = SystemRole.Member // a tenant user, not a platform admin
            };
            db.Users.Add(user);
        }

        if (await db.WorkspaceMembers.IgnoreQueryFilters().AnyAsync(m => m.WorkspaceId == id && m.UserId == user.Id, ct))
        {
            TempData["Error"] = "This user is already a member.";
            return RedirectToAction(nameof(Details), new { id });
        }

        db.WorkspaceMembers.Add(new WorkspaceMember { Workspace = ws, User = user, Role = role });
        await db.SaveChangesAsync(ct);
        TempData["Message"] = $"Added {email} as {role}.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/members/{userId:guid}/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId, CancellationToken ct)
    {
        await db.WorkspaceMembers.IgnoreQueryFilters()
            .Where(m => m.WorkspaceId == id && m.UserId == userId).ExecuteDeleteAsync(ct);
        TempData["Message"] = "Member removed.";
        return RedirectToAction(nameof(Details), new { id });
    }

    private static string Slugify(string value)
    {
        var slug = NonSlug().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "tenant-" + Guid.NewGuid().ToString("N")[..6] : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();
}
