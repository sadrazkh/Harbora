using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Tenancy;
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

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Plans";
        var vm = new PlansPageViewModel
        {
            Usage = await quota.GetUsageAsync(WorkspaceId, ct),
            IsProvider = IsProvider,
            Plans = await db.Plans.Where(p => p.IsEnabled).OrderBy(p => p.MonthlyPrice).ToListAsync(ct),
            Sizes = await db.InstanceSizes.Where(s => s.IsEnabled).OrderBy(s => s.SortOrder).ToListAsync(ct)
        };
        return View(vm);
    }

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreatePlan(
        string name, int maxApps, int maxServices, long maxMemoryMb, double maxCpu,
        string? allowedSizeKeys, decimal monthlyPrice, CancellationToken ct)
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
            AllowedSizeKeys = allowedSizeKeys ?? "",
            MonthlyPrice = monthlyPrice
        });
        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index));
    }
}
