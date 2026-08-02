using System.Globalization;
using FluentAssertions;
using Harbora.Infrastructure.Design;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Turning samples into a line.
///
/// The case that matters is the degenerate one: a flat series, a single sample, or no samples at
/// all must not silently become a confident-looking chart. A sparkline is read at a glance, which
/// is exactly why a wrong one is believed.
/// </summary>
public class SparklinePathTests
{
    [Fact]
    public void A_series_becomes_a_path()
    {
        var path = SparklinePath.Build([0, 5, 10], 100, 20);

        path.Should().StartWith("M").And.Contain("L");
    }

    [Fact]
    public void No_samples_draw_nothing()
    {
        SparklinePath.Build([], 100, 20).Should().BeNull();
    }

    [Fact]
    public void One_sample_draws_nothing()
    {
        // A single point is not a trend, and a dot at the start edge reads as a crash to zero.
        SparklinePath.Build([42], 100, 20).Should().BeNull();
    }

    [Fact]
    public void A_flat_series_runs_through_the_middle_not_along_the_floor()
    {
        // A constant 80% drawn along the bottom of the box reads as an outage.
        var path = SparklinePath.Build([80, 80, 80], 100, 20);

        path.Should().NotContain("NaN");
        Ys(path).Should().OnlyContain(y => y > 5 && y < 15);
    }

    [Fact]
    public void The_line_stays_inside_the_box()
    {
        var path = SparklinePath.Build([1, 500, 3, 900, 2], 100, 20);

        Ys(path).Should().OnlyContain(y => y >= 0 && y <= 20);
    }

    [Fact]
    public void The_highest_sample_sits_at_the_top()
    {
        // Inverting the axis is the classic mistake here, and it produces a chart that is exactly
        // wrong rather than obviously broken: the outage looks like the peak.
        var path = SparklinePath.Build([0, 100], 100, 20);
        var ys = Ys(path).ToList();

        ys[0].Should().BeGreaterThan(ys[1]);
    }

    [Fact]
    public void Coordinates_are_invariant_regardless_of_culture()
    {
        // A Persian decimal separator inside an SVG path attribute does not throw. It produces a
        // shape nobody drew.
        // Four samples on purpose: the x step becomes 100/3, so the path actually contains a
        // fractional coordinate for the culture to mangle. With three evenly-spaced samples every
        // coordinate lands on a whole number and this test passes no matter what culture is used —
        // which is exactly how it passed while the implementation was wrong.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fa-IR");

            var path = SparklinePath.Build([1, 2, 3, 4], 100, 20)!;

            path.Should().Contain(".", "the fixture must produce a fractional coordinate");
            path.Should().NotContain("٫").And.NotContain(",");
            path.Should().MatchRegex(@"^[MLd0-9. ]+$", "only ASCII digits may reach an SVG path");
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    private static IEnumerable<double> Ys(string? path) =>
        (path ?? "").Split(['M', 'L'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().Split(' ')[1])
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture));
}
