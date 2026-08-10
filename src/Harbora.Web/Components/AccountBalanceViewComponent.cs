using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Infrastructure.Billing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Web.Components;

public sealed class AccountBalanceViewComponent(
    HarboraDbContext db,
    ICurrentUser currentUser,
    IOptions<BillingOptions> billing) : ViewComponent
{
    public sealed record Model(long BalanceMinor, string Currency, bool Compact);

    public async Task<IViewComponentResult> InvokeAsync(bool compact = false)
    {
        var workspaceId = currentUser.WorkspaceId ?? Guid.Empty;
        var wallet = await db.Wallets.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.WorkspaceId == workspaceId)
            .Select(w => new { w.BalanceMinor, w.Currency })
            .FirstOrDefaultAsync(HttpContext.RequestAborted);

        return View(new Model(
            wallet?.BalanceMinor ?? 0,
            wallet?.Currency ?? billing.Value.CurrencyOrDefault,
            compact));
    }
}
