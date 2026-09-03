using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Billing;

/// <summary>
/// What the platform is earning, who is burning the most of it, and whose wallet dies next — the
/// operator's answer to a question every number for already sits in <see cref="Harbora.Domain.Billing.BillingLedgerEntry"/>,
/// unasked, until this.
///
/// <para>
/// <b>Every read here ignores the tenant filter, on purpose.</b> The caller is a platform
/// administrator whose own session belongs to one workspace — usually the provider's own — asking
/// about every other workspace on the install. Read through the filter, every query below would see
/// only that one workspace's rows, and a page that quietly reported one tenant's numbers as the
/// platform's would be worse than no page at all. This is the same call
/// <see cref="WalletService"/>, <see cref="BillingTick"/> and <see cref="BillingSuspension"/> already
/// make, for the same reason — see <see cref="WalletService"/>'s own class comment.
/// </para>
///
/// <para>
/// <b>No arithmetic here duplicates <see cref="BurnRate"/> or <see cref="CostForecast"/>.</b> The
/// runway a workspace shows on this page comes from <see cref="WalletService.ForecastAsync"/> —
/// the exact call the customer's own bill makes — never a second calculation invented for an
/// operator's screen. <see cref="BurnRate"/> and <see cref="CostForecast"/> were pulled out of the
/// low-balance warning on 2026-08-18 for precisely this reason: before that, the warning and the
/// forecast were two guesses that could quietly disagree, and a customer who saw one number on their
/// bill and a different one from a support agent reading this page would have no way to tell which
/// of the two was wrong. There is only one.
/// </para>
///
/// <para>
/// Grouped explicitly by <c>WorkspaceId</c> everywhere a total is broken out by workspace, rather
/// than trusted to fall out of the filter — the tenant-filter trap this whole feature exists to
/// avoid does not stop mattering just because <see cref="Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters{TEntity}(IQueryable{TEntity})"/>
/// was called once. A query that silently drops its <c>GroupBy</c> would read as a report that
/// still runs and still renders — a smaller number, not an error — which is exactly the "success for
/// work it never did" defect class the rest of this codebase already refuses to add to.
/// </para>
/// </summary>
public sealed class RevenueReport(
    HarboraDbContext db,
    WalletService wallets,
    ISystemClock clock,
    IOptions<BillingOptions> billing)
{
    /// <summary>How many calendar months the monthly-totals section shows, the month in progress included.</summary>
    public const int MonthsShown = 6;

    /// <summary>How many workspaces the burn ranking names, at most — see its own doc comment for why fewer is honest.</summary>
    public const int TopWorkspaceCount = 10;

    /// <summary>The trailing window "last-30-days burn" is measured over.</summary>
    public const int BurnWindowDays = 30;

    public async Task<RevenueReportResult> BuildAsync(CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var thisMonthStart = TopOfMonth(now);

        // Minted once and reused for every month below, rather than re-read six times: an install
        // running this report is not expected to carry more vouchers than fit comfortably in memory,
        // the same bet EnvironmentPlacementReport already makes about the tables it reads whole.
        //
        // Two sets, not one: every trial-credit id is also a voucher id, so a credit line's id is
        // checked against trialVoucherIds FIRST — a trial credit must never be counted twice, once
        // under its own name and again as an ordinary support voucher. See MonthRowAsync.
        var voucherRows = await db.BillingVouchers.AsNoTracking()
            .Select(v => new { v.Id, v.IsTrialCredit }).ToListAsync(ct);
        var voucherIds = voucherRows.Select(v => v.Id).ToHashSet();
        var trialVoucherIds = voucherRows.Where(v => v.IsTrialCredit).Select(v => v.Id).ToHashSet();

        // ---- Q1 & Q4: charged total and credits issued, per calendar month, the last six ----
        //
        // Oldest first, so the page reads left-to-right the way a trend line does.
        var monthlyRevenue = new List<MonthlyRevenueRow>();
        for (var i = MonthsShown - 1; i >= 0; i--)
        {
            var from = thisMonthStart.AddMonths(-i);
            var to = from.AddMonths(1);
            monthlyRevenue.Add(await MonthRowAsync(from, to, voucherIds, trialVoucherIds, ct));
        }

        // ---- Q2 & Q3: the top workspaces by 30-day burn, each with balance, runway and suspension ----
        //
        // Filtered to strictly positive burn before ranking, and never padded to ten: a workspace
        // that has burned nothing in the window is not one of "the top workspaces by burn" and
        // listing it as though it were would be inventing a rank nobody earned, the same shape of
        // dishonesty as a fabricated zero.
        var thirtyDaysAgo = now.AddDays(-BurnWindowDays);
        var burns = await db.BillingLedger.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.BillingHour >= thirtyDaysAgo && l.BillingHour <= now
                        && (l.Kind == Harbora.Domain.Billing.LedgerKind.Charge
                            || l.Kind == Harbora.Domain.Billing.LedgerKind.PlanMinimumTopUp))
            .GroupBy(l => l.WorkspaceId)
            .Select(g => new { WorkspaceId = g.Key, BurnMinor = -g.Sum(l => l.AmountMinor) })
            .Where(x => x.BurnMinor > 0)
            .OrderByDescending(x => x.BurnMinor)
            .Take(TopWorkspaceCount)
            .ToListAsync(ct);

        var topWorkspaces = new List<WorkspaceBurnRow>();
        foreach (var burn in burns)
        {
            // Workspace carries no query filter of its own (see HarboraDbContext) — read plainly,
            // the same way EnvironmentPlacementReport reads it.
            var workspace = await db.Workspaces.AsNoTracking()
                .Where(w => w.Id == burn.WorkspaceId)
                .Select(w => new { w.Id, w.Name, w.Slug, w.IsDefault, w.IsSuspended, w.SuspendedReason })
                .FirstOrDefaultAsync(ct);

            // The provider's own workspace is exempt from billing everywhere else in this codebase
            // (BillingTick, BillingGate, BillingSuspension, ResourceCreationBilling all skip it) —
            // it should never actually appear here, since nothing ever charges it, but the check is
            // kept rather than trusted to the ledger's own silence, the same defence-in-depth
            // EnvironmentPlacementReport's own comment on IgnoreQueryFilters describes.
            if (workspace is null || workspace.IsDefault) continue;

            var wallet = await db.Wallets.IgnoreQueryFilters().AsNoTracking()
                .Where(w => w.WorkspaceId == burn.WorkspaceId)
                .Select(w => new { w.BalanceMinor, w.Currency })
                .FirstOrDefaultAsync(ct);

            // The one and only place a runway is computed: call it, do not re-derive it. The period
            // passed is the month in progress, the same period the customer's own bill would be
            // showing right now — it only shapes SpentSoFarMinor/ProjectedPeriodTotalMinor, neither
            // of which this page reads; the burn rate and runway themselves depend on the ledger's
            // whole history, not on this window.
            var forecast = await wallets.ForecastAsync(
                burn.WorkspaceId, thisMonthStart, thisMonthStart.AddMonths(1), ct);

            topWorkspaces.Add(new WorkspaceBurnRow(
                burn.WorkspaceId, workspace.Name, workspace.Slug,
                wallet?.BalanceMinor ?? 0, wallet?.Currency ?? billing.Value.CurrencyOrDefault,
                burn.BurnMinor, workspace.IsSuspended, workspace.SuspendedReason,
                forecast));
        }

        return new RevenueReportResult(monthlyRevenue, topWorkspaces, now);
    }

    /// <summary>
    /// One calendar month's charges and credits, credits split three ways by whether the ledger
    /// line's id belongs to a voucher, and if so, whether that voucher is the platform's own trial
    /// credit — the same idempotency key <see cref="VoucherService"/> mints the credit under (see its
    /// own doc comment: "a voucher is the idempotency key for its one credit line"). Nothing on
    /// <see cref="Harbora.Domain.Billing.BillingLedgerEntry"/> itself says "this came from a voucher"
    /// or "this voucher was a trial credit" any louder than that, so the id is the only honest way to
    /// tell the three apart — never the free-text note, which an operator can type anything into.
    /// </summary>
    private async Task<MonthlyRevenueRow> MonthRowAsync(
        DateTimeOffset from, DateTimeOffset to,
        HashSet<Guid> voucherIds, HashSet<Guid> trialVoucherIds, CancellationToken ct)
    {
        var charges = await db.BillingLedger.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.BillingHour >= from && l.BillingHour < to
                        && (l.Kind == Harbora.Domain.Billing.LedgerKind.Charge
                            || l.Kind == Harbora.Domain.Billing.LedgerKind.PlanMinimumTopUp))
            .Select(l => l.AmountMinor)
            .ToListAsync(ct);

        var credits = await db.BillingLedger.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.BillingHour >= from && l.BillingHour < to
                        && l.Kind == Harbora.Domain.Billing.LedgerKind.Credit)
            .Select(l => new { l.Id, l.AmountMinor })
            .ToListAsync(ct);

        // Trial credits checked first and excluded from the other two buckets: every trial-credit id
        // is also a voucher id, so testing "is this a voucher" before "is this a trial credit" would
        // fold the platform's own automatic grant into "support voucher" — the exact conflation this
        // three-way split exists to refuse.
        var trialCredits = credits.Where(c => trialVoucherIds.Contains(c.Id)).ToList();
        var voucherCredits = credits.Where(c => voucherIds.Contains(c.Id) && !trialVoucherIds.Contains(c.Id)).ToList();
        var adminCredits = credits.Where(c => !voucherIds.Contains(c.Id)).ToList();

        return new MonthlyRevenueRow(
            Label(from),
            charges.Count > 0,
            // Charges are stored negative — see BillingLedgerEntry.AmountMinor — flipped once, here,
            // to the positive figure a revenue total actually is. Trial credits are never part of
            // this figure: they are a credit line, and the only thing this report ever counts as
            // income is a Charge or PlanMinimumTopUp line, so a trial credit is excluded from revenue
            // by the same rule that already excludes every other kind of credit — this split only
            // makes WHICH kind of credit visible, not whether it counts as income.
            -charges.Sum(x => x),
            adminCredits.Count, adminCredits.Sum(c => c.AmountMinor),
            voucherCredits.Count, voucherCredits.Sum(c => c.AmountMinor),
            trialCredits.Count, trialCredits.Sum(c => c.AmountMinor));
    }

    /// <summary>The <c>yyyy-MM</c> label a month is grouped and shown under — invariant, like the bill's own.</summary>
    private static string Label(DateTimeOffset from) =>
        from.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The first instant of the UTC calendar month the given instant falls in.</summary>
    private static DateTimeOffset TopOfMonth(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }
}

