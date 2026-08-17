using System.Net;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Monitoring;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The incident timeline on <c>/monitoring</c>, end to end: the real route, a real cookie, real Razor
/// (2026-08-16 monitoring-alerting spec §M4). Its own open-incident badge is where that count lives
/// now (N3, 2026-08-16 notification-system spec) — the topbar's bell counts this signed-in person's
/// unread notifications instead; see <c>NotificationsHttpTests</c> for that badge.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class MonitoringTimelineHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    [Fact]
    public async Task The_monitoring_page_shows_an_open_incident_and_a_closed_ones_reason_by_data_attribute()
    {
        var openId = Guid.CreateVersion7();
        var closedId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        Panel.Seed(db =>
        {
            db.AlertIncidents.Add(new AlertIncident
            {
                Id = openId, WorkspaceId = fixture.WorkspaceId, Condition = AlertEvent.DeployFailed,
                SubjectRef = "deployment-1", Severity = AlertSeverity.Critical,
                Title = "Deploy failed: api #4", Body = "build error",
                OpenedAt = now, LastObservedAt = now
            });
            db.AlertIncidents.Add(new AlertIncident
            {
                Id = closedId, WorkspaceId = fixture.WorkspaceId, Condition = AlertEvent.AppCrashed,
                SubjectRef = "app-1", Severity = AlertSeverity.Critical,
                Title = "App crashed: worker", Body = "exited unexpectedly",
                OpenedAt = now.AddHours(-1), LastObservedAt = now.AddHours(-1),
                ClosedAt = now, ClosedReason = IncidentClosedReason.Resolved
            });
        });
        Panel.GivenUser(fixture.WorkspaceId, "timeline-view@example.com", Harbora.Domain.Common.SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.240", "timeline-view@example.com");

        var html = await (await client.GetAsync("/monitoring")).Content.ReadAsStringAsync();

        html.Should().Contain($"data-incident-id=\"{openId}\"");
        html.Should().Contain("data-incident-open=\"true\"");
        html.Should().Contain($"data-incident-id=\"{closedId}\"");
        html.Should().Contain("data-incident-open=\"false\"");
        html.Should().Contain("data-incident-closed-reason=\"Resolved\"");
        html.Should().Contain("data-incident-condition=\"DeployFailed\"");
        html.Should().Contain("data-incident-condition=\"AppCrashed\"");

        // Persian is the panel's default in tests. "تأیید" (acknowledge) is the button on the open
        // incident's row; a closed incident has no such button.
        html.Should().Contain("&#x62A;&#x623;&#x6CC;&#x6CC;&#x62F;", "the Persian acknowledge button on the open incident's row");
    }

    /// <summary>
    /// N3 moved this badge from the topbar's bell — every workspace member's shared count of open
    /// incidents — to the timeline's own heading, unchanged in every other respect: still only open
    /// incidents, still a workspace fact rather than a per-person one.
    /// </summary>
    [Fact]
    public async Task The_timelines_own_badge_counts_only_open_incidents()
    {
        // The fixture's workspace is shared across every test in this collection, so the badge count
        // is asserted as a DELTA rather than an absolute number — another test's still-open incident
        // must not make this one flaky.
        Panel.GivenUser(fixture.WorkspaceId, "badge-view@example.com", Harbora.Domain.Common.SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.241", "badge-view@example.com");
        var before = OpenBadgeCount(await (await client.GetAsync("/monitoring")).Content.ReadAsStringAsync());

        Panel.Seed(db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.AlertIncidents.Add(new AlertIncident
            {
                WorkspaceId = fixture.WorkspaceId, Condition = AlertEvent.DiskWarning,
                SubjectRef = "server-badge-1", Severity = AlertSeverity.Warning,
                Title = "Low disk space", Body = "94% used", OpenedAt = now, LastObservedAt = now
            });
            db.AlertIncidents.Add(new AlertIncident
            {
                WorkspaceId = fixture.WorkspaceId, Condition = AlertEvent.BackupFailed,
                SubjectRef = "backup-badge-1", Severity = AlertSeverity.Warning,
                Title = "Backup failed", Body = "disk full", OpenedAt = now, LastObservedAt = now,
                ClosedAt = now, ClosedReason = IncidentClosedReason.Acknowledged
            });
        });

        var after = OpenBadgeCount(await (await client.GetAsync("/monitoring")).Content.ReadAsStringAsync());

        (after - before).Should().Be(1,
            "one incident is open and one is already closed, so the badge should only grow by the open one");
    }

    private static int OpenBadgeCount(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(html, "data-open-incident-count=\"(\\d+)\"");
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    [Fact]
    public async Task Acknowledging_an_incident_through_the_real_route_closes_it_and_the_page_reflects_it_on_reload()
    {
        var incidentId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.AlertIncidents.Add(new AlertIncident
            {
                Id = incidentId, WorkspaceId = fixture.WorkspaceId, Condition = AlertEvent.BackupFailed,
                SubjectRef = "backup-ack-1", Severity = AlertSeverity.Warning,
                Title = "Backup failed", Body = "disk full", OpenedAt = now, LastObservedAt = now
            });
        });
        Panel.GivenUser(fixture.WorkspaceId, "ack-flow@example.com", Harbora.Domain.Common.SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.242", "ack-flow@example.com");

        var token = await client.AntiforgeryTokenFrom("/monitoring");
        var response = await client.PostFormAsync($"/monitoring/incidents/{incidentId}/acknowledge", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var stored = Panel.Read(db => db.AlertIncidents.Single(i => i.Id == incidentId));
        stored.ClosedAt.Should().NotBeNull();
        stored.ClosedReason.Should().Be(IncidentClosedReason.Acknowledged);

        var html = await (await client.GetAsync("/monitoring")).Content.ReadAsStringAsync();
        html.Should().Contain($"data-incident-id=\"{incidentId}\"");
        html.Should().Contain("data-incident-open=\"false\"");
        html.Should().Contain("data-incident-closed-reason=\"Acknowledged\"");
    }

    [Fact]
    public async Task Acknowledging_another_workspaces_incident_through_the_real_route_changes_nothing()
    {
        var otherWorkspaceId = Guid.CreateVersion7();
        var incidentId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.AlertIncidents.Add(new AlertIncident
            {
                Id = incidentId, WorkspaceId = otherWorkspaceId, Condition = AlertEvent.BackupFailed,
                SubjectRef = "backup-cross-tenant", Severity = AlertSeverity.Warning,
                Title = "Backup failed", Body = "disk full", OpenedAt = now, LastObservedAt = now
            });
        });
        Panel.GivenUser(fixture.WorkspaceId, "ack-cross-tenant@example.com", Harbora.Domain.Common.SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.243", "ack-cross-tenant@example.com");

        var token = await client.AntiforgeryTokenFrom("/monitoring");
        var response = await client.PostFormAsync($"/monitoring/incidents/{incidentId}/acknowledge", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.AlertIncidents.IgnoreQueryFilters().Single(i => i.Id == incidentId))
            .ClosedAt.Should().BeNull("a rule in another workspace cannot be acknowledged from this session");
    }

    [Fact]
    public async Task The_monitoring_page_shows_the_delivery_log_by_data_attribute_in_the_default_Persian_panel()
    {
        // N1 (2026-08-16 notification-system spec): the delivery log beside the alert rules. The
        // panel renders Persian by default in tests, so this asserts on values and data- attributes
        // rather than on English prose that would never appear.
        var failedId = Guid.CreateVersion7();
        var sentId = Guid.CreateVersion7();

        Panel.Seed(db =>
        {
            db.NotificationDeliveries.Add(new Harbora.Domain.Notifications.NotificationDelivery
            {
                Id = failedId, WorkspaceId = fixture.WorkspaceId,
                Purpose = NotificationDeliveryPurpose.AlertDispatch, Channel = AlertChannel.Webhook,
                Subject = "Deploy failed: api #9", EncryptedBody = "x",
                Status = NotificationDeliveryStatus.Failed, Attempts = 3,
                LastError = "Webhook returned 502 Bad Gateway"
            });
            db.NotificationDeliveries.Add(new Harbora.Domain.Notifications.NotificationDelivery
            {
                Id = sentId, WorkspaceId = fixture.WorkspaceId,
                Purpose = NotificationDeliveryPurpose.NoRecipientFallback, Channel = AlertChannel.Email,
                RecipientAddress = "admin@example.com", Subject = "Disk warning", EncryptedBody = "x",
                Status = NotificationDeliveryStatus.Sent, Attempts = 1
            });
        });
        Panel.GivenUser(fixture.WorkspaceId, "delivery-log-view@example.com", Harbora.Domain.Common.SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.244", "delivery-log-view@example.com");

        var html = await (await client.GetAsync("/monitoring")).Content.ReadAsStringAsync();

        html.Should().Contain($"data-delivery-id=\"{failedId}\"");
        html.Should().Contain("data-delivery-status=\"Failed\"");
        html.Should().Contain("data-delivery-purpose=\"AlertDispatch\"");
        html.Should().Contain("data-delivery-attempts=\"3\"");
        html.Should().Contain("502 Bad Gateway");

        html.Should().Contain($"data-delivery-id=\"{sentId}\"");
        html.Should().Contain("data-delivery-status=\"Sent\"");
        html.Should().Contain("data-delivery-purpose=\"NoRecipientFallback\"");

        // Persian is the default. "ناموفق" (failed) is the status label on the failed row.
        html.Should().Contain("&#x646;&#x627;&#x645;&#x648;&#x641;&#x642;", "the Persian \"failed\" status label");
    }
}
