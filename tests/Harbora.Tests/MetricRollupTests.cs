using FluentAssertions;
using Harbora.Domain.Monitoring;
using Harbora.Infrastructure.Monitoring;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Keeping enough history to answer a question about last week.
///
/// Raw samples live for a day and are then deleted, so "was memory creeping up all week?" and "when
/// did this start?" had no data behind them — and those are the shape of most real capacity
/// problems. Completed periods are summarised instead, and the summaries have to survive the two
/// arithmetic traps that make most rollups quietly wrong.
/// </summary>
public class MetricRollupTests
{
    private static readonly Guid Server = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 14, 30, 0, TimeSpan.Zero);

    private static MonitoringMetric Sample(DateTimeOffset at, double value, string name = "cpu.percent") =>
        new() { ServerId = Server, Name = name, ResourceRef = "shop", Value = value, Timestamp = at };

    private static MetricRollup Hourly(DateTimeOffset hour, double min, double max, double average, int count) =>
        new()
        {
            ServerId = Server, Name = "cpu.percent", ResourceRef = "shop",
            Period = RollupPeriod.Hour, PeriodStart = hour,
            Minimum = min, Maximum = max, Average = average, SampleCount = count
        };

    [Fact]
    public void An_hours_samples_become_one_row_with_its_range()
    {
        var hour = new DateTimeOffset(2026, 7, 31, 13, 0, 0, TimeSpan.Zero);
        var samples = new[] { Sample(hour, 10), Sample(hour.AddMinutes(20), 50), Sample(hour.AddMinutes(40), 30) };

        var rollup = MetricRollups.ToHourly(samples, Now).Should().ContainSingle().Subject;

        rollup.Minimum.Should().Be(10);
        rollup.Maximum.Should().Be(50);
        rollup.Average.Should().Be(30);
        rollup.SampleCount.Should().Be(3);
        rollup.PeriodStart.Should().Be(hour);
    }

    [Fact]
    public void The_hour_that_is_still_running_is_left_alone()
    {
        // Summarising it would produce a number that changes and is then never corrected, because
        // the raw samples behind it are deleted before anyone looks again.
        var samples = new[] { Sample(Now.AddMinutes(-5), 42) };

        MetricRollups.ToHourly(samples, Now).Should().BeEmpty();
    }

    [Fact]
    public void Each_metric_and_each_resource_is_summarised_separately()
    {
        // Mixing an app's CPU with the host's would produce a number describing nothing.
        var hour = new DateTimeOffset(2026, 7, 31, 13, 0, 0, TimeSpan.Zero);
        var samples = new[]
        {
            Sample(hour, 10),
            Sample(hour, 90, name: "mem.used"),
            new MonitoringMetric { ServerId = Server, Name = "cpu.percent", ResourceRef = null, Value = 5, Timestamp = hour }
        };

        var rollups = MetricRollups.ToHourly(samples, Now);

        rollups.Should().HaveCount(3);

        // And each one carries what it describes: the per-app chart looks a summary up by exactly
        // this pair, so losing either label makes the row unreachable rather than merely untidy.
        rollups.Should().ContainSingle(r => r.Name == "cpu.percent" && r.ResourceRef == "shop");
        rollups.Should().ContainSingle(r => r.Name == "mem.used" && r.ResourceRef == "shop");
        rollups.Should().ContainSingle(r => r.Name == "cpu.percent" && r.ResourceRef == null);
    }

    [Fact]
    public void A_days_extremes_come_from_the_hourly_extremes_not_the_hourly_averages()
    {
        // The trap that makes a rollup useless: the spike is the thing being looked for, and taking
        // the daily maximum from hourly averages is exactly what hides it.
        var day = new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);
        var hourly = new[]
        {
            Hourly(day.AddHours(1), min: 5, max: 99, average: 20, count: 60),
            Hourly(day.AddHours(2), min: 10, max: 30, average: 22, count: 60)
        };

        var daily = MetricRollups.ToDaily(hourly, Now).Should().ContainSingle().Subject;

        daily.Maximum.Should().Be(99, "the spike must survive being summarised");
        daily.Minimum.Should().Be(5);
    }

    [Fact]
    public void A_days_average_is_weighted_by_how_many_samples_each_hour_held()
    {
        // An average of averages is wrong the moment two periods hold different counts — which is
        // every restart, every gap in collection.
        var day = new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);
        var hourly = new[]
        {
            Hourly(day.AddHours(1), 0, 100, average: 90, count: 90),
            Hourly(day.AddHours(2), 0, 100, average: 10, count: 10)
        };

        var daily = MetricRollups.ToDaily(hourly, Now).Should().ContainSingle().Subject;

        daily.Average.Should().Be(82, "90×90 + 10×10 over 100 samples — not the plain mean of 50");
        daily.SampleCount.Should().Be(100);
    }

    [Fact]
    public void The_day_that_is_still_running_is_left_alone()
    {
        var hourly = new[] { Hourly(MetricRollups.HourOf(Now).AddHours(-2), 1, 2, 1.5, 60) };

        MetricRollups.ToDaily(hourly, Now).Should().BeEmpty();
    }

    [Fact]
    public void Daily_rows_are_never_built_from_other_daily_rows()
    {
        // Feeding the output back in would double-count a day whose summary already exists.
        var alreadyDaily = new MetricRollup
        {
            ServerId = Server, Name = "cpu.percent", Period = RollupPeriod.Day,
            PeriodStart = new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero),
            Minimum = 1, Maximum = 2, Average = 1.5, SampleCount = 1440
        };

        MetricRollups.ToDaily([alreadyDaily], Now).Should().BeEmpty();
    }

    [Fact]
    public void An_hour_with_no_recorded_counts_still_produces_a_usable_average()
    {
        // Rows written before sample counts existed. Dividing by zero would be worse than a plain
        // mean, and losing the day entirely would be worse still.
        var day = new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);
        var hourly = new[]
        {
            Hourly(day.AddHours(1), 0, 10, average: 4, count: 0),
            Hourly(day.AddHours(2), 0, 10, average: 6, count: 0)
        };

        MetricRollups.ToDaily(hourly, Now).Should().ContainSingle().Which.Average.Should().Be(5);
    }

    [Theory]
    [InlineData(1, null)]                  // the last hour — raw
    [InlineData(24, null)]                 // the last day — still raw
    [InlineData(24 * 7, RollupPeriod.Hour)]   // last week — hourly
    [InlineData(24 * 90, RollupPeriod.Day)]   // last quarter — daily
    public void The_question_decides_which_shape_of_data_answers_it(int hours, RollupPeriod? expected)
    {
        // Reading raw points for a month returns tens of thousands of rows to draw a few hundred
        // pixels; reading daily summaries for the last hour returns one flat line.
        MetricRollups.BestSourceFor(TimeSpan.FromHours(hours)).Should().Be(expected);
    }

    [Fact]
    public void Summaries_are_kept_far_longer_than_the_samples_they_came_from()
    {
        // The entire reason they exist.
        MetricRollups.HourlyRetention.Should().BeGreaterThan(MetricRollups.RawRetention);
        MetricRollups.DailyRetention.Should().BeGreaterThan(MetricRollups.HourlyRetention);
    }

    [Fact]
    public void Nothing_to_summarise_is_not_an_error()
    {
        MetricRollups.ToHourly([], Now).Should().BeEmpty();
        MetricRollups.ToDaily([], Now).Should().BeEmpty();
    }
}
