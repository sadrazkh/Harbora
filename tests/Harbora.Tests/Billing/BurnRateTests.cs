using FluentAssertions;
using Harbora.Infrastructure.Billing;
using Xunit;

namespace Harbora.Tests.Billing;

/// <summary>
/// The one division behind both the low-balance warning's "about N more hour(s)" and the wallet
/// page's runway date. Pure, so every rule is provable without a database or a clock.
/// </summary>
public class BurnRateTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 9, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Runway_hours_floors_rather_than_rounds()
    {
        // 11,999 at 500 an hour is 23.998 hours. Rounding to 24 promises an hour the balance does
        // not have.
        BurnRate.RunwayHours(11_999, 500).Should().Be(23);
    }

    [Fact]
    public void A_balance_already_at_or_below_nothing_has_zero_hours_left_not_a_negative_count()
    {
        BurnRate.RunwayHours(0, 500).Should().Be(0);
        BurnRate.RunwayHours(-500, 500).Should().Be(0);
    }

    [Fact]
    public void Nothing_currently_costing_money_has_no_runway_at_all()
    {
        // Zero is not "lasts forever" stated as a number — it is that the question does not apply,
        // the same way MonthlyEstimate answers null rather than zero for an unpriced rate.
        BurnRate.RunwayHours(10_000, 0).Should().BeNull();
        BurnRate.RunwayHours(10_000, -100).Should().BeNull();
    }

    [Fact]
    public void The_runway_date_is_now_plus_the_floored_hours()
    {
        // 10,000 at 500 an hour is 20 whole hours.
        BurnRate.RunwayDate(Now, 10_000, 500).Should().Be(Now.AddHours(20));
    }

    [Fact]
    public void A_runway_with_no_hours_has_no_date_either()
    {
        BurnRate.RunwayDate(Now, 10_000, 0).Should().BeNull();
    }

    [Fact]
    public void A_runway_decades_out_is_not_stated_as_a_specific_date()
    {
        // One minor unit an hour against a large balance has no honest "runs out" moment — naming
        // one anyway is the overconfident-surface failure this feature exists not to repeat.
        BurnRate.RunwayHours(1_000_000_000, 1).Should().BeGreaterThan(BurnRate.MaxStatableRunwayHours);
        BurnRate.RunwayDate(Now, 1_000_000_000, 1).Should().BeNull();
    }

    [Fact]
    public void A_runway_just_inside_the_statable_bound_still_gets_a_date()
    {
        var balance = BurnRate.MaxStatableRunwayHours * 500;
        BurnRate.RunwayDate(Now, balance, 500).Should().Be(Now.AddHours(BurnRate.MaxStatableRunwayHours));
    }
}
