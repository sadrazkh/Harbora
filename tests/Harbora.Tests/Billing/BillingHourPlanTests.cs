using FluentAssertions;
using Harbora.Domain.Billing;
using Harbora.Infrastructure.Billing;
using Xunit;

namespace Harbora.Tests.Billing;

public class BillingHourPlanTests
{
    private static BillableResource App(string name, long rate, BilledRunState state = BilledRunState.Running) =>
        new(BilledResourceType.App, Guid.CreateVersion7(), name, state, rate);

    [Fact]
    public void Every_resource_gets_its_own_line_so_a_bill_can_be_read_per_app()
    {
        var lines = BillingHourPlan.For([App("api", 600), App("web", 400)], planBaseRatePerHourMinor: 0);

        lines.Where(l => l.Kind == LedgerKind.Charge).Should().HaveCount(2);
        lines.Should().Contain(l => l.Name == "api" && l.AmountMinor == -600);
        lines.Should().Contain(l => l.Name == "web" && l.AmountMinor == -400);
    }

    [Fact]
    public void When_the_resources_come_to_less_than_the_plan_the_difference_is_its_own_line()
    {
        // The plan is a floor. Writing the shortfall as a line of its own is what makes the ledger
        // total exactly what left the wallet — and lets the customer see the words "plan minimum"
        // instead of an unexplained gap between their apps and their balance.
        var lines = BillingHourPlan.For([App("api", 600)], planBaseRatePerHourMinor: 1000);

        lines.Should().HaveCount(2);
        lines.Sum(l => l.AmountMinor).Should().Be(-1000);

        var topUp = lines.Single(l => l.Kind == LedgerKind.PlanMinimumTopUp);
        topUp.AmountMinor.Should().Be(-400);
        topUp.Type.Should().Be(BilledResourceType.PlanBase);
        topUp.Id.Should().BeNull("the top-up has no resource behind it, and needs a stable key to collide on");
    }

    [Fact]
    public void When_the_resources_exceed_the_plan_there_is_no_top_up_line()
    {
        var lines = BillingHourPlan.For([App("api", 600), App("web", 900)], planBaseRatePerHourMinor: 1000);

        lines.Should().OnlyContain(l => l.Kind == LedgerKind.Charge);
        lines.Sum(l => l.AmountMinor).Should().Be(-1500);
    }

    [Fact]
    public void A_workspace_with_nothing_running_still_pays_the_plan_floor()
    {
        var lines = BillingHourPlan.For([], planBaseRatePerHourMinor: 1000);

        lines.Should().ContainSingle();
        lines[0].Kind.Should().Be(LedgerKind.PlanMinimumTopUp);
        lines[0].AmountMinor.Should().Be(-1000);
    }

    [Fact]
    public void A_free_plan_with_nothing_running_produces_no_lines_at_all()
    {
        // Writing a row of zero every hour for every dormant workspace is how a ledger becomes the
        // biggest table on the install without ever holding a number.
        BillingHourPlan.For([], planBaseRatePerHourMinor: 0).Should().BeEmpty();
    }

    [Fact]
    public void A_stopped_resource_is_still_a_line_carrying_its_stopped_state()
    {
        var lines = BillingHourPlan.For([App("api", 100, BilledRunState.Stopped)], planBaseRatePerHourMinor: 0);

        lines.Should().ContainSingle();
        lines[0].State.Should().Be(BilledRunState.Stopped);
        lines[0].AmountMinor.Should().Be(-100);
    }

    [Fact]
    public void A_resource_priced_at_zero_writes_no_line()
    {
        BillingHourPlan.For([App("legacy", 0)], planBaseRatePerHourMinor: 0).Should().BeEmpty();
    }

    [Fact]
    public void A_charge_line_carries_the_type_id_and_rate_of_the_resource_it_bills()
    {
        // Nothing else here would notice a planner that dropped these. The unique index the tick
        // relies on is (workspace, resource type, resource id, hour) with NULLS NOT DISTINCT, so a
        // charge line that lost its id would collide with every other id-less line in the hour and a
        // retried tick would write one row instead of five. The type matters for the same reason: a
        // volume filed as an App shares a key with the app it belongs to.
        //
        // The rate is asserted because BillingLedgerEntry keeps it "so the arithmetic on the line
        // can be checked by eye" — a line whose rate is zero and whose amount is not cannot be.
        var volumeId = Guid.CreateVersion7();
        var lines = BillingHourPlan.For(
            [new BillableResource(BilledResourceType.Volume, volumeId, "uploads", BilledRunState.NotApplicable, 250)],
            planBaseRatePerHourMinor: 0);

        var line = lines.Should().ContainSingle().Subject;
        line.Type.Should().Be(BilledResourceType.Volume);
        line.Id.Should().Be(volumeId);
        line.RatePerHourMinor.Should().Be(250);
        line.AmountMinor.Should().Be(-250);
    }

    [Fact]
    public void The_plan_minimum_line_carries_the_floor_as_its_rate_not_the_gap_it_made_up()
    {
        // The one line on the bill where rate times hours is deliberately not the amount. The rate
        // is the plan's floor — the fact worth keeping, and the only place the ledger records what
        // the plan was that hour. Anything that later reconciles a line by multiplying its rate by
        // its hours has to exclude this kind, so it is pinned here rather than left to be
        // rediscovered by a failing reconcile.
        var lines = BillingHourPlan.For([App("api", 600)], planBaseRatePerHourMinor: 1000);

        var topUp = lines.Single(l => l.Kind == LedgerKind.PlanMinimumTopUp);
        topUp.RatePerHourMinor.Should().Be(1000);
        topUp.AmountMinor.Should().Be(-400);
        topUp.State.Should().Be(BilledRunState.NotApplicable);
    }

    [Fact]
    public void A_resource_priced_below_zero_neither_pays_the_customer_nor_deepens_the_plan_top_up()
    {
        // A negative rate is a misconfiguration, not a discount. Charging it writes a positive line —
        // free money — and counting it towards the hour's total makes the plan-minimum top-up bigger
        // than the floor to cover a gap that does not exist. Both are silent: the ledger still adds
        // up, it just adds up to the wrong number. Dropping the line does neither.
        BillingHourPlan.For([App("misconfigured", -100)], planBaseRatePerHourMinor: 0).Should().BeEmpty();

        var lines = BillingHourPlan.For([App("api", 600), App("misconfigured", -100)], planBaseRatePerHourMinor: 1000);

        lines.Should().HaveCount(2);
        lines.Single(l => l.Kind == LedgerKind.PlanMinimumTopUp).AmountMinor.Should().Be(-400);
        lines.Sum(l => l.AmountMinor).Should().Be(-1000);
    }
}
