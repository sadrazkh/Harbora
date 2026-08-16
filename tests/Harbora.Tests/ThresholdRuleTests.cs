using FluentAssertions;
using Harbora.Infrastructure.Monitoring;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// When a per-application threshold has actually been breached.
///
/// The sustain window is the whole point. A container touches 100% CPU every time it starts and a
/// memory figure spikes during a collection — alerting on one sample fills a channel with noise,
/// and a muted channel reports nothing at all. So the breach must hold for the window, one sample
/// below the line clears it, and a gap in collection is neither.
/// </summary>
public class ThresholdRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

    /// <summary>Samples every minute over the given window, newest last.</summary>
    private static List<MetricSample> Series(params double?[] percentsOldestFirst)
    {
        var count = percentsOldestFirst.Length;
        return percentsOldestFirst
            .Select((p, i) => new MetricSample(Now.AddMinutes(-(count - 1 - i)), p))
            .ToList();
    }

    [Fact]
    public void A_sustained_breach_fires()
    {
        ThresholdRule.Breached(Series(95, 96, 94, 99, 97, 98), 90, FiveMinutes, Now).Should().BeTrue();
    }

    [Fact]
    public void A_single_spike_says_nothing()
    {
        // The whole reason the sustain exists: a container's first seconds are not an incident.
        ThresholdRule.Breached(Series(10, 12, 11, 100, 12, 11), 90, FiveMinutes, Now).Should().BeFalse();
    }

    [Fact]
    public void One_sample_below_the_line_clears_the_window()
    {
        // Anything else would let a breach that recovered mid-window still fire.
        ThresholdRule.Breached(Series(95, 96, 89, 99, 97, 98), 90, FiveMinutes, Now).Should().BeFalse();
    }

    [Fact]
    public void Exactly_at_the_line_counts_as_at_or_above()
    {
        ThresholdRule.Breached(Series(90, 90, 90, 90, 90, 90), 90, FiveMinutes, Now).Should().BeTrue();
    }

    [Fact]
    public void An_app_younger_than_the_window_has_not_sustained_anything()
    {
        // Two minutes of 100% cannot satisfy a five-minute sustain, however alarming it looks.
        var young = new List<MetricSample>
        {
            new(Now.AddMinutes(-2), 100),
            new(Now.AddMinutes(-1), 100),
            new(Now, 100)
        };

        ThresholdRule.Breached(young, 90, FiveMinutes, Now).Should().BeFalse();
    }

    [Fact]
    public void A_gap_in_collection_is_neither_a_breach_nor_a_recovery()
    {
        // Unmeasured is not zero and not 100. Silence must not be read as either.
        ThresholdRule.Breached(Series(95, 96, null, 99, 97, 98), 90, FiveMinutes, Now).Should().BeFalse();
    }

    [Fact]
    public void No_samples_at_all_is_silence()
    {
        ThresholdRule.Breached([], 90, FiveMinutes, Now).Should().BeFalse();
    }

    [Fact]
    public void Samples_outside_the_window_do_not_count_against_it()
    {
        // Yesterday's calm does not clear today's breach, and yesterday's breach does not cause one.
        var mixed = new List<MetricSample> { new(Now.AddHours(-3), 5) };
        mixed.AddRange(Series(95, 96, 97, 99, 97, 98));

        ThresholdRule.Breached(mixed, 90, FiveMinutes, Now).Should().BeTrue();
    }

    [Fact]
    public void A_sample_from_the_future_is_ignored()
    {
        // Clock skew between the collector and the evaluator must not conjure a breach.
        var skewed = new List<MetricSample> { new(Now.AddMinutes(5), 100) };

        ThresholdRule.Breached(skewed, 90, FiveMinutes, Now).Should().BeFalse();
    }

    [Fact]
    public void A_future_sample_cannot_suppress_a_real_breach_either()
    {
        // The other direction, and the one that matters more: a sample stamped ahead of the
        // evaluator's clock sits below the line and would clear a breach that is genuinely
        // happening — an alert silenced by a clock, which nobody would ever diagnose.
        var breach = Series(95, 96, 97, 99, 97, 98);
        breach.Add(new MetricSample(Now.AddMinutes(3), 5));

        ThresholdRule.Breached(breach, 90, FiveMinutes, Now).Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_threshold_of_nothing_never_fires(double threshold)
    {
        // An unconfigured rule must be inert, not a rule that fires on every sample.
        ThresholdRule.Breached(Series(1, 1, 1, 1, 1, 1), threshold, FiveMinutes, Now).Should().BeFalse();
    }

    [Fact]
    public void A_zero_sustain_fires_on_the_latest_sample()
    {
        // Somebody who genuinely wants "tell me the moment it happens" can have it.
        ThresholdRule.Breached(Series(10, 10, 95), 90, TimeSpan.Zero, Now).Should().BeTrue();
        ThresholdRule.Breached(Series(95, 95, 10), 90, TimeSpan.Zero, Now).Should().BeFalse();
    }

    // --- repeating ---

    [Fact]
    public void A_first_breach_is_always_reported()
    {
        ThresholdRule.MayRepeat(null, Now).Should().BeTrue();
    }

    [Fact]
    public void A_standing_breach_nags_rather_than_floods()
    {
        // The collector ticks every few minutes; without this the channel gets one message per tick
        // for as long as the app is busy.
        ThresholdRule.MayRepeat(Now.AddMinutes(-5), Now).Should().BeFalse();
        ThresholdRule.MayRepeat(Now - ThresholdRule.RepeatAfter, Now).Should().BeTrue();
    }

    [Fact]
    public void Omitting_the_repeat_window_falls_back_to_the_shipped_default()
    {
        // Every call site that does not carry a configured MonitoringOptions.ThresholdRepeatAfterHours
        // — this file's own tests included — must see exactly the old, hardcoded hour.
        ThresholdRule.MayRepeat(Now.AddMinutes(-30), Now).Should().BeFalse("thirty minutes is under the default hour");
        ThresholdRule.MayRepeat(Now.AddHours(-1), Now).Should().BeTrue("a full hour has passed");
    }

    [Fact]
    public void A_configured_repeat_window_shorter_than_the_default_allows_an_earlier_repeat()
    {
        // The whole point of MonitoringOptions.ThresholdRepeatAfterHours: an operator who wants a
        // noisier nag can have one, without waiting for MayRepeat's caller to be rewritten.
        var tenMinutes = TimeSpan.FromMinutes(10);

        ThresholdRule.MayRepeat(Now.AddMinutes(-5), Now, tenMinutes).Should()
            .BeFalse("five minutes is under the configured ten");
        ThresholdRule.MayRepeat(Now.AddMinutes(-11), Now, tenMinutes).Should()
            .BeTrue("eleven minutes has passed the configured ten, though not the shipped hour");
    }

    [Fact]
    public void A_configured_repeat_window_longer_than_the_default_delays_a_repeat_that_would_otherwise_fire()
    {
        var threeHours = TimeSpan.FromHours(3);

        ThresholdRule.MayRepeat(Now - ThresholdRule.RepeatAfter, Now, threeHours).Should()
            .BeFalse("the shipped hour has passed but the configured three hours has not");
        ThresholdRule.MayRepeat(Now.AddHours(-3), Now, threeHours).Should().BeTrue();
    }
}
