using Harbora.Domain.Authorization;
using Harbora.Infrastructure.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Harbora.Web.Controllers;

/// <summary>
/// What the platform is earning, who is burning the most of it, and whose wallet dies next.
///
/// <para>
/// Read-only, and gated by the same capability the rest of the provider's billing console already
/// uses — <see cref="BillingRunsController"/>, <see cref="VouchersController"/> and
/// <see cref="TenantsController"/> are all <see cref="Capabilities.TenantsManage"/>, and this page is
/// the same kind of cross-tenant read they are, so it follows them rather than inventing a fourth
/// policy for one more provider screen. A customer's own workspace role — even
/// <see cref="Harbora.Domain.Common.WorkspaceRole.Admin"/>, the highest one a tenant can hold — never
/// carries this capability; see <see cref="WorkspaceRolePermissions"/>, which grants it to nobody.
/// </para>
///
/// <para>
/// The whole page is one call to <see cref="RevenueReport.BuildAsync"/>, which is itself built out of
/// <see cref="WalletService.ForecastAsync"/> for every runway it shows — nothing here recomputes a
/// burn rate or a runway a second way. See <see cref="RevenueReport"/>'s own class comment.
/// </para>
/// </summary>
[Authorize(Policy = Capabilities.TenantsManage)]
[Route("revenue")]
public sealed class AdminRevenueController(RevenueReport report) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Revenue";
        return View(await report.BuildAsync(ct));
    }
}
