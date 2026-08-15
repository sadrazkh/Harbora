using FluentAssertions;
using Harbora.Infrastructure.Monitoring;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The rule behind the usage tab's range control: three windows, and nothing else ever comes back
/// selected. <c>MonitoringController.Metrics</c> already clamps its own <c>minutes</c> parameter to
/// a wide sane range (do-not-rebuild) — this is the narrower rule the usage tab's control itself
/// applies before it ever reaches that endpoint.
/// </summary>
public class UsageRangeWindowTests
{
    [Theory]
    [InlineData(UsageRangeWindow.OneHour)]
    [InlineData(UsageRangeWindow.OneDay)]
    [InlineData(UsageRangeWindow.OneWeek)]
    public void One_of_the_three_offered_windows_passes_through_unchanged(int minutes)
    {
        UsageRangeWindow.Clamp(minutes).Should().Be(minutes);
    }

    [Fact]
    public void A_missing_value_falls_back_to_one_hour()
    {
        UsageRangeWindow.Clamp(null).Should().Be(UsageRangeWindow.OneHour);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(45)]
    [InlineData(999_999)]
    public void A_value_that_is_not_one_of_the_three_windows_falls_back_to_one_hour(int minutes)
    {
        // Not clamped to the nearest offered window — collapsed to the default, the same way a
        // missing value is. Either behaviour keeps the control determinate; this is the one chosen,
        // because "nearest" would make a typo in a shared link silently redraw as a different,
        // unrequested window rather than the one the endpoint has always defaulted to.
        UsageRangeWindow.Clamp(minutes).Should().Be(UsageRangeWindow.OneHour);
    }

    [Fact]
    public void The_three_offered_windows_are_exactly_one_hour_a_day_and_a_week()
    {
        UsageRangeWindow.AllowedMinutes.Should().Equal(60, 1440, 10080);
    }
}
