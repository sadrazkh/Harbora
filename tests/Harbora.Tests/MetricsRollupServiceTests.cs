using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Monitoring;
using Harbora.Infrastructure.Monitoring;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Summarising history against the real database.
///
/// The rules are unit-tested separately; what this covers is the part that only goes wrong in
/// context — running twice, and never summarising the same period again.
/// </summary>
public class MetricsRollupServiceTests : IDisposable
{
    private readonly HarboraDbContext _db;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 7, 31, 14, 30, 0, TimeSpan.Zero));
    private readonly Guid _server = Guid.NewGuid();

    public MetricsRollupServiceTests()
    {
        _db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("rollup-" + Guid.NewGuid()).Options);
    }

    public void Dispose() => _db.Dispose();

    private MetricsRollupService Service() =>
        new(_db, _clock, NullLogger<MetricsRollupService>.Instance);

    private void GivenSamples(DateTimeOffset hour, params double[] values)
    {
        var minute = 0;
        foreach (var value in values)
            _db.MonitoringMetrics.Add(new MonitoringMetric
            {
                ServerId = _server, Name = "cpu.percent", ResourceRef = "shop",
                Value = value, Timestamp = hour.AddMinutes(minute += 5)
            });
        _db.SaveChanges();
    }

    [Fact]
    public async Task A_finished_hour_is_summarised()
    {
        GivenSamples(new DateTimeOffset(2026, 7, 31, 13, 0, 0, TimeSpan.Zero), 10, 20, 60);

        await Service().RunAsync(default);

        var rollup = _db.MetricRollups.Single(r => r.Period == RollupPeriod.Hour);
        rollup.Maximum.Should().Be(60);
        rollup.SampleCount.Should().Be(3);
    }

    [Fact]
    public async Task Running_twice_does_not_summarise_the_same_hour_again()
    {
        // Arithmetically harmless and permanently untidy: nothing would ever remove the duplicate.
        GivenSamples(new DateTimeOffset(2026, 7, 31, 13, 0, 0, TimeSpan.Zero), 10, 20);

        await Service().RunAsync(default);
        await Service().RunAsync(default);

        _db.MetricRollups.Count(r => r.Period == RollupPeriod.Hour).Should().Be(1);
    }

    [Fact]
    public async Task Samples_from_the_hour_still_running_are_summarised_later_not_now()
    {
        GivenSamples(MetricRollups.HourOf(_clock.UtcNow), 42);

        await Service().RunAsync(default);
        _db.MetricRollups.Should().BeEmpty();

        // An hour later it is finished, and it is picked up.
        _clock.UtcNow = _clock.UtcNow.AddHours(1);
        await Service().RunAsync(default);

        _db.MetricRollups.Should().ContainSingle(r => r.Period == RollupPeriod.Hour);
    }

    [Fact]
    public async Task A_finished_day_is_summarised_from_its_hours()
    {
        var yesterday = new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);
        GivenSamples(yesterday.AddHours(1), 10, 90);
        GivenSamples(yesterday.AddHours(2), 20, 40);

        await Service().RunAsync(default);

        var daily = _db.MetricRollups.Single(r => r.Period == RollupPeriod.Day);
        daily.Maximum.Should().Be(90, "the spike survives both levels of summarising");
        daily.SampleCount.Should().Be(4);
    }

    [Fact]
    public async Task Running_twice_does_not_summarise_the_same_day_again()
    {
        // The same trap as the hourly one, one level up — and the one my first set of tests missed:
        // a duplicate day is arithmetically harmless and nothing would ever remove it.
        var yesterday = new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);
        GivenSamples(yesterday.AddHours(1), 10, 90);

        await Service().RunAsync(default);
        await Service().RunAsync(default);

        _db.MetricRollups.Count(r => r.Period == RollupPeriod.Day).Should().Be(1);
    }

    [Fact]
    public async Task Summaries_older_than_their_retention_are_removed()
    {
        _db.MetricRollups.Add(new MetricRollup
        {
            ServerId = _server, Name = "cpu.percent", Period = RollupPeriod.Hour,
            PeriodStart = _clock.UtcNow - MetricRollups.HourlyRetention - TimeSpan.FromDays(1),
            Minimum = 1, Maximum = 2, Average = 1.5, SampleCount = 60
        });
        _db.SaveChanges();

        await Service().RunAsync(default);

        // Gone as an hourly row — but rolled into a daily one first, which is the ordering doing
        // its job: history is summarised before it is let go, never dropped.
        _db.MetricRollups.Should().NotContain(r => r.Period == RollupPeriod.Hour);
        _db.MetricRollups.Should().Contain(r => r.Period == RollupPeriod.Day);
    }

    [Fact]
    public async Task A_summary_older_than_a_year_is_gone_for_good()
    {
        // Nothing further to summarise it into, so this is the end of the line.
        _db.MetricRollups.Add(new MetricRollup
        {
            ServerId = _server, Name = "cpu.percent", Period = RollupPeriod.Day,
            PeriodStart = _clock.UtcNow - MetricRollups.DailyRetention - TimeSpan.FromDays(1),
            Minimum = 1, Maximum = 2, Average = 1.5, SampleCount = 1440
        });
        _db.SaveChanges();

        await Service().RunAsync(default);

        _db.MetricRollups.Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_collected_yet_is_not_an_error()
    {
        var act = async () => await Service().RunAsync(default);

        await act.Should().NotThrowAsync();
    }
}
