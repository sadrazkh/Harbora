using FluentAssertions;
using Harbora.Infrastructure.Monitoring;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// How full something is.
///
/// The application list printed "512 MB" with nothing beside it, which answers nothing — full or
/// empty depends entirely on whether that app was given 512 MB or 8 GB. The plan page drew a 5%
/// bar under the word "∞". Both are the same mistake in different directions: a missing half,
/// rendered as though it were present.
/// </summary>
public class AllocationReadingTests
{
    [Fact]
    public void Half_of_what_it_was_given_is_half()
    {
        var reading = AllocationReading.Of(512, 1024);

        reading.Kind.Should().Be(AllocationKind.Known);
        reading.Percent.Should().Be(50);
        reading.IsOver.Should().BeFalse();
    }

    [Fact]
    public void Nothing_measured_is_not_nothing_used()
    {
        // The bar that matters: an app whose metrics have never arrived must not be drawn as an
        // idle one. Empty and unknown look identical on a progress bar and mean opposite things.
        var reading = AllocationReading.Of(null, 1024);

        reading.Kind.Should().Be(AllocationKind.Unmeasured);
        reading.HasShare.Should().BeFalse();
    }

    [Fact]
    public void Measured_but_unlimited_has_no_share_rather_than_a_small_one()
    {
        // The plan page returned "5" here, so every unlimited resource showed a permanently
        // slightly-full bar. There is no denominator; a percentage would be invented.
        var reading = AllocationReading.Of(4096, 0);

        reading.Kind.Should().Be(AllocationKind.Unlimited);
        reading.HasShare.Should().BeFalse();
        reading.Percent.Should().Be(0);
    }

    [Fact]
    public void A_negative_limit_is_treated_as_no_limit()
    {
        AllocationReading.Of(100, -1).Kind.Should().Be(AllocationKind.Unlimited);
    }

    [Fact]
    public void Using_everything_is_a_hundred()
    {
        AllocationReading.Of(1024, 1024).Percent.Should().Be(100);
        AllocationReading.Of(1024, 1024).IsOver.Should().BeFalse();
    }

    [Fact]
    public void Over_the_allocation_is_said_and_still_fits_its_track()
    {
        // A container resized downwards keeps running at its old size until it is redeployed, so a
        // sample genuinely over the new limit is ordinary. The bar is clamped so it cannot render
        // wider than its track, and the fact is carried separately rather than lost in the clamp.
        var reading = AllocationReading.Of(2048, 1024);

        reading.Percent.Should().Be(100);
        reading.IsOver.Should().BeTrue();
    }

    [Fact]
    public void Nearly_full_rounds_to_full()
    {
        AllocationReading.Of(996, 1000).Percent.Should().Be(100);
    }

    [Fact]
    public void Barely_used_is_not_rounded_away_to_nothing()
    {
        // 0.4% rounds to 0, and that is right: it is a bar, not a reading. What must not happen is
        // the opposite — a value present but shown as unmeasured.
        var reading = AllocationReading.Of(4, 1000);

        reading.Kind.Should().Be(AllocationKind.Known);
        reading.Percent.Should().Be(0);
    }

    [Fact]
    public void Measured_as_nothing_is_still_a_measurement()
    {
        // Zero used is a fact — the app is idle. Distinct from never having been measured, which is
        // the whole point of the two kinds.
        var reading = AllocationReading.Of(0, 1024);

        reading.Kind.Should().Be(AllocationKind.Known);
        reading.HasShare.Should().BeTrue();
        reading.Percent.Should().Be(0);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void A_value_that_is_not_a_measurement_is_treated_as_none(double used)
    {
        // Docker's stats call can hand back nonsense when a container is going away. A negative
        // number of bytes is not a small reading, it is a broken one.
        AllocationReading.Of(used, 1024).Kind.Should().Be(AllocationKind.Unmeasured);
    }

    [Fact]
    public void A_limit_that_is_not_a_number_is_no_limit()
    {
        AllocationReading.Of(100, double.NaN).Kind.Should().Be(AllocationKind.Unlimited);
    }

    [Fact]
    public void Counting_apps_reads_the_same_way_as_measuring_bytes()
    {
        AllocationReading.OfCount(3, 4).Percent.Should().Be(75);
        AllocationReading.OfCount(1, 0).Kind.Should().Be(AllocationKind.Unlimited);
    }
}
