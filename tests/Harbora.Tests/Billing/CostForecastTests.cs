using FluentAssertions;
using Harbora.Domain.Billing;
using Xunit;

namespace Harbora.Tests.Billing;

/// <summary>
/// What the current billing period is heading towards, and when the balance runs out at that rate —
/// both read off hours <see cref="BillingTick"/> already priced and wrote, never a second calculation
/// of what something costs.
///
/// <para>
/// <see cref="WalletHarness.Now"/> is 2026-08-09 20:30 UTC, so the newest hour
/// <c>WalletService.ForecastAsync</c> could possibly have priced by then is 19:00–20:00 that same
/// day — <c>TopOfHour(Now)</c> is the hour in progress (20:00), and the hour immediately before it is
/// the newest one that has actually ended. Every fixture below is built around that hour so the
/// numbers can be checked by hand rather than re-deriving the method's own arithmetic.
/// </para>
/// </summary>
public class CostForecastTests
{
    private static readonly DateTimeOffset LastEndedHour = new(2026, 8, 9, 19, 0, 0, TimeSpan.Zero);

    /// <summary>Fills <paramref name="hours"/> distinct hours of ordinary charges, ending at
    /// <see cref="LastEndedHour"/> and going backwards — the shape a workspace billed steadily has.</summary>
    private static void SeedSteadyHistory(
        BillingContext db, Guid workspaceId, int hours, long ratePerHour, DateTimeOffset? endingAt = null)
    {
        var end = endingAt ?? LastEndedHour;
        for (var h = 0; h < hours; h++)
            db.BillingLedger.Add(WalletHarness.Line(workspaceId, end.AddHours(-h), -ratePerHour));
        db.SaveChanges();
    }

    [Fact]
    public async Task Fewer_than_the_minimum_hours_of_history_is_shown_as_not_enough_rather_than_projected()
    {
        // Five hours could be a resource somebody spun up this morning. Extrapolating five hours
        // across a month is the confident-wrong-number failure this feature is required to refuse.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db, balanceMinor: 50_000);
        SeedSteadyHistory(db, ws, hours: 5, ratePerHour: 500);

        var forecast = await WalletHarness.Wallets(db).ForecastAsync(
            ws, LastEndedHour.AddHours(-23), LastEndedHour.AddHours(2), default);

        forecast.HasEnoughHistory.Should().BeFalse();
        forecast.HistoryHours.Should().Be(5);
        forecast.MinimumHistoryHours.Should().Be(24);
        forecast.ProjectedPeriodTotalMinor.Should().BeNull();
        forecast.RunwayHours.Should().BeNull();
        forecast.RunwayDate.Should().BeNull();

        // What has already been charged is a fact, not a forecast, and is still worth showing.
        forecast.SpentSoFarMinor.Should().Be(5 * 500);
    }

    [Fact]
    public async Task Enough_history_projects_the_rest_of_the_period_at_the_most_recently_charged_hours_rate()
    {
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db, balanceMinor: 10_000);
        // 24 consecutive hours at 500 each, the newest one being the last hour that has actually
        // ended — 2026-08-08 20:00 through 2026-08-09 19:00.
        SeedSteadyHistory(db, ws, hours: 24, ratePerHour: 500);

        var periodFrom = LastEndedHour.AddHours(-23); // 2026-08-08 20:00, the oldest seeded hour
        var periodTo = LastEndedHour.AddHours(3);     // 2026-08-09 22:00 — 1.5h after Now (20:30)

        var forecast = await WalletHarness.Wallets(db).ForecastAsync(ws, periodFrom, periodTo, default);

        forecast.HasEnoughHistory.Should().BeTrue();
        forecast.HistoryHours.Should().Be(24);
        forecast.BurnRateHourlyMinor.Should().Be(500, "the newest ended hour cost 500");
        forecast.SpentSoFarMinor.Should().Be(24 * 500, "all 24 seeded hours fall inside the period");

        // 1.5 hours remain between Now (20:30) and periodTo (22:00); only whole hours are projected.
        forecast.ProjectedPeriodTotalMinor.Should().Be(24 * 500 + 500 * 1);

