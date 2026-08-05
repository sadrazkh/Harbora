using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Tenancy;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Shows the current workspace's usage vs its plan (for any user) and lets the provider
/// (Owner/Admin) define the plans + instance sizes offered to customers.
/// </summary>
[Authorize]
[Route("plans")]
public sealed class PlansController(HarboraDbContext db, IQuotaService quota, ICurrentUser currentUser) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private bool IsProvider => User.IsInRole("Owner") || User.IsInRole("Admin");

    // Same reasoning as Servers: this is the platform's plan administration, not a price list.
    [HttpGet("")]
    [Authorize(Policy = Capabilities.PlansManage)]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Plans";
        var vm = new PlansPageViewModel
        {
            Usage = await quota.GetUsageAsync(WorkspaceId, ct),
            IsProvider = IsProvider,
            // Withdrawn plans are shown to an administrator: they still have tenants' history
            // attached and hiding them makes a plan look deleted when it is not.
            Plans = await db.Plans.Where(p => p.IsEnabled || IsProvider)
                .OrderBy(p => p.MonthlyPrice).ToListAsync(ct),
            Sizes = await db.InstanceSizes.Where(s => s.IsEnabled).OrderBy(s => s.SortOrder).ToListAsync(ct)
        };

        // Who a limit change would already be biting. Shown because lowering a limit does not take
        // anything away — so without this it is a decision whose effect nobody sees.
        //
        // Which limits count is a rule of its own: this list checked apps, databases and CPU, and
        // skipped memory and disk, so halving a plan's memory produced no visible effect anywhere.
        if (IsProvider)
        {
            foreach (var ws in await db.Workspaces.AsNoTracking().ToListAsync(ct))
            {
                var usage = await quota.GetUsageAsync(ws.Id, ct);
                vm.Overages.AddRange(PlanOverage.For(usage)
                    .Select(b => new TenantOverPlanViewModel(ws.Name, usage.PlanName, b)));
            }
        }

        return View(vm);
    }

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.PlansManage)]
    public async Task<IActionResult> CreatePlan(
        string name, int maxApps, int maxServices, long maxMemoryMb, double maxCpu,
        long maxDiskGb, string? allowedSizeKeys, decimal monthlyPrice, CancellationToken ct)
    {
        if (!IsProvider) return Forbid();
        db.Plans.Add(new Plan
        {
            Name = name,
            NameFa = name,
            MaxApps = maxApps,
            MaxServices = maxServices,
            MaxMemoryBytes = maxMemoryMb * 1024 * 1024,
            MaxCpuCores = maxCpu,
            // The form never used to set this, so every plan carried a disk limit of zero while the
            // screen showed a column for it.
            MaxDiskBytes = maxDiskGb * 1024 * 1024 * 1024,
            AllowedSizeKeys = allowedSizeKeys ?? "",
            MonthlyPrice = monthlyPrice
        });
        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Corrects a plan. Plans could only be created, so a wrong limit or a changed price meant a new
    /// plan and moving every tenant by hand.
    /// </summary>
    [HttpPost("{id:guid}")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.PlansManage)]
    public async Task<IActionResult> UpdatePlan(
        Guid id, string name, int maxApps, int maxServices, long maxMemoryMb, double maxCpu,
        long maxDiskGb, string? allowedSizeKeys, decimal monthlyPrice, CancellationToken ct)
    {
        if (!IsProvider) return Forbid();

        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null) return NotFound();

        plan.Name = name;
        plan.MaxApps = maxApps;
        plan.MaxServices = maxServices;
        plan.MaxMemoryBytes = maxMemoryMb * 1024 * 1024;
        plan.MaxCpuCores = maxCpu;
        plan.MaxDiskBytes = maxDiskGb * 1024 * 1024 * 1024;
        plan.AllowedSizeKeys = allowedSizeKeys ?? "";
        plan.MonthlyPrice = monthlyPrice;
        await db.SaveChangesAsync(ct);

        // Nothing is taken away from tenants who are already over the new limit — a plan change must
        // not delete somebody's apps. They keep what they have and cannot add more, and the list
        // says who they are so it is a decision rather than a surprise.
        TempData["Message"] = "Plan updated. Tenants already over the new limits keep what they have.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Adds a resource tier.
    ///
    /// Tiers were seeded and then read-only forever: the entity's own comment says the provider can
    /// add custom sizes and there was nowhere to do it. That mattered little while a tier was CPU
    /// and memory; it matters now that it carries a disk figure, because a field nobody can set is
    /// a field that stays at whatever it was seeded with.
    /// </summary>
    [HttpPost("sizes")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.PlansManage)]
    public async Task<IActionResult> CreateSize(
        string key, string name, double cpuCores, long memoryMb, long diskGb, int sortOrder,
        CancellationToken ct)
    {
        var slug = Harbora.Infrastructure.Tenancy.InstanceSizeKey.Normalise(key);
        if (slug is null)
        {
            TempData["Error"] = "A size needs a key: lowercase letters, digits and dashes.";
            return RedirectToAction(nameof(Index));
        }

        // Checked rather than left to the unique index, which surfaces as a 500 on a page somebody
        // was using correctly.
        if (await db.InstanceSizes.AnyAsync(s => s.Key == slug, ct))
        {
            TempData["Error"] = $"A size with the key '{slug}' already exists.";
            return RedirectToAction(nameof(Index));
        }

        db.InstanceSizes.Add(new InstanceSize
        {
            Key = slug,
            Name = string.IsNullOrWhiteSpace(name) ? slug : name.Trim(),
            NameFa = string.IsNullOrWhiteSpace(name) ? slug : name.Trim(),
            CpuCores = cpuCores,
            MemoryBytes = memoryMb * 1024 * 1024,
            DiskBytes = diskGb * 1024 * 1024 * 1024,
            SortOrder = sortOrder
        });
        await db.SaveChangesAsync(ct);

        TempData["Message"] = $"Size '{slug}' added.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Corrects a tier.
    ///
    /// The key is deliberately not editable: apps and databases store it, and renaming it would
    /// leave every one of them pointing at a size that no longer exists — silently, since a missing
    /// size reads as "no limit".
    /// </summary>
    [HttpPost("sizes/{id:guid}")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.PlansManage)]
    public async Task<IActionResult> UpdateSize(
        Guid id, string name, double cpuCores, long memoryMb, long diskGb, int sortOrder,
        CancellationToken ct)
    {
        var size = await db.InstanceSizes.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (size is null) return NotFound();

        size.Name = string.IsNullOrWhiteSpace(name) ? size.Name : name.Trim();
        size.CpuCores = cpuCores;
        size.MemoryBytes = memoryMb * 1024 * 1024;
        size.DiskBytes = diskGb * 1024 * 1024 * 1024;
        size.SortOrder = sortOrder;
        await db.SaveChangesAsync(ct);

        // Says what it does not do. Instances already on this tier keep the figures they were given
        // — they carry their own copy — so a change here is about what happens next, not a resize
        // of everything running.
        TempData["Message"] =
            $"'{size.Name}' updated. Instances already on it keep their current limits until they are resized.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Stops offering a tier. Refused while anything is on it, for the same reason a plan is: a
    /// size that vanished reads as "no limit" everywhere it was named.
    /// </summary>
    [HttpPost("sizes/{id:guid}/disable")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.PlansManage)]
    public async Task<IActionResult> SetSizeEnabled(Guid id, bool enabled, CancellationToken ct)
    {
        var size = await db.InstanceSizes.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (size is null) return NotFound();

        if (!enabled)
        {
            var apps = await db.Apps.IgnoreQueryFilters().CountAsync(a => a.InstanceSizeKey == size.Key, ct);
            var services = await db.ManagedServices.IgnoreQueryFilters()
                .CountAsync(s => s.InstanceSizeKey == size.Key, ct);

            if (apps + services > 0)
            {
                TempData["Error"] = $"{apps + services} resource(s) are on '{size.Name}'. " +
                    "Move them first, or their limits would silently read as unlimited.";
                return RedirectToAction(nameof(Index));
            }
        }

        size.IsEnabled = enabled;
        await db.SaveChangesAsync(ct);
        TempData["Message"] = enabled ? $"'{size.Name}' is offered again." : $"'{size.Name}' is no longer offered.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Takes a plan out of circulation. Refused while tenants are on it: a workspace whose plan
    /// vanished falls back to the default one, which is a silent change to what they are allowed.
    /// </summary>
    [HttpPost("{id:guid}/disable")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.PlansManage)]
    public async Task<IActionResult> SetEnabled(Guid id, bool enabled, CancellationToken ct)
    {
        if (!IsProvider) return Forbid();

        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null) return NotFound();

        var onIt = await db.Workspaces.CountAsync(w => w.PlanId == id, ct);
        if (!enabled && onIt > 0)
        {
            TempData["Error"] = $"{onIt} tenant(s) are on this plan. Move them first, or it would " +
                                "silently change what they are allowed.";
            return RedirectToAction(nameof(Index));
        }

        plan.IsEnabled = enabled;
        await db.SaveChangesAsync(ct);
        TempData["Message"] = enabled ? "Plan is available again." : "Plan withdrawn from new tenants.";
        return RedirectToAction(nameof(Index));
    }
}
