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
