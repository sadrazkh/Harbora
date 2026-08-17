using System.Net;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Notifications;
using Harbora.Infrastructure.Notifications;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// N5 (2026-08-16 notification-system spec, "noise control") — the routing decision
/// <c>NotificationService.FanOutToMembersAsync</c> now makes per member: preference, quiet hours, and
/// the invariant that a critical event always lands somewhere.
///
/// <para>
/// The three behaviours the owner's brief names as mattering most: a critical event reaches its
/// recipient during quiet hours and cannot be switched off, only re-routed
/// (<see cref="A_critical_event_is_sent_immediately_even_during_quiet_hours"/>,
/// <see cref="A_critical_event_re_routed_to_email_only_still_reaches_the_recipient_during_quiet_hours"/>,
/// <see cref="The_safety_net_forces_the_in_app_row_if_a_critical_event_would_otherwise_land_nowhere"/>);
/// a user who has never touched preferences receives a newly added event kind
/// (<see cref="A_user_with_no_preference_rows_still_gets_the_in_app_copy_of_an_event_added_after_they_registered"/>);
/// an optional event held for quiet hours is queued for the digest rather than sent or dropped
/// (<see cref="An_optional_event_during_quiet_hours_is_queued_for_the_digest_instead_of_emailed_immediately"/>).
/// </para>
/// </summary>
public class NotificationPreferenceRoutingTests
{
    private static readonly Guid Workspace = Guid.CreateVersion7();

    private sealed class Responder(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (NotificationService Service, HarboraDbContext Db, NotificationQueueScope Scope) Build(
        FixedClock clock)
    {
        var store = "prefroute-" + Guid.NewGuid();
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase(store).Options);

        var scope = new NotificationQueueScope(store, clock);
        var service = new NotificationService(db, new PassthroughProtector(),
            new SingleHandlerFactory(new Responder(HttpStatusCode.OK)),
            new PlatformMailer(db, new PassthroughProtector(), NullLogger<PlatformMailer>.Instance),
            Harbora.Infrastructure.Functions.NullFunctionEventBus.Instance,
            scope.Factory, clock,
            Microsoft.Extensions.Options.Options.Create(new NotificationOptions()),
            new NotificationTemplateCatalog(),
            NullLogger<NotificationService>.Instance);
        return (service, db, scope);
    }

