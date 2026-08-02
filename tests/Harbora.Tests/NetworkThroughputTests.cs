using FluentAssertions;
using Harbora.Infrastructure.Monitoring;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Turning Docker's byte counters into a rate.
///
/// The counters are cumulative since the container started, so a chart of the raw value is a line
/// that only ever climbs — it says nothing about throughput. The rate is the difference over time.
///
/// The case that decides whether this is safe is a restart: the counter goes back to zero, and a
/// naive subtraction turns that into a negative rate or, once clamped, an enormous spike that looks
/// like a traffic flood at exactly the moment the service was down. Unknown has to stay unknown.
/// </summary>
public class NetworkThroughputTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_normal_interval_gives_bytes_per_second()
    {
        // 60 KB over 60 seconds is 1 KB/s.
        NetworkThroughput.Between(1_000, T0, 61_000, T0.AddSeconds(60)).Should().Be(1_000);
    }

    [Fact]
    public void A_restart_is_unknown_rather_than_negative()
    {
        // The counter reset. We cannot know what happened in between — only that this sample is not
        // comparable with the last one.
        NetworkThroughput.Between(9_000_000, T0, 12_000, T0.AddSeconds(30)).Should().BeNull();
    }

    [Fact]
    public void A_restart_is_not_reported_as_zero()
    {
        // The specific wrong answer this guards: zero is a measurement, and a flat line through an
        // outage is the most reassuring lie a dashboard can tell.
        NetworkThroughput.Between(5_000, T0, 0, T0.AddSeconds(30)).Should().NotBe(0);
    }

    [Fact]
    public void No_previous_sample_is_unknown()
    {
        // The first tick after a deploy has nothing to subtract from.
        NetworkThroughput.Between(null, null, 12_000, T0).Should().BeNull();
    }

    [Fact]
    public void Two_samples_at_the_same_instant_are_unknown()
    {
        // Dividing by zero elapsed time is infinity, which renders as a number.
        NetworkThroughput.Between(1_000, T0, 2_000, T0).Should().BeNull();
    }

    [Fact]
    public void A_sample_older_than_the_one_before_it_is_unknown()
    {
        // Out-of-order rows are possible when two collectors overlap; a negative interval would
        // flip the sign of a perfectly good delta.
        NetworkThroughput.Between(1_000, T0, 2_000, T0.AddSeconds(-30)).Should().BeNull();
    }

    [Fact]
    public void An_unchanged_counter_is_genuinely_zero()
    {
        // The one case where zero is the truth: the container is up, was measured twice, and moved
        // no traffic. This is what stops the rule from being "return null when unsure" everywhere.
        NetworkThroughput.Between(5_000, T0, 5_000, T0.AddSeconds(60)).Should().Be(0);
    }

    [Fact]
    public void A_gap_too_long_to_trust_is_unknown()
    {
        // The panel was down for an hour. Averaging an hour of traffic into one point draws a flat
        // line across a gap where anything could have happened.
        NetworkThroughput.Between(1_000, T0, 2_000, T0.Add(NetworkThroughput.MaxGap).AddSeconds(1))
            .Should().BeNull();
    }

    [Fact]
    public void A_gap_exactly_at_the_limit_is_still_trusted()
    {
        // The boundary belongs to the trusted side, so a collector ticking exactly at the limit does
        // not silently stop reporting.
        NetworkThroughput.Between(1_000, T0, 2_000, T0.Add(NetworkThroughput.MaxGap))
            .Should().NotBeNull();
    }
}

/// <summary>
/// Which metrics may be summarised.
///
/// A rollup stores a minimum, a maximum and an average. For a gauge those are three facts. For a
/// counter that only climbs they are three numbers that look like facts — and the average would be
/// charted as network usage by anything reading the same table.
/// </summary>
public class CumulativeMetricTests
{
    [Fact]
    public void Network_counters_are_not_summarised()
    {
        Harbora.Infrastructure.Monitoring.MetricRollups.IsCumulative("net.rx").Should().BeTrue();
        Harbora.Infrastructure.Monitoring.MetricRollups.IsCumulative("net.tx").Should().BeTrue();
    }

    [Fact]
    public void Gauges_still_are()
    {
        // The guard on the rule above: excluding too much would quietly delete the history that
        // makes the monitoring page worth opening.
        Harbora.Infrastructure.Monitoring.MetricRollups.IsCumulative("cpu.percent").Should().BeFalse();
        Harbora.Infrastructure.Monitoring.MetricRollups.IsCumulative("mem.used").Should().BeFalse();
        Harbora.Infrastructure.Monitoring.MetricRollups.IsCumulative("disk.total").Should().BeFalse();
    }

    [Fact]
    public void A_counter_is_kept_out_of_the_hourly_summary()
    {
        var now = new DateTimeOffset(2026, 8, 2, 13, 30, 0, TimeSpan.Zero);
        var earlier = new DateTimeOffset(2026, 8, 2, 12, 10, 0, TimeSpan.Zero);
        var samples = new[]
        {
            new Harbora.Domain.Monitoring.MonitoringMetric { Name = "net.rx", Value = 1000, Timestamp = earlier },
            new Harbora.Domain.Monitoring.MonitoringMetric { Name = "cpu.percent", Value = 12, Timestamp = earlier }
        };

        var hourly = Harbora.Infrastructure.Monitoring.MetricRollups.ToHourly(samples, now);

        hourly.Should().ContainSingle().Which.Name.Should().Be("cpu.percent");
    }
}

/// <summary>
/// Showing a rate. The gate applies here too: a rate that could not be worked out must render as
/// "not collected", never as 0 B/s.
/// </summary>
public class ThroughputDisplayTests
{
    [Fact]
    public void An_unknown_rate_renders_nothing_measured()
    {
        var view = Harbora.Infrastructure.Monitoring.MetricDisplay.ForThroughput(null);

        view.HasData.Should().BeFalse();
        view.Text.Should().BeEmpty();
    }

    [Fact]
    public void A_genuine_zero_is_still_shown()
    {
        // The container is up and moved no traffic. That is a measurement.
        var view = Harbora.Infrastructure.Monitoring.MetricDisplay.ForThroughput(0);

        view.HasData.Should().BeTrue();
        view.Text.Should().Be("0 B/s");
    }

    [Theory]
    [InlineData(512, "512 B/s")]
    [InlineData(2048, "2 KB/s")]
    [InlineData(1_572_864, "1.5 MB/s")]
    [InlineData(3_221_225_472, "3 GB/s")]
    public void A_rate_is_scaled_to_a_unit_people_read(double bytesPerSecond, string expected)
    {
        Harbora.Infrastructure.Monitoring.MetricDisplay.ForThroughput(bytesPerSecond).Text.Should().Be(expected);
    }

    [Fact]
    public void A_negative_rate_is_treated_as_unknown_not_displayed()
    {
        // Should be impossible — NetworkThroughput returns null for a reset — but if one ever
        // arrives, "-4 GB/s" on a dashboard is worse than a blank.
        Harbora.Infrastructure.Monitoring.MetricDisplay.ForThroughput(-1).HasData.Should().BeFalse();
    }
}
