using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Monitoring;
using Harbora.Infrastructure.Monitoring;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Answering "is it healthy and how often does it crash" over a window, from whichever shape of data
/// actually reaches that far back.
///
/// <see cref="MetricsCollector"/> writes <c>app.up</c> as a plain 0/1 gauge and <c>app.restarts</c> as
/// a per-tick delta — see <see cref="RestartDelta"/> and <c>ContainerLifecycleCursor</c> for why the
/// second one is a delta rather than Docker's own raw counter. Both are ordinary gauge-shaped series
/// to <see cref="MetricRollups"/>, so nothing about the rollup pipeline had to change; what has to be
/// right is what this class does with what comes back: an average of <c>app.up</c> already <em>is</em>
/// the uptime fraction, but an average of <c>app.restarts</c> is restarts-per-tick and means nothing on
/// its own — only <c>Average × SampleCount</c> recovers the count that actually happened.
/// </summary>
public class LifecycleHistoryTests
{
    private static readonly Guid Server = Guid.NewGuid();
    private const string Container = "shop-1";
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private static HarboraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase("lifecycle-" + Guid.NewGuid()).Options);

    private static void SeedUp(HarboraDbContext db, DateTimeOffset at, double value) =>
        db.MonitoringMetrics.Add(new MonitoringMetric
        { ServerId = Server, Name = "app.up", ResourceRef = Container, Value = value, Timestamp = at });

    private static void SeedRestartDelta(HarboraDbContext db, DateTimeOffset at, double value) =>
        db.MonitoringMetrics.Add(new MonitoringMetric
        { ServerId = Server, Name = "app.restarts", ResourceRef = Container, Value = value, Timestamp = at });

    private static MetricRollup UpRollup(DateTimeOffset periodStart, double average, int sampleCount) => new()
    {
        ServerId = Server, Name = "app.up", ResourceRef = Container,
        Period = RollupPeriod.Day, PeriodStart = periodStart,
        Minimum = 0, Maximum = 1, Average = average, SampleCount = sampleCount
    };

    private static MetricRollup RestartRollup(DateTimeOffset periodStart, double average, int sampleCount) => new()
    {
        ServerId = Server, Name = "app.restarts", ResourceRef = Container,
        Period = RollupPeriod.Day, PeriodStart = periodStart,
        Minimum = 0, Maximum = 1, Average = average, SampleCount = sampleCount
    };

    // ---- uptime percent ----

    [Fact]
    public async Task Uptime_over_a_recent_window_is_read_straight_from_raw_samples()
    {
        using var db = NewDb();
        SeedUp(db, Now.AddMinutes(-30), 1);
        SeedUp(db, Now.AddMinutes(-20), 1);
        SeedUp(db, Now.AddMinutes(-10), 0);
        await db.SaveChangesAsync();

        var history = new LifecycleHistory(db);
        var uptime = await history.UptimePercentAsync(Server, Container, Now.AddHours(-1), Now, default);

        uptime.Should().BeApproximately(200.0 / 3, 0.01, "two of three ticks were up");
    }

    [Fact]
    public async Task Uptime_over_a_month_is_computed_from_rollups_because_raw_samples_do_not_reach_that_far()
    {
        // No raw MonitoringMetric rows at all — only what the collector would still have after the
        // 24-hour raw retention had pruned everything and the rollup service had already summarised
        // it. If this answer came from raw samples it would be null; it must not be.
        using var db = NewDb();
        var day1 = Now.AddDays(-30);
        var day2 = Now.AddDays(-29);
        db.MetricRollups.AddRange(UpRollup(day1, average: 1.0, sampleCount: 2880), UpRollup(day2, average: 0.5, sampleCount: 2880));
        await db.SaveChangesAsync();

        var history = new LifecycleHistory(db);
        var uptime = await history.UptimePercentAsync(Server, Container, Now.AddDays(-32), Now, default);

        uptime.Should().BeApproximately(75.0, 0.01, "one full day up and one half day, weighted equally by sample count");
    }

    [Fact]
    public async Task An_app_with_no_lifecycle_samples_at_all_reports_unknown_uptime_rather_than_100_percent()
    {
        using var db = NewDb();

        var history = new LifecycleHistory(db);
        var uptime = await history.UptimePercentAsync(Server, Container, Now.AddDays(-30), Now, default);

        uptime.Should().BeNull("nothing was ever collected for this container — that is not the same as a perfect record");
    }

    // ---- restart count ----

    [Fact]
    public async Task Restart_count_over_a_recent_window_sums_the_raw_deltas()
    {
        using var db = NewDb();
        SeedUp(db, Now.AddMinutes(-30), 1);
        SeedRestartDelta(db, Now.AddMinutes(-30), 0);
        SeedRestartDelta(db, Now.AddMinutes(-20), 1);
        SeedRestartDelta(db, Now.AddMinutes(-10), 0);
        await db.SaveChangesAsync();

        var history = new LifecycleHistory(db);
        var restarts = await history.RestartCountAsync(Server, Container, Now.AddHours(-1), Now, default);

        restarts.Should().Be(1);
    }

    [Fact]
    public async Task Restart_count_over_a_month_recovers_the_true_total_from_rollup_averages_not_the_average_itself()
    {
        // 0.1 restarts/tick over 120 ticks is 12 restarts that day. Reading the rollup's Average
        // directly — the exact mistake a naive reuse of the CPU chart's endpoint would make — would
        // report roughly zero, which is a number that looks plausible on a chart and is simply wrong.
        using var db = NewDb();
        var day = Now.AddDays(-30);
        db.MetricRollups.Add(UpRollup(day, average: 1.0, sampleCount: 120));
        db.MetricRollups.Add(RestartRollup(day, average: 0.1, sampleCount: 120));
        await db.SaveChangesAsync();

        var history = new LifecycleHistory(db);
        var restarts = await history.RestartCountAsync(Server, Container, Now.AddDays(-32), Now, default);

        restarts.Should().Be(12);
    }

    [Fact]
    public async Task Restart_count_is_zero_not_unknown_for_an_app_that_was_observed_and_never_restarted()
    {
        using var db = NewDb();
        SeedUp(db, Now.AddMinutes(-30), 1);
        SeedUp(db, Now.AddMinutes(-20), 1);
        await db.SaveChangesAsync();

        var history = new LifecycleHistory(db);
        var restarts = await history.RestartCountAsync(Server, Container, Now.AddHours(-1), Now, default);

        restarts.Should().Be(0, "the app was watched and genuinely never restarted — that is a measurement, not a gap");
    }

    [Fact]
    public async Task Restart_count_is_unknown_for_an_app_that_was_never_observed_at_all()
    {
        using var db = NewDb();

        var history = new LifecycleHistory(db);
        var restarts = await history.RestartCountAsync(Server, Container, Now.AddDays(-30), Now, default);

        restarts.Should().BeNull();
    }
}
