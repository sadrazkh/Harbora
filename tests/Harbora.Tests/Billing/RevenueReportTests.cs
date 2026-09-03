using FluentAssertions;
using Harbora.Domain.Billing;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Billing;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests.Billing;

/// <summary>
/// What the platform earned, who is burning the most of it, and whose wallet dies next — built the
/// same way <see cref="WalletServiceTests"/> and <see cref="CostForecastTests"/> already prove
/// <see cref="WalletService"/> itself: over a context scoped to <see cref="WalletHarness.ProviderWorkspace"/>,
/// never to the workspaces the report is actually asking about, so a query that forgot
/// <c>IgnoreQueryFilters()</c> would fail here exactly as it would in production.
/// </summary>
public class RevenueReportTests
{
    private static RevenueReport Report(BillingContext db) =>
        new(WalletHarness.ProviderContext(db), WalletHarness.Wallets(db), WalletHarness.Clock,
            Options.Create(new BillingOptions { Currency = BillingOptions.DefaultCurrency }));

    // --- Q1 & Q4: monthly totals, across workspaces the reader's own session does not belong to ---

    [Fact]
    public async Task A_months_charged_total_combines_every_workspace_though_the_reading_session_belongs_to_none_of_them()
    {
        await using var db = WalletHarness.SystemContext();
        var a = WalletHarness.SeedWorkspace(db);
        var b = WalletHarness.SeedWorkspace(db);
        db.BillingLedger.Add(WalletHarness.Line(a, WalletHarness.Hour, -1_000));
        db.BillingLedger.Add(WalletHarness.Line(b, WalletHarness.Hour, -2_500));
        await db.SaveChangesAsync();

        var report = await Report(db).BuildAsync();

        var thisMonth = report.MonthlyRevenue[^1];
        thisMonth.Month.Should().Be("2026-08");
        thisMonth.HasChargeRows.Should().BeTrue();
        thisMonth.ChargedTotalMinor.Should().Be(3_500,
            "both workspaces' charges, though the admin reading this report is signed in to neither");
    }

