using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Monitoring;
using Harbora.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Editing and enabling/disabling an alert rule, and refusing a threshold that is only half typed in.
///
/// Before this, a rule could only be created or deleted, <c>IsEnabled</c> was hardcoded <c>true</c> in
/// the constructor call at <c>AlertsController.cs:67</c>, and a threshold with a metric but no value
/// (or an app but no metric, or a value but no app) was silently stored as a plain event rule —
/// the save "succeeded", the redirect fired, and the rule watched nothing. That is the defect this
/// file exists to close.
/// </summary>
public class AlertManagementTests
{
    private static readonly Guid Workspace = Guid.CreateVersion7();

    private sealed class StubUser : ICurrentUser
    {
        public Guid? UserId { get; init; } = Guid.CreateVersion7();
        public string? Email => "ops@example.com";
        public bool IsAuthenticated => true;
        public Guid? WorkspaceId { get; init; } = Workspace;
    }

    private sealed class StubNotifications : INotificationService
    {
        public Task<int> NotifyAsync(Guid workspaceId, Harbora.Domain.Notifications.NotificationEventData evt, AlertSeverity severity, CancellationToken ct)
            => Task.FromResult(0);

        public Task<NotificationResult> NotifyRuleAsync(Guid alertId, Harbora.Domain.Notifications.NotificationEventData evt, AlertSeverity severity, CancellationToken ct)
            => Task.FromResult(NotificationResult.Ok);

        public Task<NotificationResult> SendTestAsync(Guid alertId, CancellationToken ct)
            => Task.FromResult(NotificationResult.Ok);

        public Task ExecuteQueuedDeliveryAsync(Guid deliveryId, CancellationToken ct)
            => Task.CompletedTask;
    }