/// <summary>
/// One calendar month: what the platform charged across every workspace, and what it credited back
/// in, split by source.
/// </summary>
/// <param name="Month"><c>yyyy-MM</c>, invariant — the same label the customer's own bill uses.</param>
/// <param name="HasChargeRows">
/// False means no workspace was charged anything this month — not "charged nothing", a month with
/// no ledger rows at all. The render must say so explicitly rather than print a bare zero, the same
/// rule <see cref="Harbora.Infrastructure.Projects.EnvironmentPlacementReport"/> already keeps for
/// an empty section.
/// </param>
/// <param name="ChargedTotalMinor">Positive minor units — the ledger's own negative sign flipped once, at read time.</param>
/// <param name="AdminCreditCount">
/// How many credit lines this month were not a voucher redemption — i.e. an administrator's own
/// "credit" action from the tenant console. Zero is named by this count being zero, not inferred
/// from <see cref="AdminCreditsMinor"/> being zero, which a large negative-then-positive wash could
/// also produce in principle.
/// </param>
/// <param name="VoucherCreditCount">
/// How many credit lines this month were a support-issued voucher redemption — never counting the
/// platform's own trial credit, which is <see cref="TrialCreditCount"/> instead.
/// </param>
/// <param name="TrialCreditCount">
/// How many credit lines this month were the platform's own automatic signup credit (sub-project
/// 1.9) — a voucher redemption structurally, by <see cref="Harbora.Domain.Billing.BillingVoucher.IsTrialCredit"/>,
/// but not a purchase and not a support voucher, and never folded into either of the other two
/// counts. Excluded from revenue for the same reason every credit is: <see cref="ChargedTotalMinor"/>
/// only ever sums <c>Charge</c>/<c>PlanMinimumTopUp</c> lines.
/// </param>
public sealed record MonthlyRevenueRow(
    string Month,
    bool HasChargeRows,
    long ChargedTotalMinor,
    int AdminCreditCount,
    long AdminCreditsMinor,
    int VoucherCreditCount,
    long VoucherCreditsMinor,
    int TrialCreditCount,
    long TrialCreditsMinor)
{
    public long TotalCreditsMinor => AdminCreditsMinor + VoucherCreditsMinor + TrialCreditsMinor;
    public bool HasAnyCredits => AdminCreditCount > 0 || VoucherCreditCount > 0 || TrialCreditCount > 0;
}

