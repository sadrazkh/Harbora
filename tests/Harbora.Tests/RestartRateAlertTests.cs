using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Monitoring;
using Harbora.Domain.Servers;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Monitoring;
using Harbora.Infrastructure.Tenancy;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The restart-rate rule: "tell me when this app restarts more than N times in the last M minutes."
///
/// Unlike <see cref="AlertMetric.CpuPercent"/>/<see cref="AlertMetric.MemoryPercent"/>, this is not a
/// value held above a line for a sustained window — it is a total accumulated across one, so
/// <see cref="ThresholdRule.Breached"/> does not govern it. <c>MetricsCollector.EvaluateThresholdsAsync</c>
/// sums the raw <c>app.restarts</c> deltas in the rule's own window instead.
/// </summary>
public class RestartRateAlertTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private sealed record Harness(
        MetricsCollector Collector, HarboraDbContext Db, RecordingNotificationService Notifications,
        FixedClock Clock, Server Server);

    private static Harness NewHarness()
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("restart-rate-" + Guid.NewGuid()).Options);
        var engine = new FakeDockerEngine();
        var factory = new FakeServerEngineFactory(engine);
        var notifications = new RecordingNotificationService();
        var dedup = new AlertDedup(db);
        var clock = new FixedClock(Now);
        var rollups = new MetricsRollupService(db, clock, NullLogger<MetricsRollupService>.Instance);

        var server = new Server { Name = "node-1", IsLocal = true };
        db.Servers.Add(server);
        db.SaveChanges();

        var quota = new QuotaService(db, Options.Create(new BillingOptions()));
        var collector = new MetricsCollector(
            db, factory, notifications, new RecordingEventPublisher(), new IncidentService(db), dedup, clock, rollups,
            Options.Create(new MonitoringOptions()), quota, NullLogger<MetricsCollector>.Instance);

        return new Harness(collector, db, notifications, clock, server);
    }

    private static (App App, Deployment Deployment) SeedApp(HarboraDbContext db, Guid workspaceId, Guid serverId)
    {
        var app = new App
        {
            WorkspaceId = workspaceId, ServerId = serverId, Name = "api",
            Slug = "api-" + Guid.NewGuid().ToString("N")[..8], CpuLimit = 1.0, Status = AppStatus.Running
        };
        db.Apps.Add(app);
        db.SaveChanges();

        var deployment = new Deployment { AppId = app.Id, WorkspaceId = workspaceId, Number = 1, Status = DeploymentStatus.Succeeded };
        db.Deployments.Add(deployment);
        app.ActiveDeploymentId = deployment.Id;
        db.SaveChanges();

        return (app, deployment);
    }

    private static void SeedRestartDelta(HarboraDbContext db, Guid serverId, string containerName, double value, DateTimeOffset at) =>
        db.MonitoringMetrics.Add(new MonitoringMetric
        { ServerId = serverId, Name = "app.restarts", ResourceRef = containerName, Value = value, Timestamp = at });

    [Fact]
    public async Task Three_restarts_in_ten_minutes_fires_a_rule_watching_for_two()
    {
        var workspaceId = Guid.CreateVersion7();
        var h = NewHarness();
        var (app, deployment) = SeedApp(h.Db, workspaceId, h.Server.Id);
        h.Db.Alerts.Add(new Alert
        {
            WorkspaceId = workspaceId, Name = "flapping", Channel = AlertChannel.Webhook, EncryptedTarget = "x",
            IsEnabled = true, AppId = app.Id, Metric = AlertMetric.RestartRate, ThresholdPercent = 2, SustainedMinutes = 10
        });
        var containerName = Harbora.Infrastructure.Deployments.DeploymentPlanning.ContainerName(workspaceId, app.Slug, deployment.Number);
        SeedRestartDelta(h.Db, h.Server.Id, containerName, 1, Now.AddMinutes(-8));
        SeedRestartDelta(h.Db, h.Server.Id, containerName, 1, Now.AddMinutes(-5));
        SeedRestartDelta(h.Db, h.Server.Id, containerName, 1, Now.AddMinutes(-1));
        h.Db.SaveChanges();

        await h.Collector.CollectAsync(default);

        h.Notifications.Notifications.Should().Contain(n => n.Event == AlertEvent.ThresholdBreached);
    }

    [Fact]
    public async Task Two_restarts_in_ten_minutes_does_not_breach_a_rule_watching_for_more_than_two()
    {
        var workspaceId = Guid.CreateVersion7();
        var h = NewHarness();
        var (app, deployment) = SeedApp(h.Db, workspaceId, h.Server.Id);
        h.Db.Alerts.Add(new Alert
        {
            WorkspaceId = workspaceId, Name = "flapping", Channel = AlertChannel.Webhook, EncryptedTarget = "x",
            IsEnabled = true, AppId = app.Id, Metric = AlertMetric.RestartRate, ThresholdPercent = 3, SustainedMinutes = 10
        });
        var containerName = Harbora.Infrastructure.Deployments.DeploymentPlanning.ContainerName(workspaceId, app.Slug, deployment.Number);
        SeedRestartDelta(h.Db, h.Server.Id, containerName, 1, Now.AddMinutes(-8));
        SeedRestartDelta(h.Db, h.Server.Id, containerName, 1, Now.AddMinutes(-5));
        h.Db.SaveChanges();

        await h.Collector.CollectAsync(default);

        h.Notifications.Notifications.Should().NotContain(n => n.Event == AlertEvent.ThresholdBreached);
    }

    [Fact]
    public async Task A_restart_outside_the_rules_own_window_does_not_count_towards_it()
    {
        var workspaceId = Guid.CreateVersion7();
        var h = NewHarness();
        var (app, deployment) = SeedApp(h.Db, workspaceId, h.Server.Id);
        h.Db.Alerts.Add(new Alert
        {
            WorkspaceId = workspaceId, Name = "flapping", Channel = AlertChannel.Webhook, EncryptedTarget = "x",
            IsEnabled = true, AppId = app.Id, Metric = AlertMetric.RestartRate, ThresholdPercent = 1, SustainedMinutes = 5
        });
        // A second, unrelated rule with a much wider window than the first — purely so the collector's
        // single shared read (the widest window any rule asks for) pulls the older restart into `raw`
        // at all. Without this, the outer read alone would already exclude it, and the test would pass
        // for the wrong reason: it would never actually exercise the restart-rate rule's own five-
        // minute filter.
        h.Db.Alerts.Add(new Alert
        {
            WorkspaceId = workspaceId, Name = "cpu", Channel = AlertChannel.Webhook, EncryptedTarget = "x",
            IsEnabled = true, AppId = app.Id, Metric = AlertMetric.CpuPercent, ThresholdPercent = 90, SustainedMinutes = 60
        });
        var containerName = Harbora.Infrastructure.Deployments.DeploymentPlanning.ContainerName(workspaceId, app.Slug, deployment.Number);
        SeedRestartDelta(h.Db, h.Server.Id, containerName, 1, Now.AddMinutes(-20));
        h.Db.SaveChanges();

        await h.Collector.CollectAsync(default);

        h.Notifications.Notifications.Should().NotContain(n => n.Event == AlertEvent.ThresholdBreached);
    }
}
