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
public sealed partial class TenantsController(HarboraDbContext db, IPasswordHasher hasher) : Controller
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

    private static string Slugify(string value)
    {
        var slug = NonSlug().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "tenant-" + Guid.NewGuid().ToString("N")[..6] : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();
}
