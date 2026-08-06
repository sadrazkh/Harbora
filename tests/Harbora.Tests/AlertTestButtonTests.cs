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
/// What the "test" button on the alerts page reports.
///
/// It used to set <c>TempData["Message"] = "Test notification sent."</c> unconditionally — before the
/// delivery was judged, and even when the rule belonged to another workspace. A test that cannot fail
/// is worse than no test button: it is an assurance that the channel works, issued without looking.
/// </summary>
public class AlertTestButtonTests
{
    private static readonly Guid Workspace = Guid.CreateVersion7();

    private sealed class StubUser : ICurrentUser
    {
        public Guid? UserId { get; init; } = Guid.CreateVersion7();
        public string? Email => "ops@example.com";
        public bool IsAuthenticated => true;
        public Guid? WorkspaceId { get; init; } = Workspace;
    }

    private sealed class StubNotifications(NotificationResult result) : INotificationService
    {
        public int Sent;

        public Task NotifyAsync(Guid workspaceId, AlertEvent evt, AlertSeverity severity, string title, string body, CancellationToken ct)
            => Task.CompletedTask;

        public Task<NotificationResult> NotifyRuleAsync(Guid alertId, AlertSeverity severity, string title, string body, CancellationToken ct)
            => Task.FromResult(NotificationResult.Ok);

        public Task<NotificationResult> SendTestAsync(Guid alertId, CancellationToken ct)
        {
            Sent++;
            return Task.FromResult(result);
        }
    }

    private static (AlertsController Controller, Alert Rule, StubNotifications Notifications) Build(
        NotificationResult result, Guid? owner = null)
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("alert-test-" + Guid.NewGuid()).Options);

        var rule = new Alert
        {
            WorkspaceId = owner ?? Workspace, Name = "ops",
            Channel = AlertChannel.Webhook, EncryptedTarget = "{}"
        };
        db.Alerts.Add(rule);
        db.SaveChanges();

        var notifications = new StubNotifications(result);
        var controller = new AlertsController(db, notifications, new Fakes.PassthroughProtector(), new StubUser())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider())
        };
        return (controller, rule, notifications);
    }

    [Fact]
    public async Task A_delivered_test_reports_success()
    {
        var (controller, rule, _) = Build(NotificationResult.Ok);

        await controller.Test(rule.Id, default);

        controller.TempData["Message"].Should().BeOfType<string>().Which.Should().Contain("delivered");
        controller.TempData.Should().NotContainKey("Error");
    }

    [Fact]
    public async Task A_rejected_test_reports_the_reason_rather_than_success()
    {
        var (controller, rule, _) = Build(NotificationResult.Failed("The webhook returned 404 Not Found"));

        await controller.Test(rule.Id, default);

        controller.TempData.Should().NotContainKey("Message");
        controller.TempData["Error"].Should().BeOfType<string>()
            .Which.Should().Contain("404", "the point is to name what went wrong");
    }

    [Fact]
    public async Task Another_workspaces_alert_is_not_tested_at_all()
    {
        // It also used to print "sent" for a rule it had refused to touch.
        var (controller, rule, notifications) = Build(NotificationResult.Ok, owner: Guid.CreateVersion7());

        var response = await controller.Test(rule.Id, default);

        response.Should().BeOfType<NotFoundResult>();
        notifications.Sent.Should().Be(0);
        controller.TempData.Should().NotContainKey("Message");
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }
}