    private static (AlertsController Controller, HarboraDbContext Db) Build(Guid? userWorkspace = null)
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("alert-mgmt-" + Guid.NewGuid()).Options);

        var controller = new AlertsController(
            db, new StubNotifications(), new Fakes.PassthroughProtector(), new StubUser { WorkspaceId = userWorkspace ?? Workspace })
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider())
        };
        return (controller, db);
    }

    // ---- the defining defect: a half-filled threshold is refused, not swallowed -------------

    [Fact]
    public async Task A_rule_saved_with_a_metric_and_no_threshold_value_is_refused_and_names_the_missing_field()
    {
        var (controller, db) = Build();
        var appId = Guid.CreateVersion7();

        await controller.Create(
            name: "cpu watch", channel: AlertChannel.Webhook, minSeverity: AlertSeverity.Warning,
            webhookUrl: "https://hooks.example/x", telegramToken: null, telegramChatId: null,
            smtpHost: null, smtpPort: 0, smtpUser: null, smtpPassword: null, emailFrom: null, emailTo: null,
            onDeployFailed: true, onAppCrashed: true, onSslExpiring: false, onDiskWarning: true, onBackupFailed: true,
            appId: appId, metric: AlertMetric.CpuPercent, thresholdPercent: null, sustainedMinutes: 5,
            ct: default);

        db.Alerts.Should().BeEmpty("the incomplete triple must not be stored as an inert event rule");
        controller.TempData["Error"].Should().BeOfType<string>()
            .Which.Should().Contain("threshold value", "the refusal must name the field that is actually missing");
    }

    [Fact]
    public async Task A_rule_with_an_app_but_no_metric_or_value_is_refused_the_same_way()
    {
        var (controller, db) = Build();

        await controller.Create(
            name: "half", channel: AlertChannel.Webhook, minSeverity: AlertSeverity.Warning,
            webhookUrl: "https://hooks.example/x", telegramToken: null, telegramChatId: null,
            smtpHost: null, smtpPort: 0, smtpUser: null, smtpPassword: null, emailFrom: null, emailTo: null,
            onDeployFailed: true, onAppCrashed: true, onSslExpiring: false, onDiskWarning: true, onBackupFailed: true,
            appId: Guid.CreateVersion7(), metric: null, thresholdPercent: null, sustainedMinutes: 5,
            ct: default);

        db.Alerts.Should().BeEmpty();
        controller.TempData["Error"].Should().BeOfType<string>()
            .Which.Should().Contain("metric").And.Contain("threshold value");
    }

    [Fact]
    public async Task A_plain_event_rule_with_no_threshold_fields_at_all_is_saved_without_complaint()
    {
        var (controller, db) = Build();

        await controller.Create(
            name: "event only", channel: AlertChannel.Webhook, minSeverity: AlertSeverity.Warning,
            webhookUrl: "https://hooks.example/x", telegramToken: null, telegramChatId: null,
            smtpHost: null, smtpPort: 0, smtpUser: null, smtpPassword: null, emailFrom: null, emailTo: null,
            onDeployFailed: true, onAppCrashed: true, onSslExpiring: false, onDiskWarning: true, onBackupFailed: true,
            appId: null, metric: null, thresholdPercent: null, sustainedMinutes: 5,
            ct: default);

        db.Alerts.Should().ContainSingle();
        controller.TempData.Should().NotContainKey("Error");
    }

    [Fact]
    public async Task A_fully_specified_threshold_is_saved_and_watches_the_app()
    {
        var (controller, db) = Build();
        var appId = Guid.CreateVersion7();

        await controller.Create(
            name: "cpu watch", channel: AlertChannel.Webhook, minSeverity: AlertSeverity.Warning,
            webhookUrl: "https://hooks.example/x", telegramToken: null, telegramChatId: null,
            smtpHost: null, smtpPort: 0, smtpUser: null, smtpPassword: null, emailFrom: null, emailTo: null,
            onDeployFailed: true, onAppCrashed: true, onSslExpiring: false, onDiskWarning: true, onBackupFailed: true,
            appId: appId, metric: AlertMetric.CpuPercent, thresholdPercent: 85, sustainedMinutes: 10,
            ct: default);

        var saved = db.Alerts.Should().ContainSingle().Subject;
        saved.AppId.Should().Be(appId);
        saved.Metric.Should().Be(AlertMetric.CpuPercent);
        saved.ThresholdPercent.Should().Be(85);
        saved.SustainedMinutes.Should().Be(10);
        controller.TempData.Should().NotContainKey("Error");
    }

    // ---- toggle: the field already exists, nothing could write it ---------------------------

    [Fact]
    public async Task Toggling_an_enabled_rule_disables_it_and_it_stays_in_the_list()
    {
        var (controller, db) = Build();
        var rule = SeedRule(db, Workspace);
        rule.IsEnabled.Should().BeTrue();

        await controller.Toggle(rule.Id, default);

        db.Alerts.Single(a => a.Id == rule.Id).IsEnabled.Should().BeFalse();
        db.Alerts.Should().ContainSingle("disabling is not deleting");
    }

    [Fact]
    public async Task Toggling_twice_re_enables_the_rule()
    {
        var (controller, db) = Build();
        var rule = SeedRule(db, Workspace);

        await controller.Toggle(rule.Id, default);
        await controller.Toggle(rule.Id, default);

        db.Alerts.Single(a => a.Id == rule.Id).IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Toggling_another_workspaces_rule_is_refused_and_changes_nothing()
    {
        var (controller, db) = Build();
        var otherWorkspace = Guid.CreateVersion7();
        var rule = SeedRule(db, otherWorkspace);

        var response = await controller.Toggle(rule.Id, default);

        response.Should().BeOfType<NotFoundResult>();
        db.Alerts.Single(a => a.Id == rule.Id).IsEnabled.Should().BeTrue("the rule belongs to someone else");
    }

    // ---- edit: name/severity/events change; the encrypted target is not required again ------

    [Fact]
    public async Task Editing_a_rules_severity_leaves_its_stored_target_byte_for_byte_unchanged()
    {
        var (controller, db) = Build();
        var rule = SeedRule(db, Workspace);
        var storedBefore = rule.EncryptedTarget;

        await controller.Edit(
            rule.Id, name: "renamed", channel: AlertChannel.Webhook, minSeverity: AlertSeverity.Critical,
            webhookUrl: null, telegramToken: null, telegramChatId: null,
            smtpHost: null, smtpPort: 0, smtpUser: null, smtpPassword: null, emailFrom: null, emailTo: null,
            onDeployFailed: true, onAppCrashed: true, onSslExpiring: true, onDiskWarning: true, onBackupFailed: true,
            appId: null, metric: null, thresholdPercent: null, sustainedMinutes: 5, ct: default);

        var saved = db.Alerts.Single(a => a.Id == rule.Id);
        saved.Name.Should().Be("renamed");
        saved.MinSeverity.Should().Be(AlertSeverity.Critical);
        saved.EncryptedTarget.Should().Be(storedBefore, "the target field was left blank, so nothing about it should change — not even its ciphertext");
    }

    [Fact]
    public async Task Editing_with_a_new_webhook_url_replaces_the_stored_target()
    {
        var (controller, db) = Build();
        var rule = SeedRule(db, Workspace);
        var protector = new Fakes.PassthroughProtector();

        await controller.Edit(
            rule.Id, name: rule.Name, channel: AlertChannel.Webhook, minSeverity: rule.MinSeverity,
            webhookUrl: "https://new.example/hook", telegramToken: null, telegramChatId: null,
            smtpHost: null, smtpPort: 0, smtpUser: null, smtpPassword: null, emailFrom: null, emailTo: null,
            onDeployFailed: true, onAppCrashed: true, onSslExpiring: true, onDiskWarning: true, onBackupFailed: true,
            appId: null, metric: null, thresholdPercent: null, sustainedMinutes: 5, ct: default);

        var saved = db.Alerts.Single(a => a.Id == rule.Id);
        protector.Unprotect(saved.EncryptedTarget).Should().Contain("https://new.example/hook");
    }

    [Fact]
    public async Task Editing_only_the_smtp_password_keeps_every_other_email_field_the_merge_is_per_field_not_whole_target()
    {
        var (controller, db) = Build();
        var protector = new Fakes.PassthroughProtector();
        var rule = SeedRule(db, Workspace, a =>
        {
            a.Channel = AlertChannel.Email;
            a.EncryptedTarget = protector.Protect(
                """{"host":"smtp.example.com","port":587,"user":"ops","password":"old-pass","from":"alerts@example.com","to":"ops@example.com","useSsl":true}""");
        });

        await controller.Edit(
            rule.Id, name: rule.Name, channel: AlertChannel.Email, minSeverity: rule.MinSeverity,
            webhookUrl: null, telegramToken: null, telegramChatId: null,
            smtpHost: null, smtpPort: 0, smtpUser: null, smtpPassword: "new-pass", emailFrom: null, emailTo: null,
            onDeployFailed: true, onAppCrashed: true, onSslExpiring: true, onDiskWarning: true, onBackupFailed: true,
            appId: null, metric: null, thresholdPercent: null, sustainedMinutes: 5, ct: default);

        var saved = db.Alerts.Single(a => a.Id == rule.Id);
        var target = System.Text.Json.JsonDocument.Parse(protector.Unprotect(saved.EncryptedTarget)).RootElement;
        target.GetProperty("password").GetString().Should().Be("new-pass", "the field that was typed changes");
        target.GetProperty("host").GetString().Should().Be("smtp.example.com", "fields left blank on the edit must survive");
        target.GetProperty("user").GetString().Should().Be("ops");
        target.GetProperty("from").GetString().Should().Be("alerts@example.com");
        target.GetProperty("to").GetString().Should().Be("ops@example.com");
        target.GetProperty("port").GetInt32().Should().Be(587);
    }

    [Fact]
    public async Task Switching_channel_without_a_new_target_is_refused()
    {
        var (controller, db) = Build();
        var rule = SeedRule(db, Workspace); // Webhook

        await controller.Edit(
            rule.Id, name: rule.Name, channel: AlertChannel.Telegram, minSeverity: rule.MinSeverity,
            webhookUrl: null, telegramToken: null, telegramChatId: null,
            smtpHost: null, smtpPort: 0, smtpUser: null, smtpPassword: null, emailFrom: null, emailTo: null,
            onDeployFailed: true, onAppCrashed: true, onSslExpiring: true, onDiskWarning: true, onBackupFailed: true,
            appId: null, metric: null, thresholdPercent: null, sustainedMinutes: 5, ct: default);

        var saved = db.Alerts.Single(a => a.Id == rule.Id);
        saved.Channel.Should().Be(AlertChannel.Webhook, "a channel switch with no new target must not be applied");
        controller.TempData["Error"].Should().BeOfType<string>();
    }

    [Fact]
    public async Task Switching_channel_with_a_new_target_is_applied()
    {
        var (controller, db) = Build();
        var rule = SeedRule(db, Workspace); // Webhook
        var protector = new Fakes.PassthroughProtector();

        await controller.Edit(
            rule.Id, name: rule.Name, channel: AlertChannel.Telegram, minSeverity: rule.MinSeverity,
            webhookUrl: null, telegramToken: "bot-token", telegramChatId: "12345",
            smtpHost: null, smtpPort: 0, smtpUser: null, smtpPassword: null, emailFrom: null, emailTo: null,
            onDeployFailed: true, onAppCrashed: true, onSslExpiring: true, onDiskWarning: true, onBackupFailed: true,
            appId: null, metric: null, thresholdPercent: null, sustainedMinutes: 5, ct: default);

        var saved = db.Alerts.Single(a => a.Id == rule.Id);
        saved.Channel.Should().Be(AlertChannel.Telegram);
        protector.Unprotect(saved.EncryptedTarget).Should().Contain("bot-token");
    }

    [Fact]
    public async Task Editing_a_rules_threshold_value_updates_the_stored_line()
    {
        var (controller, db) = Build();
        var appId = Guid.CreateVersion7();
        var rule = SeedRule(db, Workspace, a =>
        {
            a.AppId = appId; a.Metric = AlertMetric.MemoryPercent; a.ThresholdPercent = 90; a.SustainedMinutes = 5;
        });

        await controller.Edit(
            rule.Id, name: rule.Name, channel: AlertChannel.Webhook, minSeverity: rule.MinSeverity,
            webhookUrl: null, telegramToken: null, telegramChatId: null,
            smtpHost: null, smtpPort: 0, smtpUser: null, smtpPassword: null, emailFrom: null, emailTo: null,
            onDeployFailed: true, onAppCrashed: true, onSslExpiring: true, onDiskWarning: true, onBackupFailed: true,
            appId: appId, metric: AlertMetric.MemoryPercent, thresholdPercent: 70, sustainedMinutes: 15, ct: default);

        var saved = db.Alerts.Single(a => a.Id == rule.Id);
        saved.ThresholdPercent.Should().Be(70, "raising or lowering the line is exactly what edit exists for");
        saved.SustainedMinutes.Should().Be(15);
        controller.TempData.Should().NotContainKey("Error");
    }

    [Fact]
    public async Task Editing_a_rule_into_a_half_filled_threshold_is_refused_and_the_row_is_unchanged()
    {
        var (controller, db) = Build();
        var appId = Guid.CreateVersion7();
        var rule = SeedRule(db, Workspace, a =>
        {
            a.AppId = appId; a.Metric = AlertMetric.MemoryPercent; a.ThresholdPercent = 90;
        });

        await controller.Edit(
            rule.Id, name: rule.Name, channel: AlertChannel.Webhook, minSeverity: rule.MinSeverity,
            webhookUrl: null, telegramToken: null, telegramChatId: null,
            smtpHost: null, smtpPort: 0, smtpUser: null, smtpPassword: null, emailFrom: null, emailTo: null,
            onDeployFailed: true, onAppCrashed: true, onSslExpiring: true, onDiskWarning: true, onBackupFailed: true,
            appId: appId, metric: AlertMetric.MemoryPercent, thresholdPercent: null, sustainedMinutes: 5, ct: default);

        var saved = db.Alerts.Single(a => a.Id == rule.Id);
        saved.ThresholdPercent.Should().Be(90, "the refused edit must not overwrite the rule that was already there");
        controller.TempData["Error"].Should().BeOfType<string>()
            .Which.Should().Contain("threshold value");
    }

    [Fact]
    public async Task Editing_another_workspaces_rule_is_not_found_and_changes_nothing()
    {
        var (controller, db) = Build();
        var otherWorkspace = Guid.CreateVersion7();
        var rule = SeedRule(db, otherWorkspace);

        var response = await controller.Edit(
            rule.Id, name: "hijacked", channel: AlertChannel.Webhook, minSeverity: AlertSeverity.Critical,
            webhookUrl: null, telegramToken: null, telegramChatId: null,
            smtpHost: null, smtpPort: 0, smtpUser: null, smtpPassword: null, emailFrom: null, emailTo: null,
            onDeployFailed: true, onAppCrashed: true, onSslExpiring: true, onDiskWarning: true, onBackupFailed: true,
            appId: null, metric: null, thresholdPercent: null, sustainedMinutes: 5, ct: default);

        response.Should().BeOfType<NotFoundResult>();
        db.Alerts.Single(a => a.Id == rule.Id).Name.Should().NotBe("hijacked");
    }

    private static Alert SeedRule(HarboraDbContext db, Guid workspaceId, Action<Alert>? configure = null)
    {
        var protector = new Fakes.PassthroughProtector();
        var rule = new Alert
        {
            WorkspaceId = workspaceId,
            Name = "ops",
            Channel = AlertChannel.Webhook,
            MinSeverity = AlertSeverity.Warning,
            EncryptedTarget = protector.Protect("""{"url":"https://old.example/hook"}"""),
            IsEnabled = true
        };
        configure?.Invoke(rule);
        db.Alerts.Add(rule);
        db.SaveChanges();
        return rule;
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }
}
