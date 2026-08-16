using FluentAssertions;
using Harbora.Infrastructure.Monitoring;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The one piece of arithmetic the whole restart series depends on: turning Docker's own ever-climbing
/// counter into the per-tick delta that is actually written to the metrics table.
///
/// A restart count is a counter, not a gauge — but rather than teach the rollup pipeline a second kind
/// of counter (on top of the network one it already excludes), the collector converts it to a delta
/// before it is ever recorded. This is the function that does that conversion, and it is the only
/// place a container replacement's reset has to be recognised.
/// </summary>
public class RestartDeltaTests
{
    [Fact]
    public void Two_more_restarts_than_last_time_is_a_delta_of_two()
    {
        RestartDelta.Between(previousCount: 3, currentCount: 5).Should().Be(2);
    }

    [Fact]
    public void The_same_count_as_last_time_is_a_delta_of_zero()
    {
        RestartDelta.Between(previousCount: 4, currentCount: 4).Should().Be(0);
    }

    [Fact]
    public void A_lower_count_than_last_time_reads_as_a_container_replacement_not_a_negative_restart()
    {
        // Docker's own counter never goes down while a container keeps its identity. A lower reading
        // means the container was replaced (a redeploy, not a restart) and its counter started over
        // at zero — attributing the drop to this tick as a negative delta would corrupt every sum
        // this series is rolled up into afterwards.
        RestartDelta.Between(previousCount: 5, currentCount: 0).Should().Be(0);
    }

    [Fact]
    public void A_partial_reset_still_never_produces_a_negative_delta()
    {
        // Not just the all-the-way-to-zero case: any drop is a reset, however small.
        RestartDelta.Between(previousCount: 5, currentCount: 2).Should().Be(0);
    }
}
