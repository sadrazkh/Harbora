using FluentAssertions;
using Harbora.Infrastructure.Billing;
using Xunit;

namespace Harbora.Tests.Billing;

public class MonthlyEstimateTests
{
    [Fact]
    public void A_month_is_seven_hundred_and_thirty_hours()
    {
        // 365 x 24 / 12. Asserted rather than left in a comment, because the whole feature is one
        // multiplication and the constant is the only thing in it anybody can get wrong.
        MonthlyEstimate.HoursPerMonth.Should().Be(730);
        MonthlyEstimate.HoursPerMonth.Should().Be(365 * 24 / 12);
    }

    [Fact]
    public void An_hourly_rate_becomes_that_rate_for_every_hour_of_an_average_month()
    {
        MonthlyEstimate.FromHourly(2).Should().Be(1460);
        MonthlyEstimate.FromHourly(1).Should().Be(730);
    }

    [Fact]
    public void A_rate_nobody_has_set_produces_no_estimate_rather_than_a_very_convincing_zero()
    {
        // The one failure this helper could introduce into a codebase that has been careful about it
        // everywhere else: nothing times 730 is zero, and "≈ 0.00/month" beside an unpriced tier
        // reads as a free tier. Unpriced has no monthly figure, because it has no hourly one.
        MonthlyEstimate.FromHourly(null).Should().BeNull();
    }

    [Fact]
    public void A_rate_of_zero_produces_an_estimate_of_zero_because_free_is_an_answer()
    {
        // The other half of the distinction the rate columns exist for. Somebody typed a zero on
        // purpose, and a free tier's month really does cost nothing — so this is a figure, not a gap.
        MonthlyEstimate.FromHourly(0).Should().Be(0);
    }

    [Fact]
    public void A_negative_rate_produces_no_estimate_rather_than_a_monthly_credit()
    {
        // BillingHourPlan drops a negative rate rather than charging it, so no such rate is ever
        // money — and rendering "≈ -14.60/month" would advertise a refund the ledger will never
        // make. There is no honest monthly figure for a rate that is itself a bug upstream.
        MonthlyEstimate.FromHourly(-2).Should().BeNull();
    }

    [Fact]
    public void A_rate_too_large_to_multiply_produces_no_estimate_rather_than_wrapping_negative()
    {
        // This project compiles unchecked, so the multiplication would not throw — it would wrap to
        // a large negative and print a monthly credit on the biggest bills on the install, which is
        // the same trap MinorUnits.MaxMajor and BillingRates.GibibytesCeiling already guard.
        MonthlyEstimate.FromHourly(long.MaxValue).Should().BeNull();
        MonthlyEstimate.FromHourly(long.MaxValue / MonthlyEstimate.HoursPerMonth + 1).Should().BeNull();
    }

    [Fact]
    public void The_largest_rate_that_still_fits_is_still_estimated()
    {
        // The bound refuses what it must and nothing more: a rate one unit below the overflow is a
        // real rate and must still get its figure, or the guard becomes a silent ceiling on pricing.
        var largest = long.MaxValue / MonthlyEstimate.HoursPerMonth;
        MonthlyEstimate.FromHourly(largest).Should().Be(largest * MonthlyEstimate.HoursPerMonth);
    }
}
