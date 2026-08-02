using System.Globalization;
using FluentAssertions;
using Harbora.Infrastructure.Monitoring;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What a panel prints when nobody measured anything.
///
/// The mockups this design came from show about forty populated panels; Harbora collects four
/// metrics. Every one of the rest is a chance to print a confident zero about something that was
/// never observed — a flat line at the bottom of a chart reads as "no traffic", not "no data", and
/// somebody eventually makes a decision on it.
/// </summary>
public class MetricDisplayTests
{
    [Fact]
    public void A_measured_value_is_shown()
    {
        MetricDisplay.For(28.4, "%").Text.Should().Be("28.4%");
    }

    [Fact]
    public void An_unmeasured_value_prints_nothing_at_all()
    {
        var view = MetricDisplay.For(null, "%");

        view.HasData.Should().BeFalse();
        view.Text.Should().BeEmpty();
    }

    [Fact]
    public void Zero_is_a_measurement_and_survives()
    {
        // The other half of the rule, and the one a careless implementation breaks: a service that
        // genuinely served no requests measured zero, and must not be hidden as "unknown".
        var view = MetricDisplay.For(0, "req");

        view.HasData.Should().BeTrue();
        view.Text.Should().Be("0req");
    }

    [Fact]
    public void An_empty_series_is_not_a_flat_line()
    {
        var view = MetricDisplay.ForSeries([], "%");

        view.HasData.Should().BeFalse();
        view.Series.Should().BeEmpty();
    }

    [Fact]
    public void A_null_series_is_not_a_flat_line()
    {
        MetricDisplay.ForSeries(null, "%").HasData.Should().BeFalse();
    }

    [Fact]
    public void A_series_reports_its_last_sample()
    {
        // The headline number over a sparkline is "now", not the average of a day.
        var view = MetricDisplay.ForSeries([10, 20, 30], "%");

        view.HasData.Should().BeTrue();
        view.Text.Should().Be("30%");
        view.Series.Should().Equal(10, 20, 30);
    }

    [Fact]
    public void Numbers_are_formatted_the_same_in_every_culture()
    {
        // The Persian ambient culture renders digits and separators differently, and a metric that
        // reads "۲۸٫۴" in one place and "28.4" in another looks like two different measurements.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fa-IR");
            MetricDisplay.For(1234.5, "").Text.Should().Be("1234.5");
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void A_whole_number_is_not_padded_with_a_decimal()
    {
        MetricDisplay.For(42, "%").Text.Should().Be("42%");
    }
}