/// <summary>
/// One of the workspaces burning the most over the trailing 30 days, with what it has left and when
/// it runs out at that rate.
/// </summary>
/// <param name="Burn30DayMinor">
/// The sum of every charge and plan-minimum top-up line in the trailing 30 days — a fact about what
/// already happened, unlike <see cref="Forecast"/>'s hourly rate, which is a claim about what keeps
/// happening. The two can legitimately differ: a workspace that ran heavily for three weeks and was
/// stopped yesterday still shows a large 30-day burn here while <see cref="Forecast"/> correctly
/// shows nothing currently costing money — see <see cref="WalletService.ForecastAsync"/>'s own doc
/// comment on why the burn rate is one hour, not an average.
/// </param>
/// <param name="Forecast">
/// Read straight off <see cref="WalletService.ForecastAsync"/> — never recomputed. Its
/// <see cref="Harbora.Infrastructure.Billing.CostForecast.HasEnoughHistory"/> flag is what makes a
/// workspace with under <see cref="WalletService.MinimumHistoryHours"/> of billed history say so
/// honestly here, exactly as it already does on that workspace's own bill.
/// </param>
public sealed record WorkspaceBurnRow(
    Guid WorkspaceId,
    string WorkspaceName,
    string WorkspaceSlug,
    long BalanceMinor,
    string Currency,
    long Burn30DayMinor,
    bool Suspended,
    SuspensionReason SuspendedReason,
    CostForecast Forecast);

/// <summary>The full platform revenue report: what came in each month, and who is burning the most of it right now.</summary>
public sealed record RevenueReportResult(
    IReadOnlyList<MonthlyRevenueRow> MonthlyRevenue,
    IReadOnlyList<WorkspaceBurnRow> TopWorkspacesByBurn,
    DateTimeOffset GeneratedAt);
