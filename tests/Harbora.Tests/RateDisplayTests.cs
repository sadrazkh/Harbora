using FluentAssertions;
using Harbora.Web.Infrastructure;
using Xunit;

namespace Harbora.Tests;

public class RateDisplayTests
{
    [Fact]
    public void An_unpriced_rate_says_so_rather_than_reading_as_nothing()
    {
        // The distinction five screens now depend on. "0.00" beside a tier nobody has priced sells
        // capacity for nothing and says so to nobody.
        RateDisplay.Hourly(null, isFa: false).Should().Be("not priced");
        RateDisplay.Hourly(null, isFa: true).Should().NotBe(RateDisplay.Hourly(null, isFa: false));
        RateDisplay.Hourly(null, isFa: true).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_deliberately_free_rate_says_free_rather_than_showing_a_zero()
    {
        // Somebody typed a zero on purpose. It reads as the decision it is, and — importantly — as a
        // different word from the one above it.
        RateDisplay.Hourly(0, isFa: false).Should().Be("free");
        RateDisplay.Hourly(0, isFa: false).Should().NotBe(RateDisplay.Hourly(null, isFa: false));
    }

    [Fact]
    public void A_priced_rate_reads_as_a_grouped_figure_to_two_places()
    {
        RateDisplay.Hourly(2, isFa: false).Should().Be("0.02");
        RateDisplay.Hourly(150_000, isFa: false).Should().Be("1,500.00");
    }

    [Fact]
    public void A_monthly_estimate_carries_the_approximation_sign_it_depends_on()
    {
        // The "≈" is the whole honesty of the figure and it lives in the helper rather than in each
        // caller's markup, where one of five would eventually be written without it.
        RateDisplay.Monthly(2, isFa: false).Should().Be("≈ 14.60");
    }

    [Fact]
    public void An_unpriced_rate_has_no_monthly_estimate_at_all()
    {
        // Null, not a dash: a dash beside a real hourly figure reads as "this tier has no monthly
        // cost". The caller is expected to print nothing.
        RateDisplay.Monthly(null, isFa: false).Should().BeNull();
        RateDisplay.Monthly(null, isFa: true).Should().BeNull();
    }

    [Fact]
    public void A_free_rate_says_free_monthly_too_rather_than_approximately_nothing()
    {
        // Nothing is approximate about free, and the word matches the hourly column so the two agree.
        RateDisplay.Monthly(0, isFa: false).Should().Be("free");
        RateDisplay.Monthly(0, isFa: false).Should().Be(RateDisplay.Hourly(0, isFa: false));
    }

    [Fact]
    public void A_rate_too_large_to_project_has_no_monthly_estimate()
    {
        // Inherited from MonthlyEstimate's overflow guard: a wrapped multiplication would print a
        // monthly credit, and only on the biggest bills on the install.
        RateDisplay.Monthly(long.MaxValue, isFa: false).Should().BeNull();
    }

    [Fact]
    public void The_one_line_form_drops_the_monthly_half_when_there_is_none()
    {
        // Rather than leaving a separator pointing at nothing.
        RateDisplay.HourlyAndMonthly(null, isFa: false).Should().Be("not priced");
        RateDisplay.HourlyAndMonthly(null, isFa: false).Should().NotContain("·");
    }

    [Fact]
    public void The_one_line_form_says_free_once_rather_than_twice()
    {
        RateDisplay.HourlyAndMonthly(0, isFa: false).Should().Be("free");
    }

    [Fact]
    public void The_one_line_form_carries_both_figures_and_their_units()
    {
        // A rate with no unit on it is the reason this string exists: "0.02" alone has been read as a
        // monthly price on a card before now.
        var line = RateDisplay.HourlyAndMonthly(2, isFa: false);

        line.Should().Contain("0.02").And.Contain("hour");
        line.Should().Contain("14.60").And.Contain("month");
    }

    [Fact]
    public void The_one_line_form_is_written_in_whichever_language_is_asked_for()
    {
        var en = RateDisplay.HourlyAndMonthly(2, isFa: false);
        var fa = RateDisplay.HourlyAndMonthly(2, isFa: true);

        fa.Should().NotBe(en);
        // The figures are invariant in both, so a bill reconciles digit-for-digit against the same
        // bill opened in the other language — see MinorUnits.Format.
        fa.Should().Contain("0.02").And.Contain("14.60");
    }
}
