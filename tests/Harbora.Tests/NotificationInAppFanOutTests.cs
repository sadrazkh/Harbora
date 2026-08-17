using System.Net;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Monitoring;
using Harbora.Domain.Notifications;
using Harbora.Infrastructure.Notifications;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// In-app as the sink that cannot fail (N3, 2026-08-16 notification-system spec, "told a person, not
/// a channel"). Every previous test file in this area is about whether a <i>channel</i> took a
/// message; this one is about whether a <i>person</i> did — a question a channel-only test can never
/// answer, because a workspace that never configured one has always looked identical, from the
/// channel's point of view, to a workspace that reached everybody.
///
/// <para>
/// The scenario named directly in N3's own brief: a workspace with no channel configured at all is
/// still a workspace somebody hears from. That is exercised here with zero <see cref="Alert"/> rows —
/// the ordinary case, since nothing but the alerts page ever creates one — rather than with a channel
/// that fails, which is N1's concern, not this one's.
/// </para>
/// </summary>
public class NotificationInAppFanOutTests
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
        HttpStatusCode channelStatus = HttpStatusCode.OK)
    {
        var store = "fanout-" + Guid.NewGuid();
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase(store).Options);

        var scope = new NotificationQueueScope(store);
        var service = new NotificationService(db, new PassthroughProtector(),
            new SingleHandlerFactory(new Responder(channelStatus)),
            new Harbora.Infrastructure.Notifications.PlatformMailer(
                db, new PassthroughProtector(),
                NullLogger<Harbora.Infrastructure.Notifications.PlatformMailer>.Instance),
            Harbora.Infrastructure.Functions.NullFunctionEventBus.Instance,
            scope.Factory,
            new FixedClock(),
            Microsoft.Extensions.Options.Options.Create(new NotificationOptions()),
            new NotificationTemplateCatalog(),
            NullLogger<NotificationService>.Instance);
        return (service, db, scope);
    }

    /// <summary>A signed-up, active member of <see cref="Workspace"/> in <paramref name="db"/>.
    /// <paramref name="preferredCulture"/> is null to leave <c>User.PreferredCulture</c> at its own
    /// default ("fa" — <c>User.cs:25</c>) rather than this fixture choosing one by hand.</summary>
    private static Guid AddMember(
        HarboraDbContext db, WorkspaceRole role = WorkspaceRole.Member, bool active = true, string? preferredCulture = null)
    {
        var user = new User { Email = $"{Guid.NewGuid()}@example.com", DisplayName = "member", IsActive = active };
        if (preferredCulture is not null) user.PreferredCulture = preferredCulture;
        db.Users.Add(user);
        db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = Workspace, UserId = user.Id, Role = role });
        db.SaveChanges();
        return user.Id;
    }

    [Fact]
    public async Task A_workspace_with_no_alert_rule_at_all_still_writes_an_in_app_row_for_every_member()
    {
        var (service, db, scope) = Build();
        var alice = AddMember(db);
        var bob = AddMember(db);

        var evt = NotificationEventData.Create(AlertEvent.DeployFailed,
            ("AppName", "api"), ("DeploymentNumber", "4"), ("Reason", "build error"));
        await service.NotifyAsync(Workspace, evt, AlertSeverity.Critical, default);

        // Neither member set a culture, so both render at User.PreferredCulture's own default ("fa").
        var expected = new NotificationTemplateCatalog().Render(evt, "fa");
        var rows = scope.NewDb().UserNotifications.Where(n => n.WorkspaceId == Workspace).ToList();
        rows.Select(r => r.UserId).Should().BeEquivalentTo([alice, bob],
            "a workspace nobody configured a channel for is still a workspace somebody was told about");
        rows.Should().AllSatisfy(r =>
        {
            r.ReadAt.Should().BeNull();
            r.Title.Should().Be(expected.Subject);
            r.Body.Should().Be(expected.TextBody);
            r.Severity.Should().Be(AlertSeverity.Critical);
        });
    }

    [Fact]
    public async Task Every_member_gets_their_own_copy_even_when_a_channel_also_matches()
    {
        // The in-app copy is not the zero-rule fallback's understudy — it is written whether or not a
        // channel exists, so an alert rule matching (and its own delivery reaching Discord/webhook/etc.)
        // must not suppress the in-app row for anybody in the workspace.
        var (service, db, scope) = Build(HttpStatusCode.NoContent);
        db.Alerts.Add(new Alert
        {
            WorkspaceId = Workspace, Name = "ops", Channel = AlertChannel.Discord,
            MinSeverity = AlertSeverity.Info, EncryptedTarget = """{"Url":"https://discord.com/api/webhooks/1/x"}""",
            IsEnabled = true
        });
        var alice = AddMember(db);
        var bob = AddMember(db);
        db.SaveChanges();

        var matched = await service.NotifyAsync(Workspace,
            NotificationEventData.Create(AlertEvent.DeployFailed,
                ("AppName", "api"), ("DeploymentNumber", "5"), ("Reason", "build error")),
            AlertSeverity.Critical, default);

        matched.Should().Be(1, "one alert rule matched, which is what NotifyAsync's count has always meant");
        scope.NewDb().UserNotifications.Where(n => n.WorkspaceId == Workspace)
            .Select(n => n.UserId).Should().BeEquivalentTo([alice, bob]);
    }

    [Fact]
    public async Task A_deactivated_member_gets_no_copy()
    {
        var (service, db, scope) = Build();
        var alice = AddMember(db);
        AddMember(db, active: false);

        await service.NotifyAsync(Workspace,
            NotificationEventData.Create(AlertEvent.AppCrashed, ("AppName", "worker"), ("Reason", "Exited")),
            AlertSeverity.Critical, default);

        scope.NewDb().UserNotifications.Where(n => n.WorkspaceId == Workspace)
            .Select(n => n.UserId).Should().BeEquivalentTo([alice]);
    }

    [Fact]
    public async Task A_workspace_with_nobody_in_it_writes_nothing_and_does_not_throw()
    {
        var (service, db, scope) = Build();

        var act = async () => await service.NotifyAsync(Workspace,
            NotificationEventData.Create(AlertEvent.DiskWarning, ("ServerName", "node-1"), ("Percent", "94")),
            AlertSeverity.Warning, default);

        await act.Should().NotThrowAsync();
        scope.NewDb().UserNotifications.Where(n => n.WorkspaceId == Workspace).Should().BeEmpty();
    }

    [Fact]
    public async Task NotifyRuleAsync_also_fans_out_to_every_member_of_the_rules_workspace()
    {
        // The per-app threshold path (MetricsCollector -> NotifyRuleAsync) is a workspace event too —
        // "told a person, not a channel" does not stop being true just because the caller already
        // holds the matched rule rather than asking NotifyAsync to find one.
        var (service, db, scope) = Build();
        var rule = new Alert
        {
            WorkspaceId = Workspace, Name = "cpu", Channel = AlertChannel.Webhook,
            EncryptedTarget = """{"Url":"https://hooks.example.com/abc"}""", IsEnabled = true
        };
        db.Alerts.Add(rule);
        var alice = AddMember(db);
        db.SaveChanges();

        var result = await service.NotifyRuleAsync(rule.Id,
            NotificationEventData.Create(AlertEvent.ThresholdBreached,
                ("AppName", "api"), ("Metric", "MemoryPercent"), ("Threshold", "90"), ("SustainedMinutes", "5")),
            AlertSeverity.Warning, default);

        result.Delivered.Should().BeTrue();
        scope.NewDb().UserNotifications.Where(n => n.WorkspaceId == Workspace)
            .Select(n => n.UserId).Should().BeEquivalentTo([alice]);
    }

    [Fact]
    public async Task Each_members_row_is_independent_so_one_can_be_marked_read_without_the_others()
    {
        // Not "one row, many readers" — one row per member. This is what makes "read by one, still
        // unread for another" possible at all; the controller test in NotificationsHttpTests exercises
        // the real mark-read path end to end, but the independence has to be true at the data layer
        // first, or nothing built on top of it could ever be correct.
        var (service, db, scope) = Build();
        var alice = AddMember(db);
        var bob = AddMember(db);

        await service.NotifyAsync(Workspace,
            NotificationEventData.Create(AlertEvent.BackupFailed, ("TargetRef", "primary-db"), ("Detail", "disk full")),
            AlertSeverity.Warning, default);

        var writer = scope.NewDb();
        var aliceRow = writer.UserNotifications.Single(n => n.UserId == alice);
        aliceRow.ReadAt = new FixedClock().UtcNow;
        writer.SaveChanges();

        var reader = scope.NewDb();
        reader.UserNotifications.Single(n => n.UserId == alice).ReadAt.Should().NotBeNull();
        reader.UserNotifications.Single(n => n.UserId == bob).ReadAt.Should().BeNull(
            "each member's row is their own; one person's read state must never leak onto another's");
    }

    // ---- N4 (2026-08-16 notification-system spec, "in the reader's own language") ------------

    /// <summary>
    /// The whole feature, named directly: one raised event, two members of the same workspace, two
    /// different <c>PreferredCulture</c>s — and two different languages land in their inboxes. A test
    /// with a single recipient could not prove this at all; it would still pass if
    /// <c>NotificationService</c> quietly rendered every row in whichever culture happened to be
    /// requested first.
    /// </summary>
    [Fact]
    public async Task The_same_event_renders_in_each_members_own_preferred_culture()
    {
        var (service, db, scope) = Build();
        var farsiReader = AddMember(db, preferredCulture: "fa");
        var englishReader = AddMember(db, preferredCulture: "en");

        var evt = NotificationEventData.Create(AlertEvent.AppCrashed, ("AppName", "worker"), ("Reason", "Exited"));
        await service.NotifyAsync(Workspace, evt, AlertSeverity.Critical, default);

        var catalog = new NotificationTemplateCatalog();
        var fa = catalog.Render(evt, "fa");
        var en = catalog.Render(evt, "en");
        fa.Subject.Should().NotBe(en.Subject, "the fixture is only worth anything if the two languages actually differ");

        var rows = scope.NewDb().UserNotifications.Where(n => n.WorkspaceId == Workspace).ToList();
        var farsiRow = rows.Single(r => r.UserId == farsiReader);
        var englishRow = rows.Single(r => r.UserId == englishReader);

        farsiRow.Title.Should().Be(fa.Subject);
        farsiRow.Body.Should().Be(fa.TextBody);
        englishRow.Title.Should().Be(en.Subject);
        englishRow.Body.Should().Be(en.TextBody);
        farsiRow.Title.Should().NotBe(englishRow.Title,
            "the same event, told to two people, must not read the same to both of them");
    }

    /// <summary>
    /// A member who never touched the culture picker still has a row — <c>User.PreferredCulture</c>'s
    /// own default (<c>User.cs:25</c>) is "fa", not a null the rendering step has to special-case.
    /// </summary>
    [Fact]
    public async Task A_member_with_no_preferred_culture_set_is_told_in_the_platforms_default_fa()
    {
        var (service, db, scope) = Build();
        var member = AddMember(db); // preferredCulture left null — the User entity's own default applies.
        db.Users.Single(u => u.Id == member).PreferredCulture.Should().Be("fa",
            "the fixture must actually exercise the unset default, not a value this test chose");

        var evt = NotificationEventData.Create(AlertEvent.DiskWarning, ("ServerName", "node-1"), ("Percent", "94"));
        await service.NotifyAsync(Workspace, evt, AlertSeverity.Warning, default);

        var expected = new NotificationTemplateCatalog().Render(evt, "fa");
        var row = scope.NewDb().UserNotifications.Single(n => n.WorkspaceId == Workspace);
        row.Title.Should().Be(expected.Subject);
        row.Body.Should().Be(expected.TextBody);
    }
}
