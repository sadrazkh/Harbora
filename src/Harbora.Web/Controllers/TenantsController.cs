using System.Text.RegularExpressions;
using Harbora.Application.Abstractions;
using Harbora.Data;
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
[Authorize(Roles = "Owner,Admin")]
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

        var appCounts = await db.Apps.GroupBy(a => a.WorkspaceId).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct);
        var svcCounts = await db.ManagedServices.GroupBy(s => s.WorkspaceId).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct);
        var memCounts = await db.WorkspaceMembers.GroupBy(m => m.WorkspaceId).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct);

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

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws is null) return NotFound();
        ViewData["Title"] = ws.Name;

        var members = await db.WorkspaceMembers.Where(m => m.WorkspaceId == id)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new TenantMember(u.Id, u.Email, u.DisplayName, m.Role.ToString(), u.IsActive))
            .OrderBy(m => m.Email).ToListAsync(ct);

        return View(new TenantDetailsViewModel
        {
            WorkspaceId = ws.Id, Name = ws.Name, Slug = ws.Slug, IsDefault = ws.IsDefault, Suspended = ws.IsSuspended,
            Usage = await quota.GetUsageAsync(ws.Id, ct),
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

        if (await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == id && m.UserId == user.Id, ct))
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
        await db.WorkspaceMembers.Where(m => m.WorkspaceId == id && m.UserId == userId).ExecuteDeleteAsync(ct);
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
