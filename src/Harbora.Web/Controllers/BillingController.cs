using System.Globalization;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Billing;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Billing;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Harbora.Web.Controllers;

/// <summary>
/// The customer's own bill: what is left on the account, and where the last month of it went.
///
/// <para>
/// Open to every authenticated member of the workspace rather than to an administrator, and
/// deliberately: this is the page somebody opens when their app has stopped and they want to know
/// why, and a bill only a Workspace Admin can read is a bill that gets asked for over email instead.
/// It shows nothing anybody in the workspace could not already infer from what they are running.
/// </para>
///
/// <para>
/// <b>The workspace comes from the signed-in session and from nowhere else.</b> Not a route value,
/// not a query parameter — <see cref="WalletService"/> reads unfiltered by design, so a workspace id
/// taken from the URL would hand any authenticated customer any other customer's bill.
/// </para>
/// </summary>
[Authorize]
[Route("billing")]
public sealed class BillingController(
    HarboraDbContext db,
    WalletService wallets,
    VoucherService vouchers,
    ICurrentUser currentUser,
    IAuditLogger audit,
    Microsoft.Extensions.Options.IOptions<BillingOptions> billing,
    ISystemClock clock,
    WorkspaceBudgetService budgets,
    BillingSuspension suspension) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;

    /// <summary>
    /// One month of the bill. <paramref name="month"/> is <c>yyyy-MM</c>; anything else, including
    /// nothing, shows the month in progress.
    /// </summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(string? month, CancellationToken ct)
    {
        ViewData["Title"] = "Billing";

        var (from, to) = MonthOf(month);

        var workspace = await db.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == WorkspaceId, ct);

        // Read with the explicit predicate on top of the tenant filter — belt and braces, the way
        // every other controller on this panel reads a row it was given an id for.
        var wallet = await db.Wallets.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkspaceId == WorkspaceId, ct);

        var credits = await db.BillingLedger.AsNoTracking()
            .Where(l => l.WorkspaceId == WorkspaceId
                        && l.Kind == LedgerKind.Credit
                        && l.BillingHour >= from
                        && l.BillingHour < to)
            .OrderByDescending(l => l.BillingHour).ThenByDescending(l => l.CreatedAt)
            .Select(l => new BillingCreditRow(l.BillingHour, l.AmountMinor, l.Description))
            .ToListAsync(ct);

        var adjustments = await db.BillingLedger.AsNoTracking()
            .Where(l => l.WorkspaceId == WorkspaceId
                        && l.Kind == LedgerKind.Adjustment
                        && l.BillingHour >= from
                        && l.BillingHour < to)
            .OrderByDescending(l => l.BillingHour).ThenByDescending(l => l.CreatedAt)
            .Select(l => new BillingAdjustmentRow(l.BillingHour, l.AmountMinor, l.Description))
            .ToListAsync(ct);

        var thisMonth = Label(MonthOf(null).From);
        var period = Label(from);
        var budgetState = await budgets.GetAsync(WorkspaceId, clock.UtcNow, ct);
        var suspended = workspace?.IsSuspended ?? false;

        // A forecast only for the month in progress — a closed month has already happened, and
        // projecting it further is not a forecast, it is arithmetic the ledger has already settled.
        // And never for a suspended workspace: its most recently charged hour will catch up with
        // that suspension the next time the tick runs, but showing a number in the meantime that
        // assumes today's rate keeps going is exactly the "forecast it as though it were still
        // burning" failure this feature is required to refuse.
        var forecast = period == thisMonth && !suspended
            ? await wallets.ForecastAsync(WorkspaceId, from, to, ct)
            : null;

        return View(new BillingPageViewModel
        {
            WorkspaceName = workspace?.Name ?? string.Empty,
            HasWallet = wallet is not null,
            BalanceMinor = wallet?.BalanceMinor ?? 0,
            // The wallet's own code where there is one, and the install's setting where there is
            // not. A workspace the meter has never reached has no row to read a currency off, and
            // printing the shipped default at a provider who sells in something else would label
            // every figure on the page with the wrong money.
            Currency = wallet?.Currency ?? billing.Value.CurrencyOrDefault,
            Suspended = suspended,
            SuspendedForNoBalance = workspace?.SuspendedReason == SuspensionReason.NoBalance,
            SuspendedForSpendLimit = workspace?.SuspendedReason == SuspensionReason.SpendLimit,
            CurrentMonthSpendMinor = budgetState.SpendMinor,
            MonthlyBudgetMinor = budgetState.BudgetMinor,
            MonthlySpendLimitMinor = budgetState.SpendLimitMinor,
            SpendLimitRemainingMinor = budgetState.RemainingMinor,
            BudgetExceeded = budgetState.BudgetExceeded,
            CanManageBudget = User.FindFirstValue(Harbora.Web.Infrastructure.HarboraClaims.WorkspaceRole)
                == WorkspaceRole.Admin.ToString(),
            Period = period,
            PreviousPeriod = Label(from.AddMonths(-1)),
            // No link forward out of the month in progress: there is no bill after it yet, and a
            // link to an empty page reads as a page that failed to load.
            NextPeriod = string.CompareOrdinal(period, thisMonth) < 0 ? Label(from.AddMonths(1)) : null,
            Costs = await wallets.BreakdownAsync(WorkspaceId, from, to, ct),
            Credits = credits,
            Adjustments = adjustments,
            Forecast = forecast,
            // The same flag BillingTick already writes when a low-balance warning is outstanding —
            // reused as-is rather than a second "is this running low" threshold invented for this
            // page. See BillingPageViewModel.LowBalanceWarningActive.
            LowBalanceWarningActive = wallet?.LowBalanceWarnedAtBalanceMinor is not null
        });
    }

    [HttpPost("budget")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBudget(string? monthlyBudget, string? monthlySpendLimit, CancellationToken ct)
    {
        if (User.FindFirstValue(Harbora.Web.Infrastructure.HarboraClaims.WorkspaceRole)
            != WorkspaceRole.Admin.ToString()) return Forbid();

        if (!Harbora.Web.Infrastructure.MinorUnits.TryParseRate(monthlyBudget, out var budgetMinor)
            || !Harbora.Web.Infrastructure.MinorUnits.TryParseRate(monthlySpendLimit, out var limitMinor)
            || budgetMinor is < 0 || limitMinor is < 0)
        {
            TempData["Error"] = "Budget and spend limit must be empty or non-negative money amounts.";
            return RedirectToAction(nameof(Index));
        }
        if (budgetMinor is > 0 && limitMinor is > 0 && budgetMinor > limitMinor)
        {
            TempData["Error"] = "The warning budget cannot be higher than the hard spend limit.";
            return RedirectToAction(nameof(Index));
        }

        var workspace = await db.Workspaces.FirstAsync(w => w.Id == WorkspaceId, ct);
        workspace.MonthlyBudgetMinor = budgetMinor is > 0 ? budgetMinor : null;
        workspace.MonthlySpendLimitMinor = limitMinor is > 0 ? limitMinor : null;
        await db.SaveChangesAsync(ct);
        var resumed = await suspension.ResumeAsync(workspace.Id, ct);
        await audit.LogAsync("billing.budget_updated", "workspace", workspace.Id.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            metadataJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                workspace.MonthlyBudgetMinor,
                workspace.MonthlySpendLimitMinor,
                resumed.WorkspaceSuspended
            }), ct: ct);
        TempData["Message"] = "Monthly budget controls saved.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Redeems a single-use voucher into the workspace carried by the signed-in session.</summary>
    [HttpPost("voucher")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("voucher")]
    public async Task<IActionResult> RedeemVoucher(string? code, CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId || WorkspaceId == Guid.Empty)
        {
            TempData["Error"] = "Sign in to a workspace before redeeming a voucher.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var result = await vouchers.RedeemAsync(code, WorkspaceId, userId, ct);
            TempData["Message"] = result.Applied
                ? $"Voucher applied. Your balance is now {Harbora.Web.Infrastructure.MinorUnits.Format(result.BalanceMinor)}."
                : $"That voucher was already applied to this workspace. Your balance is {Harbora.Web.Infrastructure.MinorUnits.Format(result.BalanceMinor)}.";
            if (result.Failures.Count > 0) TempData["Error"] = string.Join(" ", result.Failures);

            await audit.LogAsync("billing.voucher.redeem", "voucher", result.VoucherId.ToString(),
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                metadataJson: System.Text.Json.JsonSerializer.Serialize(new
                {
                    workspaceId = WorkspaceId,
                    result.AmountMinor,
                    result.Applied
                }), ct: ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// The half-open window one <c>yyyy-MM</c> names, in UTC — the same clock the billing hour on
    /// every ledger line is stamped with, so a statement's edges land exactly between two hours
    /// rather than part-way through one.
    /// </summary>
    private (DateTimeOffset From, DateTimeOffset To) MonthOf(string? month)
    {
        var now = clock.UtcNow.ToUniversalTime();

        var from = DateTimeOffset.TryParseExact(
            month, "yyyy-MM", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? new DateTimeOffset(parsed.Year, parsed.Month, 1, 0, 0, 0, TimeSpan.Zero)
            : new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

        return (from, from.AddMonths(1));
    }

    private static string Label(DateTimeOffset from) => from.ToString("yyyy-MM", CultureInfo.InvariantCulture);
}
