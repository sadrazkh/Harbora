using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Monitoring;
using Harbora.Domain.Servers;
using Harbora.Infrastructure.Monitoring;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Proves that <see cref="MonitoringOptions"/> is not merely bound but actually consulted: a
/// configured disk ratio, disk-alert interval, or threshold repeat window changes whether
/// <see cref="MetricsCollector"/> fires — not just what number it carries.
///
/// <para>
/// Every scenario is paired: the same facts, once against the shipped default (proving nothing
/// changed for an install that configures nothing) and once against a deliberately different
/// configured value (proving the knob is real).
/// </para>
/// </summary>
public class MetricsCollectorOptionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed record Harness(
        MetricsCollector Collector, HarboraDbContext Db, FakeDockerEngine Engine,
        RecordingNotificationService Notifications, FixedClock Clock, Server Server);

    private static Harness NewHarness(MonitoringOptions? options = null)
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("metrics-options-" + Guid.NewGuid()).Options);
        var engine = new FakeDockerEngine();
        var factory = new FakeServerEngineFactory(engine);
        var notifications = new RecordingNotificationService();
        var throttle = new Harbora.Infrastructure.Monitoring.AlertThrottle();
        var clock = new FixedClock(Now);
        var rollups = new MetricsRollupService(db, clock, NullLogger<MetricsRollupService>.Instance);

        var server = new Server { Name = "node-1", IsLocal = true };
        db.Servers.Add(server);
        db.SaveChanges();

        var collector = new MetricsCollector(
            db, factory, notifications, new IncidentService(db), throttle, clock, rollups,
            Options.Create(options ?? new MonitoringOptions()), NullLogger<MetricsCollector>.Instance);

        return new Harness(collector, db, engine, notifications, clock, server);
    }

    private static void SeedDiskWatchingRule(HarboraDbContext db, Guid workspaceId) => db.Alerts.Add(new Alert
    {
        WorkspaceId = workspaceId, Name = "disk", Channel = AlertChannel.Webhook,
        EncryptedTarget = "x", IsEnabled = true, OnDiskWarning = true
    });

    // ---- disk warn ratio ----

    [Fact]
    public async Task A_disk_at_70_percent_alerts_once_the_ratio_is_configured_down_to_60_percent()
    {
        var h = NewHarness(new MonitoringOptions { DiskWarnRatio = 0.60 });
        SeedDiskWatchingRule(h.Db, Guid.CreateVersion7());
        h.Db.SaveChanges();
        h.Engine.TotalDiskBytes = 100L << 30;
        h.Engine.FreeDiskBytes = 30L << 30; // 70% used

        await h.Collector.CollectAsync(default);

        h.Notifications.Notifications.Should().Contain(n => n.Event == AlertEvent.DiskWarning);
    }

    [Fact]
    public async Task The_shipped_default_ratio_leaves_the_same_70_percent_disk_unremarked()
    {
        var h = NewHarness(); // default 0.85
        SeedDiskWatchingRule(h.Db, Guid.CreateVersion7());
        h.Db.SaveChanges();
        h.Engine.TotalDiskBytes = 100L << 30;
        h.Engine.FreeDiskBytes = 30L << 30; // 70% used

        await h.Collector.CollectAsync(default);

        h.Notifications.Notifications.Should().NotContain(n => n.Event == AlertEvent.DiskWarning);
    }

    [Fact]
    public async Task A_configured_ratio_higher_than_the_default_quiets_a_disk_the_default_would_flag()
    {
        var h = NewHarness(new MonitoringOptions { DiskWarnRatio = 0.95 });
        SeedDiskWatchingRule(h.Db, Guid.CreateVersion7());
        h.Db.SaveChanges();
        h.Engine.TotalDiskBytes = 100L << 30;
        h.Engine.FreeDiskBytes = 10L << 30; // 90% used — over the shipped default, under the configured one

        await h.Collector.CollectAsync(default);

        h.Notifications.Notifications.Should().NotContain(n => n.Event == AlertEvent.DiskWarning);
    }

    // ---- disk alert interval ----

    [Fact]
    public async Task A_second_disk_alert_ten_minutes_later_is_suppressed_by_the_shipped_default_interval()
    {
        var h = NewHarness(); // default: one hour
        SeedDiskWatchingRule(h.Db, Guid.CreateVersion7());
        h.Db.SaveChanges();
        h.Engine.TotalDiskBytes = 100L << 30;
        h.Engine.FreeDiskBytes = 5L << 30; // 95% used, well past any default

        await h.Collector.CollectAsync(default);
        h.Clock.UtcNow = Now.AddMinutes(10);
        await h.Collector.CollectAsync(default);

        h.Notifications.Notifications.Count(n => n.Event == AlertEvent.DiskWarning).Should().Be(1,
            "one node filling up must nag once per interval, not once per collector tick");
    }

    [Fact]
    public async Task Configuring_a_five_minute_interval_lets_a_second_disk_alert_fire_ten_minutes_later()
    {
        var h = NewHarness(new MonitoringOptions { DiskAlertIntervalHours = 5.0 / 60 });
        SeedDiskWatchingRule(h.Db, Guid.CreateVersion7());
        h.Db.SaveChanges();
        h.Engine.TotalDiskBytes = 100L << 30;
        h.Engine.FreeDiskBytes = 5L << 30;

        await h.Collector.CollectAsync(default);
        h.Clock.UtcNow = Now.AddMinutes(10);
        await h.Collector.CollectAsync(default);

        h.Notifications.Notifications.Count(n => n.Event == AlertEvent.DiskWarning).Should().Be(2,
            "ten minutes is past the configured five-minute interval, even though it is under the shipped hour");
    }

    // ---- threshold repeat window ----

    private static (App App, Deployment Deployment) SeedThresholdApp(HarboraDbContext db, Guid workspaceId, Guid serverId)
    {
        var app = new App
        {
            WorkspaceId = workspaceId, ServerId = serverId, Name = "api", Slug = "api-" + Guid.NewGuid().ToString("N")[..8],
            CpuLimit = 1.0, Status = AppStatus.Running
        };
        db.Apps.Add(app);
        db.SaveChanges();

        var deployment = new Deployment
        {
            AppId = app.Id, WorkspaceId = workspaceId, Number = 1, Status = DeploymentStatus.Succeeded
        };
        db.Deployments.Add(deployment);
        app.ActiveDeploymentId = deployment.Id;
        db.SaveChanges();

        return (app, deployment);
    }

    private static void SeedCpuSample(HarboraDbContext db, Guid serverId, string containerName, double value, DateTimeOffset at) =>
        db.MonitoringMetrics.Add(new MonitoringMetric
        {
            ServerId = serverId, Name = "cpu.percent", ResourceRef = containerName, Value = value, Timestamp = at
        });

    [Fact]
    public async Task A_standing_threshold_breach_repeats_ten_minutes_later_once_the_window_is_configured_down()
    {
        var workspaceId = Guid.CreateVersion7();
        var h = NewHarness(new MonitoringOptions { ThresholdRepeatAfterHours = 5.0 / 60 });
        var (app, deployment) = SeedThresholdApp(h.Db, workspaceId, h.Server.Id);
        h.Db.Alerts.Add(new Alert
        {
            WorkspaceId = workspaceId, Name = "cpu", Channel = AlertChannel.Webhook, EncryptedTarget = "x",
            IsEnabled = true, AppId = app.Id, Metric = AlertMetric.CpuPercent, ThresholdPercent = 90, SustainedMinutes = 0
        });
        h.Db.SaveChanges();
        var containerName = Harbora.Infrastructure.Deployments.DeploymentPlanning.ContainerName(workspaceId, app.Slug, deployment.Number);

        SeedCpuSample(h.Db, h.Server.Id, containerName, 95, Now);
        h.Db.SaveChanges();
        await h.Collector.CollectAsync(default);

        h.Clock.UtcNow = Now.AddMinutes(10);
        SeedCpuSample(h.Db, h.Server.Id, containerName, 96, h.Clock.UtcNow);
        h.Db.SaveChanges();
        await h.Collector.CollectAsync(default);

        h.Notifications.Notifications.Count(n => n.Event == AlertEvent.ThresholdBreached).Should().Be(2,
            "ten minutes is past the configured five-minute repeat window");
    }

    [Fact]
    public async Task The_shipped_default_repeat_window_suppresses_the_same_breach_ten_minutes_later()
    {
        var workspaceId = Guid.CreateVersion7();
        var h = NewHarness(); // default: one hour
        var (app, deployment) = SeedThresholdApp(h.Db, workspaceId, h.Server.Id);
        h.Db.Alerts.Add(new Alert
        {
            WorkspaceId = workspaceId, Name = "cpu", Channel = AlertChannel.Webhook, EncryptedTarget = "x",
            IsEnabled = true, AppId = app.Id, Metric = AlertMetric.CpuPercent, ThresholdPercent = 90, SustainedMinutes = 0
        });
        h.Db.SaveChanges();
        var containerName = Harbora.Infrastructure.Deployments.DeploymentPlanning.ContainerName(workspaceId, app.Slug, deployment.Number);

        SeedCpuSample(h.Db, h.Server.Id, containerName, 95, Now);
        h.Db.SaveChanges();
        await h.Collector.CollectAsync(default);

        h.Clock.UtcNow = Now.AddMinutes(10);
        SeedCpuSample(h.Db, h.Server.Id, containerName, 96, h.Clock.UtcNow);
        h.Db.SaveChanges();
        await h.Collector.CollectAsync(default);

        h.Notifications.Notifications.Count(n => n.Event == AlertEvent.ThresholdBreached).Should().Be(1,
            "ten minutes has not passed the shipped hour");
    }
}
