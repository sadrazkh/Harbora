using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Monitoring;
using Harbora.Infrastructure.Monitoring;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The collector half of the uptime/restart series: what actually gets written to
/// <see cref="MonitoringMetric"/> and <see cref="ContainerLifecycleCursor"/> on each tick.
///
/// <para>
/// The case that matters most is a redeploy: Docker's restart counter starts over at zero for the new
/// container, and the collector must not read that drop as −N restarts. See <see cref="RestartDelta"/>
/// for the arithmetic; these tests are about the collector actually calling it with the right numbers,
/// tick after tick, through a real cursor round-trip in the database.
/// </para>
/// </summary>
public class ContainerLifecycleCollectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 9, 0, 0, TimeSpan.Zero);

    private sealed record Harness(
        MetricsCollector Collector, HarboraDbContext Db, FakeDockerEngine Engine, FixedClock Clock, Domain.Servers.Server Server);

    private static Harness NewHarness()
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("lifecycle-collection-" + Guid.NewGuid()).Options);
        var engine = new FakeDockerEngine();
        var factory = new FakeServerEngineFactory(engine);
        var notifications = new RecordingNotificationService();
        var throttle = new AlertThrottle();
        var clock = new FixedClock(Now);
        var rollups = new MetricsRollupService(db, clock, NullLogger<MetricsRollupService>.Instance);

        var server = new Domain.Servers.Server { Name = "node-1", IsLocal = true };
        db.Servers.Add(server);
        db.SaveChanges();

        var collector = new MetricsCollector(
            db, factory, notifications, new IncidentService(db), throttle, clock, rollups,
            Options.Create(new MonitoringOptions()), NullLogger<MetricsCollector>.Instance);

        return new Harness(collector, db, engine, clock, server);
    }

    private static IReadOnlyList<MonitoringMetric> SamplesNamed(HarboraDbContext db, string name) =>
        db.MonitoringMetrics.Where(m => m.Name == name).ToList();

    [Fact]
    public async Task A_running_container_is_recorded_as_up_on_every_tick()
    {
        var h = NewHarness();
        h.Engine.SeedContainer("shop-1", "shop", state: "running");
        h.Engine.SeedDetail("shop-1", new ContainerDetail(
            "shop-1", "shop-1", "img", null, "running", "Up", null, RestartCount: 0, StartedAt: Now.AddHours(-2)));

        await h.Collector.CollectAsync(default);

        SamplesNamed(h.Db, "app.up").Should().ContainSingle(m => m.ResourceRef == "shop-1" && m.Value == 1);
    }

    [Fact]
    public async Task A_container_that_is_not_running_is_recorded_as_down_not_silently_skipped()
    {
        var h = NewHarness();
        h.Engine.SeedContainer("shop-1", "shop", state: "exited");

        await h.Collector.CollectAsync(default);

        SamplesNamed(h.Db, "app.up").Should().ContainSingle(m => m.ResourceRef == "shop-1" && m.Value == 0);
    }

    [Fact]
    public async Task The_first_tick_a_container_is_seen_remembers_the_baseline_but_writes_no_restart_sample()
    {
        var h = NewHarness();
        h.Engine.SeedContainer("shop-1", "shop", state: "running");
        h.Engine.SeedDetail("shop-1", new ContainerDetail(
            "shop-1", "shop-1", "img", null, "running", "Up", null, RestartCount: 3, StartedAt: Now.AddHours(-2)));

        await h.Collector.CollectAsync(default);

        SamplesNamed(h.Db, "app.restarts").Should().BeEmpty("there is nothing yet to compute a delta against");
        var cursor = h.Db.ContainerLifecycleCursors.Should().ContainSingle().Subject;
        cursor.ServerId.Should().Be(h.Server.Id);
        cursor.ResourceRef.Should().Be("shop-1");
        cursor.LastRestartCount.Should().Be(3);
    }

    [Fact]
    public async Task A_restart_between_two_ticks_is_recorded_as_a_delta_of_one()
    {
        var h = NewHarness();
        h.Engine.SeedContainer("shop-1", "shop", state: "running");
        h.Engine.SeedDetail("shop-1", new ContainerDetail(
            "shop-1", "shop-1", "img", null, "running", "Up", null, RestartCount: 3, StartedAt: Now.AddHours(-2)));
        await h.Collector.CollectAsync(default);

        h.Clock.UtcNow = Now.AddSeconds(30);
        h.Engine.SeedDetail("shop-1", new ContainerDetail(
            "shop-1", "shop-1", "img", null, "running", "Up", null, RestartCount: 4, StartedAt: Now.AddHours(-2)));
        await h.Collector.CollectAsync(default);

        SamplesNamed(h.Db, "app.restarts").Should().ContainSingle(m => m.ResourceRef == "shop-1" && m.Value == 1);
        h.Db.ContainerLifecycleCursors.Single().LastRestartCount.Should().Be(4);
    }

    [Fact]
    public async Task A_redeployed_container_whose_counter_resets_produces_a_zero_delta_not_a_negative_one()
    {
        // The exact case the design has to get right: RestartCount going from 5 to 0 is a container
        // replacement (a redeploy), not five restarts undoing themselves.
        var h = NewHarness();
        h.Engine.SeedContainer("shop-1", "shop", state: "running");
        h.Engine.SeedDetail("shop-1", new ContainerDetail(
            "shop-1", "shop-1", "img", null, "running", "Up", null, RestartCount: 5, StartedAt: Now.AddHours(-6)));
        await h.Collector.CollectAsync(default);

        h.Clock.UtcNow = Now.AddSeconds(30);
        h.Engine.SeedDetail("shop-1", new ContainerDetail(
            "shop-1", "shop-1", "img", null, "running", "Up", null, RestartCount: 0, StartedAt: h.Clock.UtcNow));
        await h.Collector.CollectAsync(default);

        var deltas = SamplesNamed(h.Db, "app.restarts").Where(m => m.ResourceRef == "shop-1").ToList();
        deltas.Should().ContainSingle();
        deltas[0].Value.Should().Be(0, "a replacement is not five restarts happening in one tick");
        deltas.Should().NotContain(m => m.Value < 0);
    }

    [Fact]
    public async Task A_running_container_whose_engine_declines_the_lifecycle_call_still_records_uptime()
    {
        // No SeedDetail — the fake answers GetLifecycleAsync with null, the same as an older node
        // agent or an engine that timed out on this one call. Uptime tracking does not depend on it.
        var h = NewHarness();
        h.Engine.SeedContainer("shop-1", "shop", state: "running");

        await h.Collector.CollectAsync(default);

        SamplesNamed(h.Db, "app.up").Should().ContainSingle(m => m.ResourceRef == "shop-1" && m.Value == 1);
        SamplesNamed(h.Db, "app.restarts").Should().BeEmpty();
        h.Db.ContainerLifecycleCursors.Should().BeEmpty("nothing was learned about the restart count this tick");
    }
}
