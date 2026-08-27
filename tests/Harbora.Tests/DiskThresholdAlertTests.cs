using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Monitoring;
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
/// C2 (2026-08-27 "the outage nobody sees coming"): <see cref="AlertMetric.DiskPercent"/> — an app's
/// own volumes against the size limit set on them.
///
/// Unlike <see cref="AlertMetric.CpuPercent"/>/<see cref="AlertMetric.MemoryPercent"/>,
/// <c>MetricsCollector.EvaluateDiskThresholdsAsync</c> reads <c>Volume.StorageBytes</c> directly
/// rather than a live <c>MonitoringMetrics</c> sample, so these tests never seed a server, a
/// deployment or a container name.
/// </summary>
public class DiskThresholdAlertTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
    private const long Gb = 1024L * 1024 * 1024;

    private sealed record Harness(MetricsCollector Collector, HarboraDbContext Db, RecordingNotificationService Notifications, FixedClock Clock);

    private static Harness NewHarness()
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("disk-threshold-" + Guid.NewGuid()).Options);
        var factory = new FakeServerEngineFactory(new FakeDockerEngine());
        var notifications = new RecordingNotificationService();
        var dedup = new AlertDedup(db);
        var clock = new FixedClock(Now);
        var rollups = new MetricsRollupService(db, clock, NullLogger<MetricsRollupService>.Instance);
        var quota = new QuotaService(db, Options.Create(new BillingOptions()));

        var collector = new MetricsCollector(
            db, factory, notifications, new RecordingEventPublisher(), new IncidentService(db), dedup, clock,
            rollups, Options.Create(new MonitoringOptions()), quota, NullLogger<MetricsCollector>.Instance);

        return new Harness(collector, db, notifications, clock);
    }

    private static App GivenApp(HarboraDbContext db, Guid workspaceId)
    {
        // Deliberately no ServerId, no Deployment, no ActiveDeploymentId — proving DiskPercent does
        // not require a running container the way CpuPercent/MemoryPercent do.
        var app = new App { WorkspaceId = workspaceId, Name = "api", Slug = "api-" + Guid.NewGuid().ToString("N")[..8] };
        db.Apps.Add(app);
        db.SaveChanges();
        return app;
    }

    private static void GivenVolume(HarboraDbContext db, Guid appId, long? sizeLimitBytes, long? storageBytes) =>
        db.Volumes.Add(new Volume
        {
            AppId = appId, Name = "v-" + Guid.NewGuid().ToString("N")[..6], MountPath = "/data",
            SizeLimitBytes = sizeLimitBytes, StorageBytes = storageBytes
        });

    private static void GivenDiskRule(HarboraDbContext db, Guid workspaceId, Guid appId, double thresholdPercent) =>
        db.Alerts.Add(new Alert
        {
            WorkspaceId = workspaceId, Name = "disk", Channel = AlertChannel.Webhook, EncryptedTarget = "x",
            IsEnabled = true, AppId = appId, Metric = AlertMetric.DiskPercent, ThresholdPercent = thresholdPercent, SustainedMinutes = 5
        });

    [Fact]
    public async Task A_volume_at_90_percent_fires_a_rule_watching_for_80()
    {
        var h = NewHarness();
        var workspaceId = Guid.CreateVersion7();
        var app = GivenApp(h.Db, workspaceId);
        GivenVolume(h.Db, app.Id, sizeLimitBytes: 10 * Gb, storageBytes: 9 * Gb);
        GivenDiskRule(h.Db, workspaceId, app.Id, thresholdPercent: 80);
        h.Db.SaveChanges();

        await h.Collector.CollectAsync(default);

        h.Notifications.Notifications.Should().Contain(n =>
            n.Event == AlertEvent.ThresholdBreached && n.Data.Get("Metric") == "DiskPercent");
    }

    [Fact]
    public async Task A_volume_comfortably_under_the_threshold_does_not_fire()
    {
        var h = NewHarness();
        var workspaceId = Guid.CreateVersion7();
        var app = GivenApp(h.Db, workspaceId);
        GivenVolume(h.Db, app.Id, sizeLimitBytes: 10 * Gb, storageBytes: 5 * Gb); // 50%
        GivenDiskRule(h.Db, workspaceId, app.Id, thresholdPercent: 80);
        h.Db.SaveChanges();

        await h.Collector.CollectAsync(default);

        h.Notifications.Notifications.Should().NotContain(n => n.Data.Get("Metric") == "DiskPercent");
    }

    [Fact]
    public async Task An_app_with_no_capped_volume_is_skipped_however_much_it_has_stored()
    {
        var h = NewHarness();
        var workspaceId = Guid.CreateVersion7();
        var app = GivenApp(h.Db, workspaceId);
        GivenVolume(h.Db, app.Id, sizeLimitBytes: null, storageBytes: 500 * Gb); // no ceiling set
        GivenDiskRule(h.Db, workspaceId, app.Id, thresholdPercent: 80);
        h.Db.SaveChanges();

        await h.Collector.CollectAsync(default);

        h.Notifications.Notifications.Should().NotContain(n => n.Data.Get("Metric") == "DiskPercent",
            "an uncapped volume has no ceiling for its bytes to be a share of");
    }

    [Fact]
    public async Task An_unmeasured_capped_volume_is_skipped_rather_than_treated_as_zero()
    {
        var h = NewHarness();
        var workspaceId = Guid.CreateVersion7();
        var app = GivenApp(h.Db, workspaceId);
        GivenVolume(h.Db, app.Id, sizeLimitBytes: 10 * Gb, storageBytes: null); // never measured yet
        GivenDiskRule(h.Db, workspaceId, app.Id, thresholdPercent: 80);
        h.Db.SaveChanges();

        await h.Collector.CollectAsync(default);

        h.Notifications.Notifications.Should().NotContain(n => n.Data.Get("Metric") == "DiskPercent",
            "a capped volume nothing has measured yet is silence, not a false 0%");
    }

    [Fact]
    public async Task Two_volumes_on_the_same_app_are_summed_before_the_percentage_is_taken()
    {
        var h = NewHarness();
        var workspaceId = Guid.CreateVersion7();
        var app = GivenApp(h.Db, workspaceId);
        GivenVolume(h.Db, app.Id, sizeLimitBytes: 10 * Gb, storageBytes: 4 * Gb);
        GivenVolume(h.Db, app.Id, sizeLimitBytes: 10 * Gb, storageBytes: 5 * Gb); // combined: 9/20 = 45%
        GivenDiskRule(h.Db, workspaceId, app.Id, thresholdPercent: 40);
        h.Db.SaveChanges();

        await h.Collector.CollectAsync(default);

        h.Notifications.Notifications.Should().Contain(n => n.Data.Get("Metric") == "DiskPercent");
    }

    [Fact]
    public async Task An_app_with_no_active_deployment_is_still_evaluated()
    {
        // The defining difference from CpuPercent/MemoryPercent: EvaluateThresholdsAsync skips an app
        // with no container name outright, but a volume's bytes sit on disk whether or not the app is
        // between deployments right now.
        var h = NewHarness();
        var workspaceId = Guid.CreateVersion7();
        var app = GivenApp(h.Db, workspaceId); // ActiveDeploymentId is null
        app.ActiveDeploymentId.Should().BeNull();
        GivenVolume(h.Db, app.Id, sizeLimitBytes: 10 * Gb, storageBytes: 9 * Gb);
        GivenDiskRule(h.Db, workspaceId, app.Id, thresholdPercent: 80);
        h.Db.SaveChanges();

        await h.Collector.CollectAsync(default);

        h.Notifications.Notifications.Should().Contain(n => n.Data.Get("Metric") == "DiskPercent");
    }

    [Fact]
    public async Task Usage_dropping_back_under_the_line_resolves_the_open_incident()
    {
        var h = NewHarness();
        var workspaceId = Guid.CreateVersion7();
        var app = GivenApp(h.Db, workspaceId);
        var volume = new Volume
        {
            AppId = app.Id, Name = "v", MountPath = "/data", SizeLimitBytes = 10 * Gb, StorageBytes = 9 * Gb
        };
        h.Db.Volumes.Add(volume);
        GivenDiskRule(h.Db, workspaceId, app.Id, thresholdPercent: 80);
        h.Db.SaveChanges();
        await h.Collector.CollectAsync(default);
        h.Db.AlertIncidents.IgnoreQueryFilters().Single(i => i.Condition == AlertEvent.ThresholdBreached)
            .ClosedAt.Should().BeNull();

        volume.StorageBytes = 2 * Gb; // dropped to 20%
        h.Db.SaveChanges();
        h.Clock.UtcNow = Now.AddMinutes(1);

        await h.Collector.CollectAsync(default);

        h.Db.AlertIncidents.IgnoreQueryFilters().Single(i => i.Condition == AlertEvent.ThresholdBreached)
            .ClosedAt.Should().NotBeNull();
    }
}