    private static Guid AddMember(
        HarboraDbContext db, string? timeZoneId = null, int? quietStart = null, int? quietEnd = null)
    {
        var user = new User
        {
            Email = $"{Guid.NewGuid()}@example.com", DisplayName = "member", IsActive = true,
            TimeZoneId = timeZoneId ?? "UTC", QuietHoursStartHour = quietStart, QuietHoursEndHour = quietEnd
        };
        db.Users.Add(user);
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            WorkspaceId = Workspace, UserId = user.Id, Role = WorkspaceRole.Member
        });
        db.SaveChanges();
        return user.Id;
    }

    private static void SetPreference(
        HarboraDbContext db, Guid userId, AlertEvent evt, NotificationChannel channel, NotificationPreferenceMode mode)
    {
        db.NotificationPreferences.Add(new NotificationPreference
        {
            UserId = userId, EventType = evt, Channel = channel, Mode = mode
        });
        db.SaveChanges();
    }

    // 23:00 UTC, inside a 22->06 quiet window measured in UTC (member's TimeZoneId defaults to UTC).
    private static readonly DateTimeOffset DuringQuietHours = new(2026, 6, 15, 23, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_critical_event_is_sent_immediately_even_during_quiet_hours()
    {
        var clock = new FixedClock(DuringQuietHours);
        var (service, db, scope) = Build(clock);
        var member = AddMember(db, quietStart: 22, quietEnd: 6);

        await service.NotifyAsync(Workspace,
            NotificationEventData.Create(AlertEvent.AppCrashed, ("AppName", "worker"), ("Reason", "Exited")),
            AlertSeverity.Critical, default);

        var row = scope.NewDb().UserNotifications.Should().ContainSingle().Which;
        row.UserId.Should().Be(member);
        row.ReadAt.Should().BeNull("the in-app row is not held back — a critical event ignores quiet hours entirely");
    }

    [Fact]
    public async Task A_critical_event_re_routed_to_email_only_still_reaches_the_recipient_during_quiet_hours()
    {
        var clock = new FixedClock(DuringQuietHours);
        var (service, db, scope) = Build(clock);
        var member = AddMember(db, quietStart: 22, quietEnd: 6);
        SetPreference(db, member, AlertEvent.AppCrashed, NotificationChannel.Email, NotificationPreferenceMode.Immediate);
        SetPreference(db, member, AlertEvent.AppCrashed, NotificationChannel.InApp, NotificationPreferenceMode.Off);

        await service.NotifyAsync(Workspace,
            NotificationEventData.Create(AlertEvent.AppCrashed, ("AppName", "worker"), ("Reason", "Exited")),
            AlertSeverity.Critical, default);

        scope.NewDb().UserNotifications.Where(n => n.WorkspaceId == Workspace).Should().BeEmpty(
            "in-app was explicitly turned off, and email covers the event, so no in-app copy is written");

        // The re-routed channel: a Pending NotificationDelivery for this member's own address, queued
        // for immediate send — not folded into a digest, because quiet hours never touch a critical
        // event's delivery, whichever channel it ends up on. Filtered to this member's own purpose:
        // the workspace also has no alert rule and no admin, so N1's own admin fallback fires
        // alongside this and is not what the test is about.
        var delivery = scope.NewDb().NotificationDeliveries
            .Where(d => d.Purpose == NotificationDeliveryPurpose.PersonalPreference)
            .Should().ContainSingle().Which;
        delivery.RecipientAddress.Should().Be(db.Users.Single(u => u.Id == member).Email);
        delivery.Status.Should().Be(NotificationDeliveryStatus.Pending);
        scope.NewDb().NotificationDigestEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task The_safety_net_forces_the_in_app_row_if_a_critical_event_would_otherwise_land_nowhere()
    {
        // A row-level anomaly a well-behaved NotificationPreferenceService.SetAsync would never
        // produce (both channels explicitly Off for a critical event) — written directly, the way a
        // bad migration or a hand edit could. FanOutToMembersAsync's own defensive check must still
        // guarantee delivery rather than trust the stored rows.
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero));
        var (service, db, scope) = Build(clock);
        var member = AddMember(db);
        SetPreference(db, member, AlertEvent.BackupFailed, NotificationChannel.InApp, NotificationPreferenceMode.Off);
        SetPreference(db, member, AlertEvent.BackupFailed, NotificationChannel.Email, NotificationPreferenceMode.Off);

        await service.NotifyAsync(Workspace,
            NotificationEventData.Create(AlertEvent.BackupFailed, ("TargetRef", "db"), ("Detail", "disk full")),
            AlertSeverity.Critical, default);

        scope.NewDb().UserNotifications.Where(n => n.UserId == member).Should().ContainSingle(
            "the safety net must win over two Off rows that should never have coexisted for a critical event");
    }

    [Fact]
    public async Task A_user_with_no_preference_rows_still_gets_the_in_app_copy_of_an_event_added_after_they_registered()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero));
        var (service, db, scope) = Build(clock);
        var member = AddMember(db);
        db.NotificationPreferences.Should().BeEmpty("this member has never opened the preferences page");

        // ThresholdBreached stands in for "a kind that did not exist when this member registered" —
        // what matters is that no row names it, for anybody, and the in-app copy still arrives.
        await service.NotifyAsync(Workspace,
            NotificationEventData.Create(AlertEvent.ThresholdBreached,
                ("AppName", "api"), ("Metric", "CpuPercent"), ("Threshold", "90"), ("SustainedMinutes", "5")),
            AlertSeverity.Warning, default);

        scope.NewDb().UserNotifications.Where(n => n.WorkspaceId == Workspace)
            .Select(n => n.UserId).Should().BeEquivalentTo([member],
                "an absent preference row resolves to the default (in-app, immediate) — proven here, not assumed");
    }

    [Fact]
    public async Task An_optional_event_during_quiet_hours_is_queued_for_the_digest_instead_of_emailed_immediately()
    {
        var clock = new FixedClock(DuringQuietHours);
        var (service, db, scope) = Build(clock);
        var member = AddMember(db, quietStart: 22, quietEnd: 6);
        SetPreference(db, member, AlertEvent.ThresholdBreached, NotificationChannel.Email, NotificationPreferenceMode.Immediate);

        var rule = new Domain.Monitoring.Alert
        {
            WorkspaceId = Workspace, Name = "cpu", Channel = AlertChannel.Webhook,
            EncryptedTarget = """{"Url":"https://hooks.example.com/abc"}""", IsEnabled = true
        };
        db.Alerts.Add(rule);
        db.SaveChanges();

        await service.NotifyRuleAsync(rule.Id,
            NotificationEventData.Create(AlertEvent.ThresholdBreached,
                ("AppName", "api"), ("Metric", "MemoryPercent"), ("Threshold", "90"), ("SustainedMinutes", "5")),
            AlertSeverity.Warning, default);

        scope.NewDb().NotificationDeliveries.Where(d => d.Purpose == NotificationDeliveryPurpose.PersonalPreference)
            .Should().BeEmpty("quiet hours downgraded the immediate email to a digest entry instead");
        var entry = scope.NewDb().NotificationDigestEntries.Should().ContainSingle().Which;
        entry.UserId.Should().Be(member);
        entry.DeliveryId.Should().BeNull("nothing has flushed it into a delivery yet");
    }

    [Fact]
    public async Task An_optional_event_outside_quiet_hours_is_emailed_immediately_when_the_preference_says_so()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero)); // midday — never quiet
        var (service, db, scope) = Build(clock);
        var member = AddMember(db, quietStart: 22, quietEnd: 6);
        SetPreference(db, member, AlertEvent.ThresholdBreached, NotificationChannel.Email, NotificationPreferenceMode.Immediate);

        var rule = new Domain.Monitoring.Alert
        {
            WorkspaceId = Workspace, Name = "cpu", Channel = AlertChannel.Webhook,
            EncryptedTarget = """{"Url":"https://hooks.example.com/abc"}""", IsEnabled = true
        };
        db.Alerts.Add(rule);
        db.SaveChanges();

        await service.NotifyRuleAsync(rule.Id,
            NotificationEventData.Create(AlertEvent.ThresholdBreached,
                ("AppName", "api"), ("Metric", "MemoryPercent"), ("Threshold", "90"), ("SustainedMinutes", "5")),
            AlertSeverity.Warning, default);

        scope.NewDb().NotificationDigestEntries.Should().BeEmpty();
        scope.NewDb().NotificationDeliveries
            .Where(d => d.Purpose == NotificationDeliveryPurpose.PersonalPreference)
            .Should().ContainSingle().Which.RecipientAddress.Should()
            .Be(db.Users.Single(u => u.Id == member).Email);
    }

    [Fact]
    public async Task An_optional_event_muted_on_every_channel_reaches_nobody()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero));
        var (service, db, scope) = Build(clock);
        var member = AddMember(db);
        SetPreference(db, member, AlertEvent.ThresholdBreached, NotificationChannel.InApp, NotificationPreferenceMode.Off);
        SetPreference(db, member, AlertEvent.ThresholdBreached, NotificationChannel.Email, NotificationPreferenceMode.Off);

        var rule = new Domain.Monitoring.Alert
        {
            WorkspaceId = Workspace, Name = "cpu", Channel = AlertChannel.Webhook,
            EncryptedTarget = """{"Url":"https://hooks.example.com/abc"}""", IsEnabled = true
        };
        db.Alerts.Add(rule);
        db.SaveChanges();

        await service.NotifyRuleAsync(rule.Id,
            NotificationEventData.Create(AlertEvent.ThresholdBreached,
                ("AppName", "api"), ("Metric", "MemoryPercent"), ("Threshold", "90"), ("SustainedMinutes", "5")),
            AlertSeverity.Warning, default);

        scope.NewDb().UserNotifications.Where(n => n.UserId == member).Should().BeEmpty();
        scope.NewDb().NotificationDigestEntries.Should().BeEmpty();
        scope.NewDb().NotificationDeliveries
            .Where(d => d.Purpose == NotificationDeliveryPurpose.PersonalPreference).Should().BeEmpty(
                "an optional event may be silenced on every channel — this is what makes it optional");
    }
}
