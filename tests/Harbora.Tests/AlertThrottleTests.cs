using FluentAssertions;
using Harbora.Infrastructure.Monitoring;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Keeping a standing condition from alerting every 30 seconds, without silencing anything else.
///
/// The disk warning used one <c>static</c> timestamp on a scoped collector: shared by every server
/// and every workspace, so the first node to fill up muted the warning for all the others.
/// </summary>
public class AlertThrottleTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    [Fact]
    public void The_first_occurrence_always_fires()
        => new AlertThrottle().ShouldFire("disk:a", Now, Hour).Should().BeTrue();

    [Fact]
    public void A_repeat_within_the_interval_is_suppressed()
    {
        var throttle = new AlertThrottle();
        throttle.ShouldFire("disk:a", Now, Hour);

        throttle.ShouldFire("disk:a", Now.AddMinutes(59), Hour).Should().BeFalse();
    }

    [Fact]
    public void It_fires_again_once_the_interval_has_passed()
    {
        var throttle = new AlertThrottle();
        throttle.ShouldFire("disk:a", Now, Hour);

        throttle.ShouldFire("disk:a", Now.AddMinutes(61), Hour).Should().BeTrue();
    }

    [Fact]
    public void One_subject_does_not_silence_another()
    {
        // The whole point: a full disk on one node must not hide a full disk on the next.
        var throttle = new AlertThrottle();
        throttle.ShouldFire("disk:node-a", Now, Hour).Should().BeTrue();

        throttle.ShouldFire("disk:node-b", Now, Hour).Should().BeTrue();
    }

    [Fact]
    public void A_suppressed_attempt_does_not_extend_the_silence()
    {
        // If every blocked attempt reset the clock, a condition checked every 30 seconds would never
        // alert a second time at all.
        var throttle = new AlertThrottle();
        throttle.ShouldFire("disk:a", Now, Hour);
        throttle.ShouldFire("disk:a", Now.AddMinutes(30), Hour).Should().BeFalse();

        throttle.ShouldFire("disk:a", Now.AddMinutes(61), Hour).Should().BeTrue();
    }
}