        // 10,000 at 500 an hour is 20 whole hours.
        forecast.RunwayHours.Should().Be(20);
        forecast.RunwayDate.Should().Be(WalletHarness.Now.AddHours(20));
    }

    [Fact]
    public async Task A_workspace_with_nothing_currently_running_is_not_projected_as_though_it_were_still_burning()
    {
        // History exists — the workspace ran steadily for a day — but the newest ended hour (the one
        // right after it stopped) has no charge line at all, which is what a genuinely free hour
        // looks like on this ledger. The old, higher rate must not leak into the projection.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db, balanceMinor: 10_000);
        SeedSteadyHistory(db, ws, hours: 24, ratePerHour: 500, endingAt: LastEndedHour.AddHours(-1));

        var periodFrom = LastEndedHour.AddHours(-24);
        var periodTo = LastEndedHour.AddHours(3);

        var forecast = await WalletHarness.Wallets(db).ForecastAsync(ws, periodFrom, periodTo, default);

        forecast.HasEnoughHistory.Should().BeTrue();
        forecast.BurnRateHourlyMinor.Should().Be(0);

        // Nothing is being added on top of what already happened, and nothing is being drawn down.
        forecast.ProjectedPeriodTotalMinor.Should().Be(forecast.SpentSoFarMinor);
        forecast.RunwayHours.Should().BeNull();
        forecast.RunwayDate.Should().BeNull();
    }

    [Fact]
    public async Task A_balance_already_at_nothing_has_a_runway_of_zero_hours_starting_now()
    {
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db, balanceMinor: 0);
        SeedSteadyHistory(db, ws, hours: 24, ratePerHour: 500);

        var forecast = await WalletHarness.Wallets(db).ForecastAsync(
            ws, LastEndedHour.AddHours(-23), LastEndedHour.AddHours(3), default);

        forecast.RunwayHours.Should().Be(0);
        forecast.RunwayDate.Should().Be(WalletHarness.Now);
    }

    [Fact]
    public async Task History_is_counted_in_distinct_hours_not_in_ledger_rows()
    {
        // Two resources charged in the same hour is one hour of history, not two — a workspace with a
        // dozen apps must not look like it has earned confidence a single-app workspace has not.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db, balanceMinor: 10_000);
        for (var h = 0; h < 24; h++)
        {
            var hour = LastEndedHour.AddHours(-h);
            db.BillingLedger.Add(WalletHarness.Line(ws, hour, -300, name: "api"));
            db.BillingLedger.Add(WalletHarness.Line(ws, hour, -200, name: "worker"));
        }
        await db.SaveChangesAsync();

        var forecast = await WalletHarness.Wallets(db).ForecastAsync(
            ws, LastEndedHour.AddHours(-23), LastEndedHour.AddHours(3), default);

        forecast.HistoryHours.Should().Be(24);
        forecast.HasEnoughHistory.Should().BeTrue();
        forecast.BurnRateHourlyMinor.Should().Be(500, "both resources' lines for the newest hour count");
    }

    [Fact]
    public async Task A_credit_does_not_count_as_history_or_move_the_burn_rate()
    {
        // A top-up is money in, not a fact about what is running. Twenty-four credits must not let a
        // brand-new workspace clear the history bar, and a credit landing in the newest hour must not
        // read as that hour's cost.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db, balanceMinor: 10_000);
        for (var h = 0; h < 24; h++)
            db.BillingLedger.Add(WalletHarness.Line(
                ws, LastEndedHour.AddHours(-h), amountMinor: 1_000, kind: LedgerKind.Credit));
        await db.SaveChangesAsync();

        var forecast = await WalletHarness.Wallets(db).ForecastAsync(
            ws, LastEndedHour.AddHours(-23), LastEndedHour.AddHours(3), default);

        forecast.HasEnoughHistory.Should().BeFalse();
        forecast.HistoryHours.Should().Be(0);
    }

    [Fact]
    public async Task Spent_so_far_only_counts_the_period_asked_about()
    {
        // Half-open, the same rule WalletService.BreakdownAsync keeps for the customer-facing bill:
        // an hour on the boundary belongs to exactly one statement.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db, balanceMinor: 10_000);
        SeedSteadyHistory(db, ws, hours: 24, ratePerHour: 500);
        // One more hour, the day before the period asked about starts.
        db.BillingLedger.Add(WalletHarness.Line(ws, LastEndedHour.AddHours(-30), -900, name: "earlier"));
        await db.SaveChangesAsync();

        var periodFrom = LastEndedHour.AddHours(-23);
        var forecast = await WalletHarness.Wallets(db).ForecastAsync(
            ws, periodFrom, LastEndedHour.AddHours(3), default);

        forecast.SpentSoFarMinor.Should().Be(24 * 500, "the -900 line sits before the period starts");
    }
}
