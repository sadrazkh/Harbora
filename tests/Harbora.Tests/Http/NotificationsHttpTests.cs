using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// A person's own inbox, end to end (N3, 2026-08-16 notification-system spec, "told a person, not a
/// channel"): the real bell, the real <c>/notifications</c> route, real Razor. Complements
/// <see cref="NotificationInAppFanOutTests"/>, which drives a hand-built <c>NotificationService</c>
/// directly — this drives the whole panel through HTTP, including the production DI wiring and the
/// two controller actions a hand-built service never exercises.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class NotificationsHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private static int UnreadBadgeCount(string html)
    {
        var match = Regex.Match(html, "data-unread-notifications-count=\"(\\d+)\"");
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    /// <summary>
    /// The bell's whole point of changing meaning: it answers "what has THIS person not read", not
    /// "how many open incidents does the workspace have" — a colleague's unread rows, and the same
    /// person's already-read rows, must not move the number.
    /// </summary>
    [Fact]
    public async Task The_bell_counts_this_persons_own_unread_notifications_and_nobody_elses()
    {
        var me = Panel.GivenUser(fixture.WorkspaceId, "bell-me@example.com", SystemRole.Owner);
        var someoneElse = Panel.GivenUser(fixture.WorkspaceId, "bell-someone-else@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.250", "bell-me@example.com");

        var before = UnreadBadgeCount(await (await client.GetAsync("/monitoring")).Content.ReadAsStringAsync());

        Panel.Seed(db =>
        {
            db.UserNotifications.Add(new UserNotification
            {
                WorkspaceId = fixture.WorkspaceId, UserId = me.Id,
                Title = "Deploy failed: api #1", Body = "build error"
            });
            db.UserNotifications.Add(new UserNotification
            {
                // Already read — must not count.
                WorkspaceId = fixture.WorkspaceId, UserId = me.Id,
                Title = "Deploy failed: api #0", Body = "build error", ReadAt = DateTimeOffset.UtcNow
            });
            db.UserNotifications.Add(new UserNotification
            {
                // A colleague's own copy of the same event — must not count on MY badge.
                WorkspaceId = fixture.WorkspaceId, UserId = someoneElse.Id,
                Title = "Deploy failed: api #1", Body = "build error"
            });
        });

        var after = UnreadBadgeCount(await (await client.GetAsync("/monitoring")).Content.ReadAsStringAsync());

        (after - before).Should().Be(1, "only my own unread row should move my badge");
    }

    /// <summary>The core claim of N3 itself: a workspace with no channel configured at all is still a
    /// workspace whose members were told, because the write does not depend on one existing.</summary>
    [Fact]
    public async Task A_workspace_with_no_channel_configured_at_all_still_reaches_its_members_in_app()
    {
        var workspaceId = Guid.CreateVersion7();
        Panel.Seed(db => db.Workspaces.Add(new Harbora.Domain.Identity.Workspace
        {
            Id = workspaceId, Name = "No Channel Co", Slug = "no-channel-co-" + workspaceId
        }));
        var alice = Panel.GivenUser(workspaceId, "unheard-alice@example.com", SystemRole.Owner);
        var bob = Panel.GivenUser(workspaceId, "unheard-bob@example.com", SystemRole.Member);

        // No Alert row is ever created for this workspace — the ordinary case, since nothing but the
        // alerts page creates one. Real production DI wiring, not a hand-built service. A scope of its
        // own, kept open for the whole async call: Resolve<T>() disposes its scope on return, which
        // would tear down the very DbContext this call still needs mid-flight.
        using (var scope = Panel.Services.CreateScope())
        {
            var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
            await notifications.NotifyAsync(workspaceId,
                NotificationEventData.Create(AlertEvent.DeployFailed,
                    ("AppName", "api"), ("DeploymentNumber", "9"), ("Reason", "build error")),
                AlertSeverity.Critical, default);
        }

        var rows = Panel.Read(db => db.UserNotifications.Where(n => n.WorkspaceId == workspaceId).ToList());
        rows.Select(r => r.UserId).Should().BeEquivalentTo([alice.Id, bob.Id],
            "every member of a workspace with no channel at all is still somebody who was told");
    }

    /// <summary>The property that makes a shared bell honest: one person's read state can never leak
    /// onto another's, because each row belongs to exactly one person.</summary>
    [Fact]
    public async Task A_notification_read_by_one_member_stays_unread_for_another()
    {
        var alice = Panel.GivenUser(fixture.WorkspaceId, "read-alice@example.com", SystemRole.Owner);
        var bob = Panel.GivenUser(fixture.WorkspaceId, "read-bob@example.com", SystemRole.Owner);

        var aliceRowId = Guid.CreateVersion7();
        var bobRowId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.UserNotifications.Add(new UserNotification
            {
                Id = aliceRowId, WorkspaceId = fixture.WorkspaceId, UserId = alice.Id,
                Title = "Backup failed", Body = "disk full"
            });
            db.UserNotifications.Add(new UserNotification
            {
                Id = bobRowId, WorkspaceId = fixture.WorkspaceId, UserId = bob.Id,
                Title = "Backup failed", Body = "disk full"
            });
        });

        var aliceClient = await Panel.SignedInAs("203.0.113.251", "read-alice@example.com");
        var token = await aliceClient.AntiforgeryTokenFrom("/notifications");
        var response = await aliceClient.PostFormAsync($"/notifications/{aliceRowId}/read", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.UserNotifications.Single(n => n.Id == aliceRowId)).ReadAt.Should().NotBeNull();
        Panel.Read(db => db.UserNotifications.Single(n => n.Id == bobRowId)).ReadAt.Should().BeNull(
            "bob's own copy of the same event must be untouched by alice's read");
    }

    /// <summary>Posting somebody else's notification id must find nothing to mark, not touch it via
    /// the id alone — the lookup is by (id, UserId, WorkspaceId) together.</summary>
    [Fact]
    public async Task Marking_a_neighbours_notification_read_by_id_changes_nothing()
    {
        var owner = Panel.GivenUser(fixture.WorkspaceId, "guard-owner@example.com", SystemRole.Owner);
        Panel.GivenUser(fixture.WorkspaceId, "guard-attacker@example.com", SystemRole.Owner);

        var ownersRowId = Guid.CreateVersion7();
        Panel.Seed(db => db.UserNotifications.Add(new UserNotification
        {
            Id = ownersRowId, WorkspaceId = fixture.WorkspaceId, UserId = owner.Id,
            Title = "Low disk space", Body = "94% used"
        }));

        var attacker = await Panel.SignedInAs("203.0.113.252", "guard-attacker@example.com");
        var token = await attacker.AntiforgeryTokenFrom("/notifications");
        var response = await attacker.PostFormAsync($"/notifications/{ownersRowId}/read", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.UserNotifications.Single(n => n.Id == ownersRowId)).ReadAt.Should().BeNull(
            "a notification id alone must not be enough to mark somebody else's row read");
    }

    [Fact]
    public async Task Mark_all_read_clears_every_one_of_my_unread_rows_and_no_one_elses()
    {
        var me = Panel.GivenUser(fixture.WorkspaceId, "markall-me@example.com", SystemRole.Owner);
        var someoneElse = Panel.GivenUser(fixture.WorkspaceId, "markall-someone-else@example.com", SystemRole.Owner);

        var otherPersonsRowId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.UserNotifications.Add(new UserNotification
            {
                WorkspaceId = fixture.WorkspaceId, UserId = me.Id, Title = "a", Body = "a"
            });
            db.UserNotifications.Add(new UserNotification
            {
                WorkspaceId = fixture.WorkspaceId, UserId = me.Id, Title = "b", Body = "b"
            });
            db.UserNotifications.Add(new UserNotification
            {
                Id = otherPersonsRowId, WorkspaceId = fixture.WorkspaceId, UserId = someoneElse.Id,
                Title = "c", Body = "c"
            });
        });

        var client = await Panel.SignedInAs("203.0.113.253", "markall-me@example.com");
        var token = await client.AntiforgeryTokenFrom("/notifications");
        var response = await client.PostFormAsync("/notifications/read-all", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.UserNotifications.Where(n => n.UserId == me.Id).ToList())
            .Should().OnlyContain(n => n.ReadAt != null);
        Panel.Read(db => db.UserNotifications.Single(n => n.Id == otherPersonsRowId))
            .ReadAt.Should().BeNull("mark-all-read is scoped to the caller, never the whole workspace");
    }

    [Fact]
    public async Task The_notifications_page_paginates_and_filters_to_unread_only_by_data_attribute()
    {
        var me = Panel.GivenUser(fixture.WorkspaceId, "page-me@example.com", SystemRole.Owner);
        var readId = Guid.CreateVersion7();
        var unreadId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.UserNotifications.Add(new UserNotification
            {
                Id = readId, WorkspaceId = fixture.WorkspaceId, UserId = me.Id,
                Title = "Already seen", Body = "x", ReadAt = DateTimeOffset.UtcNow
            });
            db.UserNotifications.Add(new UserNotification
            {
                Id = unreadId, WorkspaceId = fixture.WorkspaceId, UserId = me.Id,
                Title = "Not seen yet", Body = "y"
            });
        });

        var client = await Panel.SignedInAs("203.0.113.254", "page-me@example.com");

        var all = await (await client.GetAsync("/notifications")).Content.ReadAsStringAsync();
        all.Should().Contain($"data-notification-id=\"{readId}\"");
        all.Should().Contain($"data-notification-id=\"{unreadId}\"");
        all.Should().Contain("data-notification-read=\"true\"");
        all.Should().Contain("data-notification-read=\"false\"");

        var unreadOnly = await (await client.GetAsync("/notifications?unreadOnly=true")).Content.ReadAsStringAsync();
        unreadOnly.Should().NotContain($"data-notification-id=\"{readId}\"");
        unreadOnly.Should().Contain($"data-notification-id=\"{unreadId}\"");
    }
}