    [Fact]
    public async Task Six_calendar_months_are_shown_oldest_first_ending_on_the_month_in_progress()
    {
        await using var db = WalletHarness.SystemContext();
        await db.SaveChangesAsync();

        var report = await Report(db).BuildAsync();

        report.MonthlyRevenue.Should().HaveCount(RevenueReport.MonthsShown);
        report.MonthlyRevenue.Select(m => m.Month).Should().BeEquivalentTo(
            ["2026-03", "2026-04", "2026-05", "2026-06", "2026-07", "2026-08"], o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task A_month_with_no_ledger_rows_says_so_rather_than_printing_a_bare_zero()
    {
        // An empty database, deliberately: every one of the six months has nothing behind it, and
        // the honesty flag has to say that for every one of them, not only the first.
        await using var db = WalletHarness.SystemContext();
        await db.SaveChangesAsync();

        var report = await Report(db).BuildAsync();

        report.MonthlyRevenue.Should().OnlyContain(m => !m.HasChargeRows && m.ChargedTotalMinor == 0);
        report.MonthlyRevenue.Should().OnlyContain(m => !m.HasAnyCredits && m.TotalCreditsMinor == 0);
    }

    [Fact]
    public async Task Credits_issued_this_month_are_split_between_an_administrators_own_credit_and_a_redeemed_voucher()
    {
        await using var db = WalletHarness.SystemContext();
        var tenant = WalletHarness.SeedWorkspace(db);
        var voucherId = Guid.CreateVersion7();

        // An administrator's own credit — the TenantsController ConfirmCredit path. Its ledger id is
        // freshly minted and matches no voucher.
        db.BillingLedger.Add(new BillingLedgerEntry
        {
            Id = Guid.CreateVersion7(), WorkspaceId = tenant, BillingHour = WalletHarness.Hour,
            Kind = LedgerKind.Credit, AmountMinor = 50_000, ResourceName = string.Empty,
            CreatedByUserId = WalletHarness.Admin
        });

        // A redeemed voucher — VoucherService.RedeemAsync mints the credit's ledger id AS the
        // voucher's own id (see BillingPageHttpTests: "a voucher is the idempotency key for its one
        // credit line"). The report has nothing else to tell the two apart by.
        db.BillingLedger.Add(new BillingLedgerEntry
        {
            Id = voucherId, WorkspaceId = tenant, BillingHour = WalletHarness.Hour,
            Kind = LedgerKind.Credit, AmountMinor = 20_000, ResourceName = string.Empty,
        });
        db.BillingVouchers.Add(new BillingVoucher
        {
            Id = voucherId, CodeHash = "hash-911", CodeHint = "9911",
            AmountMinor = 20_000, Currency = "IRR", Note = "campaign",
            CreatedByUserId = WalletHarness.Admin, RedeemedWorkspaceId = tenant, RedeemedAt = WalletHarness.Now
        });
        await db.SaveChangesAsync();

        var report = await Report(db).BuildAsync();

        var thisMonth = report.MonthlyRevenue[^1];
        thisMonth.AdminCreditCount.Should().Be(1);
        thisMonth.AdminCreditsMinor.Should().Be(50_000);
        thisMonth.VoucherCreditCount.Should().Be(1);
        thisMonth.VoucherCreditsMinor.Should().Be(20_000);
        thisMonth.TotalCreditsMinor.Should().Be(70_000);
    }

    [Fact]
    public async Task A_signup_trial_credit_is_counted_apart_from_a_support_voucher_and_never_as_revenue()
    {
        // Sub-project 1.9's own requirement, proven here: the trial credit must appear as what it is
        // — distinguishable from a purchase (a charge) and from a support-issued voucher — and must
        // never inflate ChargedTotalMinor, the only figure this report treats as income.
        await using var db = WalletHarness.SystemContext();
        var tenant = WalletHarness.SeedWorkspace(db);
        var owner = Guid.CreateVersion7();

        // A real charge this same month, so ChargedTotalMinor has something genuine to stay at.
        db.BillingLedger.Add(WalletHarness.Line(tenant, WalletHarness.Hour, -1_000));

        // A support-issued voucher, redeemed — IsTrialCredit left at its default, false.
        var supportVoucherId = Guid.CreateVersion7();
        db.BillingLedger.Add(new BillingLedgerEntry
        {
            Id = supportVoucherId, WorkspaceId = tenant, BillingHour = WalletHarness.Hour,
            Kind = LedgerKind.Credit, AmountMinor = 20_000, ResourceName = string.Empty,
        });
        db.BillingVouchers.Add(new BillingVoucher
        {
            Id = supportVoucherId, CodeHash = "hash-support", CodeHint = "9911",
            AmountMinor = 20_000, Currency = "IRR", Note = "campaign",
            CreatedByUserId = WalletHarness.Admin, RedeemedWorkspaceId = tenant, RedeemedAt = WalletHarness.Now
        });

        // The platform's own automatic signup credit — the same shape SignupTrialCreditService
        // writes: a voucher flagged IsTrialCredit, CreatedByUserId is the beneficiary's own owner
        // (never an administrator), redeemed into the ledger under its own id.
        var trialVoucherId = Guid.CreateVersion7();
        db.BillingLedger.Add(new BillingLedgerEntry
        {
            Id = trialVoucherId, WorkspaceId = tenant, BillingHour = WalletHarness.Hour,
            Kind = LedgerKind.Credit, AmountMinor = 5_000, ResourceName = string.Empty,
        });
        db.BillingVouchers.Add(new BillingVoucher
        {
            Id = trialVoucherId, CodeHash = "hash-trial", CodeHint = "1122",
            AmountMinor = 5_000, Currency = "IRR", Note = "Signup trial credit",
            CreatedByUserId = owner, IsTrialCredit = true,
            RedeemedWorkspaceId = tenant, RedeemedByUserId = owner, RedeemedAt = WalletHarness.Now
        });
        await db.SaveChangesAsync();

        var report = await Report(db).BuildAsync();

        var thisMonth = report.MonthlyRevenue[^1];
        thisMonth.TrialCreditCount.Should().Be(1);
        thisMonth.TrialCreditsMinor.Should().Be(5_000);
        // Not folded into either of the other two buckets.
        thisMonth.VoucherCreditCount.Should().Be(1, "the trial credit must not double as a support voucher");
        thisMonth.VoucherCreditsMinor.Should().Be(20_000);
        thisMonth.AdminCreditCount.Should().Be(0);
        thisMonth.AdminCreditsMinor.Should().Be(0);
        // The one figure this report ever treats as income is untouched by any of the three credits.
        thisMonth.ChargedTotalMinor.Should().Be(1_000, "a trial credit is not a charge and must never count as revenue");
        thisMonth.TotalCreditsMinor.Should().Be(25_000);
    }

    // --- Q2 & Q3: top workspaces by 30-day burn, each with balance, runway and suspension ---

    [Fact]
    public async Task Workspaces_rank_by_thirty_day_burn_highest_first_and_a_workspace_with_none_is_not_listed()
    {
        await using var db = WalletHarness.SystemContext();
        var heavy = WalletHarness.SeedWorkspace(db, balanceMinor: 100_000);
        var light = WalletHarness.SeedWorkspace(db, balanceMinor: 100_000);
        var idle = WalletHarness.SeedWorkspace(db, balanceMinor: 100_000);
        db.BillingLedger.Add(WalletHarness.Line(heavy, WalletHarness.Hour, -9_000));
        db.BillingLedger.Add(WalletHarness.Line(light, WalletHarness.Hour, -1_000));
        // idle burns nothing — it must not appear padded in as a false "top workspace".
        await db.SaveChangesAsync();

        var report = await Report(db).BuildAsync();

        report.TopWorkspacesByBurn.Select(w => w.WorkspaceId).Should().Equal(heavy, light);
        report.TopWorkspacesByBurn.Should().NotContain(w => w.WorkspaceId == idle);
    }

    [Fact]
    public async Task Only_the_ten_heaviest_burners_are_named_never_padded_to_look_like_more()
    {
        await using var db = WalletHarness.SystemContext();
        var workspaces = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            var ws = WalletHarness.SeedWorkspace(db, balanceMinor: 100_000);
            workspaces.Add(ws);
            // Distinct burn per workspace so the ranking has one unambiguous order.
            db.BillingLedger.Add(WalletHarness.Line(ws, WalletHarness.Hour, -(1_000 + i * 100)));
        }
        await db.SaveChangesAsync();

        var report = await Report(db).BuildAsync();

        report.TopWorkspacesByBurn.Should().HaveCount(RevenueReport.TopWorkspaceCount);
        // Highest burn (last seeded, 1_000 + 11*100 = 2_100) ranks first.
        report.TopWorkspacesByBurn[0].WorkspaceId.Should().Be(workspaces[^1]);
        report.TopWorkspacesByBurn.Select(w => w.Burn30DayMinor)
            .Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task A_charge_outside_the_thirty_day_window_does_not_count_towards_burn()
    {
        await using var db = WalletHarness.SystemContext();
        var tenant = WalletHarness.SeedWorkspace(db, balanceMinor: 100_000);
        db.BillingLedger.Add(WalletHarness.Line(tenant, WalletHarness.Hour.AddDays(-31), -50_000));
        await db.SaveChangesAsync();

        var report = await Report(db).BuildAsync();

        report.TopWorkspacesByBurn.Should().BeEmpty("the only charge is 31 days old, outside the burn window");
    }

    [Fact]
    public async Task A_workspaces_row_carries_its_balance_and_suspension_state_alongside_the_burn()
    {
        await using var db = WalletHarness.SystemContext();
        var tenant = WalletHarness.SeedWorkspace(
            db, balanceMinor: 42_000, suspended: true, reason: SuspensionReason.NoBalance);
        db.BillingLedger.Add(WalletHarness.Line(tenant, WalletHarness.Hour, -5_000));
        await db.SaveChangesAsync();

        var report = await Report(db).BuildAsync();

        var row = report.TopWorkspacesByBurn.Single();
        row.BalanceMinor.Should().Be(42_000);
        row.Burn30DayMinor.Should().Be(5_000);
        row.Suspended.Should().BeTrue();
        row.SuspendedReason.Should().Be(SuspensionReason.NoBalance);
    }

    [Fact]
    public async Task A_workspace_under_the_minimum_history_shows_the_same_not_enough_history_honesty_the_bill_itself_shows()
    {
        // A single charged hour, exactly like CostForecastTests' own "not enough history" case —
        // the report must not invent a runway WalletService itself refused to state.
        await using var db = WalletHarness.SystemContext();
        var tenant = WalletHarness.SeedWorkspace(db, balanceMinor: 50_000);
        db.BillingLedger.Add(WalletHarness.Line(tenant, WalletHarness.Hour, -500));
        await db.SaveChangesAsync();

        var report = await Report(db).BuildAsync();

        var row = report.TopWorkspacesByBurn.Single();
        row.Forecast.HasEnoughHistory.Should().BeFalse();
        row.Forecast.HistoryHours.Should().Be(1);
        row.Forecast.RunwayDate.Should().BeNull();
    }

    [Fact]
    public async Task The_runway_on_a_workspaces_row_is_read_from_WalletServices_own_forecast_never_recomputed()
    {
        // Twenty-four steady hours at the same rate CostForecastTests uses for its own "enough
        // history" case, so the runway here can be checked against that method's own documented
        // arithmetic rather than a second implementation of BurnRate.
        await using var db = WalletHarness.SystemContext();
        var tenant = WalletHarness.SeedWorkspace(db, balanceMinor: 12_000);
        var lastEndedHour = new DateTimeOffset(2026, 8, 9, 19, 0, 0, TimeSpan.Zero);
        for (var h = 0; h < 24; h++)
            db.BillingLedger.Add(WalletHarness.Line(tenant, lastEndedHour.AddHours(-h), -500));
        await db.SaveChangesAsync();

        var directForecast = await WalletHarness.Wallets(db).ForecastAsync(
            tenant, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), default);

        var report = await Report(db).BuildAsync();
        var row = report.TopWorkspacesByBurn.Single();

        row.Forecast.Should().BeEquivalentTo(directForecast,
            "the row's forecast is exactly what WalletService.ForecastAsync answers — not a second guess");
    }

    [Fact]
    public async Task The_providers_own_workspace_never_appears_even_if_something_charged_it()
    {
        // Billing exempts the default workspace everywhere else in this codebase (BillingTick,
        // BillingGate, BillingSuspension, ResourceCreationBilling all skip IsDefault) — this asserts
        // the revenue view keeps that same promise defensively rather than trusting the ledger's
        // silence.
        await using var db = WalletHarness.SystemContext();
        var provider = new Workspace { Name = "Provider", Slug = "provider-" + Guid.NewGuid().ToString("n")[..8], IsDefault = true };
        db.Workspaces.Add(provider);
        db.Wallets.Add(new Wallet { WorkspaceId = provider.Id, BalanceMinor = 100_000 });
        db.BillingLedger.Add(WalletHarness.Line(provider.Id, WalletHarness.Hour, -5_000));
        await db.SaveChangesAsync();

        var report = await Report(db).BuildAsync();

        report.TopWorkspacesByBurn.Should().NotContain(w => w.WorkspaceId == provider.Id);
    }
}
