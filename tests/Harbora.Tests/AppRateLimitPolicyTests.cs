using FluentAssertions;
using Harbora.Domain.Apps;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The bounds a customer's rate-limit choice must fall within, and the recommended starting point
/// shown beside the fields (C3, 2026-08-27 what's-left plan) — the same shape
/// <c>ServerCapacityPolicyTests</c> already proves for <c>ServerCapacityPolicy</c>.
/// </summary>
public class AppRateLimitPolicyTests
{
    [Theory]
    [InlineData(0, false, "zero would render as an unlimited middleware, not a strict one")]
    [InlineData(-1, false, "negative is nonsensical for a request count")]
    [InlineData(1, true, "the floor itself is valid")]
    [InlineData(300, true, "the recommended value is obviously valid")]
    [InlineData(1_000_000, true, "the ceiling itself is valid")]
    [InlineData(1_000_001, false, "past the ceiling refuses")]
    public void An_average_is_valid_only_within_its_floor_and_ceiling(int average, bool expected, string because) =>
        AppRateLimitPolicy.IsValidAverage(average).Should().Be(expected, because);

    [Theory]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    [InlineData(1, true)]
    [InlineData(150, true)]
    [InlineData(1_000_000, true)]
    [InlineData(1_000_001, false)]
    public void A_burst_is_valid_only_within_its_floor_and_ceiling(int burst, bool expected) =>
        AppRateLimitPolicy.IsValidBurst(burst).Should().Be(expected);

    [Fact]
    public void The_recommended_burst_is_half_the_recommended_average()
    {
        // Stated as a fact about the constants, not just as a comment: a real visitor's page load
        // fires a handful of requests at once and should sail through; a flood should not get a
        // whole extra minute's allowance before the steady rate catches it.
        AppRateLimitPolicy.RecommendedBurst.Should().Be(AppRateLimitPolicy.RecommendedAverage / 2);
    }

    [Fact]
    public void The_period_is_fixed_at_one_minute()
    {
        // Not a customer choice — see the type's own remarks for why. Pinned here so nobody changes
        // the unit the "requests per minute" copy on the app page promises without noticing.
        AppRateLimitPolicy.PeriodSeconds.Should().Be(60);
    }
}
