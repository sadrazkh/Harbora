using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Monitoring;
using Harbora.Domain.Tenancy;
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
/// C1 (2026-08-27 "warn before the refusal"), end to end through <see cref="MetricsCollector.CollectAsync"/>:
/// a workspace close to a plan cap gets an <see cref="AlertEvent.QuotaWarning"/> notification and an
/// open <see cref="AlertIncident"/>, driven by the exact <see cref="IQuotaService.GetUsageAsync"/>
/// figures a refusal would read — never a second computation.
/// </summary>
public class QuotaWarningAlertTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
    private const long Gb = 1024L * 1024 * 1024;

    private sealed record Harness(
        MetricsCollector Collector, HarboraDbContext Db, RecordingNotificationService Notifications,
        FixedClock Clock);

    private static Harness NewHarness(MonitoringOptions? options = null)
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("quota-warning-" + Guid.NewGuid()).Options);
        var factory = new FakeServerEngineFactory(new FakeDockerEngine());
        var notifications = new RecordingNotificationService();
        var dedup = new AlertDedup(db);
        var clock = new FixedClock(Now);
        var rollups = new MetricsRollupService(db, clock, NullLogger<MetricsRollupService>.Instance);
        var quota = new QuotaService(db, Options.Create(new BillingOptions { Enabled = true }));

        var collector = new MetricsCollector(
            db, factory, notifications, new RecordingEventPublisher(), new IncidentService(db), dedup, clock,
            rollups, Options.Create(options ?? new MonitoringOptions()), quota, NullLogger<MetricsCollector>.Instance);

        return new Harness(collector, db, notifications, clock);
    }

    private static Guid GivenPlan(HarboraDbContext db, int maxApps = 0, bool isDefault = false)
    {
        var planId = Guid.CreateVersion7();
        db.Plans.Add(new Plan { Id = planId, Name = "Test", MaxApps = maxApps, IsEnabled = true, IsDefault = isDefault });
        db.SaveChanges();
        return planId;
    }

    private static Guid GivenWorkspace(HarboraDbContext db, Guid? planId) =>
        GivenWorkspaceInternal(db, planId);

    private static Guid GivenWorkspaceInternal(HarboraDbContext db, Guid? planId)
    {
        var id = Guid.CreateVersion7();
        db.Workspaces.Add(new Workspace { Id = id, Name = "acme", Slug = "acme-" + id.ToString("N")[..8], PlanId = planId });
        db.SaveChanges();
        return id;
    }

    private static void GivenApps(HarboraDbContext db, Guid workspaceId, int count)
    {
        for (var i = 0; i < count; i++)
            db.Apps.Add(new App { WorkspaceId = workspaceId, Name = $"app-{i}", Slug = $"app-{workspaceId:N}-{i}" });
        db.SaveChanges();
    }

    private static void GivenQuotaAlert(HarboraDbContext db, Guid workspaceId, bool onQuotaWarning = true) =>
        db.Alerts.Add(new Alert
        {
            WorkspaceId = workspaceId, Name = "quota", Channel = AlertChannel.Webhook, EncryptedTarget = "x",
            IsEnabled = true, OnQuotaWarning = onQuotaWarning
        });

    [Fact]
    public async Task A_workspace_at_90_percent_of_its_app_cap_gets_a_quota_warning_and_an_open_incident()
    {
        var h = NewHarness();
        var planId = GivenPlan(h.Db, maxApps: 10);
        var workspaceId = GivenWorkspace(h.Db, planId);
        GivenApps(h.Db, workspaceId, 9);
        GivenQuotaAlert(h.Db, workspaceId);
        h.Db.SaveChanges();

        await h.Collector.CollectAsync(default);

        h.Notifications.Notifications.Should().Contain(n => n.Event == AlertEvent.QuotaWarning);
        h.Db.AlertIncidents.IgnoreQueryFilters()
            .Should().ContainSingle(i => i.WorkspaceId == workspaceId && i.Condition == AlertEvent.QuotaWarning
                && i.ClosedAt == null);
    }

    [Fact]
    public async Task A_workspace_comfortably_under_every_cap_gets_no_warning()
    {
        var h = NewHarness();
        var planId = GivenPlan(h.Db, maxApps: 10);
        var workspaceId = GivenWorkspace(h.Db, planId);
        GivenApps(h.Db, workspaceId, 3); // 30%
        GivenQuotaAlert(h.Db, workspaceId);
        h.Db.SaveChanges();

        await h.Collector.CollectAsync(default);

        h.Notifications.Notifications.Should().NotContain(n => n.Event == AlertEvent.QuotaWarning);
        h.Db.AlertIncidents.IgnoreQueryFilters().Should().BeEmpty();
    }

    [Fact]
    public async Task A_workspace_with_no_plan_at_all_is_never_warned_however_many_apps_it_has()
    {
        // No PlanId, and GivenPlan(isDefault: true) is never called — QuotaService.GetUsageAsync falls
        // back to a default plan that does not exist here, so every Max* reads as 0 (unlimited).
        var h = NewHarness();
        var workspaceId = GivenWorkspace(h.Db, planId: null);
        GivenApps(h.Db, workspaceId, 50);
        GivenQuotaAlert(h.Db, workspaceId);
        h.Db.SaveChanges();

        await h.Collector.CollectAsync(default);

        h.Notifications.Notifications.Should().NotContain(n => n.Event == AlertEvent.QuotaWarning,
            "a workspace with no cap at all must not be warned about a limit it does not have");
    }

    [Fact]
    public async Task A_workspace_whose_alert_does_not_opt_in_gets_nothing_even_when_over_the_line()
    {
        var h = NewHarness();
        var planId = GivenPlan(h.Db, maxApps: 10);
        var workspaceId = GivenWorkspace(h.Db, planId);
        GivenApps(h.Db, workspaceId, 9);
        GivenQuotaAlert(h.Db, workspaceId, onQuotaWarning: false);
        h.Db.SaveChanges();

        await h.Collector.CollectAsync(default);

        h.Notifications.Notifications.Should().NotContain(n => n.Event == AlertEvent.QuotaWarning);
        h.Db.AlertIncidents.IgnoreQueryFilters().Should().BeEmpty();
    }

    [Fact]
    public async Task Usage_dropping_back_under_the_line_resolves_the_open_incident()
    {
        var h = NewHarness();
        var planId = GivenPlan(h.Db, maxApps: 10);
        var workspaceId = GivenWorkspace(h.Db, planId);
        GivenApps(h.Db, workspaceId, 9); // 90%
        GivenQuotaAlert(h.Db, workspaceId);
        h.Db.SaveChanges();
        await h.Collector.CollectAsync(default);
        h.Db.AlertIncidents.IgnoreQueryFilters().Single(i => i.Condition == AlertEvent.QuotaWarning)
            .ClosedAt.Should().BeNull();

        // The workspace deletes seven apps — usage drops to 20%, well under the line.
        foreach (var app in h.Db.Apps.Where(a => a.WorkspaceId == workspaceId).Take(7).ToList())
            h.Db.Apps.Remove(app);
        h.Db.SaveChanges();
        h.Clock.UtcNow = Now.AddMinutes(1);

        await h.Collector.CollectAsync(default);

        h.Db.AlertIncidents.IgnoreQueryFilters().Single(i => i.Condition == AlertEvent.QuotaWarning)
            .ClosedAt.Should().NotBeNull("the condition cleared, which is a resolve, not an acknowledgement");
    }

    [Fact]
    public async Task A_second_tick_ten_minutes_later_does_not_repeat_the_notification_inside_the_shipped_hour()
    {
        var h = NewHarness(); // default QuotaAlertIntervalHours: 1
        var planId = GivenPlan(h.Db, maxApps: 10);
        var workspaceId = GivenWorkspace(h.Db, planId);
        GivenApps(h.Db, workspaceId, 9);
        GivenQuotaAlert(h.Db, workspaceId);
        h.Db.SaveChanges();

        await h.Collector.CollectAsync(default);
        h.Clock.UtcNow = Now.AddMinutes(10);
        await h.Collector.CollectAsync(default);

        h.Notifications.Notifications.Count(n => n.Event == AlertEvent.QuotaWarning).Should().Be(1,
            "a workspace sitting above the line does not need the same fact every collector tick");
        // The incident itself stays open and current even while the channel is throttled — the
        // interval governs how often a person is pinged, not how long the condition is tracked as open.
        h.Db.AlertIncidents.IgnoreQueryFilters().Single(i => i.Condition == AlertEvent.QuotaWarning)
            .ClosedAt.Should().BeNull();
    }

    [Fact]
    public async Task Configuring_a_five_minute_interval_lets_a_second_warning_fire_ten_minutes_later()
    {
        var h = NewHarness(new MonitoringOptions { QuotaAlertIntervalHours = 5.0 / 60 });
        var planId = GivenPlan(h.Db, maxApps: 10);
        var workspaceId = GivenWorkspace(h.Db, planId);
        GivenApps(h.Db, workspaceId, 9);
        GivenQuotaAlert(h.Db, workspaceId);
        h.Db.SaveChanges();

        await h.Collector.CollectAsync(default);
        h.Clock.UtcNow = Now.AddMinutes(10);
        await h.Collector.CollectAsync(default);

        h.Notifications.Notifications.Count(n => n.Event == AlertEvent.QuotaWarning).Should().Be(2,
            "ten minutes is past the configured five-minute interval");
    }

    [Fact]
    public async Task The_warning_names_the_same_used_and_max_figures_the_refusal_itself_would_read()
    {
        var h = NewHarness();
        var planId = GivenPlan(h.Db, maxApps: 10);
        var workspaceId = GivenWorkspace(h.Db, planId);
        GivenApps(h.Db, workspaceId, 9);
        GivenQuotaAlert(h.Db, workspaceId);
        h.Db.SaveChanges();
        var quota = new QuotaService(h.Db, Options.Create(new BillingOptions { Enabled = true }));

        await h.Collector.CollectAsync(default);

        // The exact figure IQuotaService.GetUsageAsync reports right now — read independently here,
        // the same way a refusal would read it, to prove the notification did not invent a second one.
        var usage = await quota.GetUsageAsync(workspaceId, default);
        usage.Apps.Should().Be(9);
        usage.MaxApps.Should().Be(10);

        var sent = h.Notifications.Notifications.Single(n => n.Event == AlertEvent.QuotaWarning);
        sent.Data.Get("Summary").Should().Contain("9/10").And.Contain("90%");
    }
}
