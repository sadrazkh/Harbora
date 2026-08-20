using System.Net;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Notifications;
using Harbora.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Platform announcements (Sub-project 4, 2026-08-20 platform-options plan), through real requests —
/// the banner partial rendered from <c>_Layout</c>, the admin CRUD behind <c>tenants.manage</c>, and
/// the dismiss button any signed-in person can reach.
///
/// <para>
/// Every assertion reads <c>data-announcement</c> rather than a sentence — the panel renders Persian
/// by default in this harness, the same reasoning <c>AttentionPanelModeHttpTests</c> and
/// <c>RevenuePageHttpTests</c> already document for their own assertions.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AnnouncementHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private static (string Title, string Body, string TitleFa, string BodyFa) Fields(string label) => (
        $"{label} title", $"{label} body",
        $"{label} عنوان", $"{label} متن");

    private Announcement Seed(
        string label, AlertSeverity severity = AlertSeverity.Info,
        DateTimeOffset? startsAt = null, DateTimeOffset? endsAt = null)
    {
        var (title, body, titleFa, bodyFa) = Fields(label);
        var announcement = new Announcement
        {
            Title = title, Body = body, TitleFa = titleFa, BodyFa = bodyFa,
            Severity = severity, StartsAt = startsAt, EndsAt = endsAt,
            CreatedByUserId = Guid.CreateVersion7(), CreatedByEmail = "seed@example.com"
        };
        Panel.Seed(db => db.Announcements.Add(announcement));
        return announcement;
    }

    // --- who may open the admin console ----------------------------------------------------------

    [Fact]
    public async Task A_workspace_owner_cannot_reach_the_admin_console()
    {
        var tenant = new Workspace { Name = "announce-owner-refused", Slug = "announce-owner-refused" };
        Panel.Seed(db => db.Workspaces.Add(tenant));
        var owner = Panel.GivenUser(tenant.Id, "announce-owner@example.com", SystemRole.Member);
        Panel.Seed(db =>
        {
            var membership = db.WorkspaceMembers.IgnoreQueryFilters()
                .Single(m => m.WorkspaceId == tenant.Id && m.UserId == owner.Id);
            membership.Role = WorkspaceRole.Admin;
        });
        var client = await Panel.SignedInAs("203.0.113.210", "announce-owner@example.com");

        var response = await client.GetAsync("/announcements");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/denied");
    }

    [Fact]
    public async Task A_platform_owner_opens_the_admin_console()
    {
        Panel.GivenUser(fixture.WorkspaceId, "announce-platform-owner@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.211", "announce-platform-owner@example.com");

        var response = await client.GetAsync("/announcements");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // --- both languages are required, not optional ------------------------------------------------

    [Fact]
    public async Task Posting_without_a_persian_title_creates_nothing()
    {
        Panel.GivenUser(fixture.WorkspaceId, "announce-no-fa@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.212", "announce-no-fa@example.com");
        var token = await client.AntiforgeryTokenFrom("/announcements");

        await client.PostFormAsync("/announcements/create", token,
            ("title", "English only"), ("body", "English body"),
            ("titleFa", ""), ("bodyFa", ""), ("severity", "Info"));

        Panel.Read(db => db.Announcements.Any(a => a.Title == "English only")).Should().BeFalse(
            "an announcement half its readers cannot read is not an announcement");
    }

    [Fact]
    public async Task Posting_without_an_english_title_creates_nothing()
    {
        Panel.GivenUser(fixture.WorkspaceId, "announce-no-en@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.213", "announce-no-en@example.com");
        var token = await client.AntiforgeryTokenFrom("/announcements");

        await client.PostFormAsync("/announcements/create", token,
            ("title", ""), ("body", ""),
            ("titleFa", "فقط فارسی"), ("bodyFa", "متن فارسی"), ("severity", "Info"));

        Panel.Read(db => db.Announcements.Any(a => a.TitleFa == "فقط فارسی")).Should().BeFalse();
    }

    [Fact]
    public async Task Posting_with_both_languages_creates_the_row()
    {
        Panel.GivenUser(fixture.WorkspaceId, "announce-both@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.214", "announce-both@example.com");
        var token = await client.AntiforgeryTokenFrom("/announcements");

        await client.PostFormAsync("/announcements/create", token,
            ("title", "Both languages"), ("body", "English body"),
            ("titleFa", "هر دو زبان"), ("bodyFa", "متن فارسی"), ("severity", "Info"));

        Panel.Read(db => db.Announcements.Any(a => a.Title == "Both languages" && a.TitleFa == "هر دو زبان"))
            .Should().BeTrue();
    }

    // --- active window respected -------------------------------------------------------------------

    [Fact]
    public async Task The_active_window_is_respected_before_during_and_after()
    {
        var now = DateTimeOffset.UtcNow;
        var active = Seed("window-active");
        var future = Seed("window-future", startsAt: now.AddDays(1));
        var past = Seed("window-past", endsAt: now.AddDays(-1));

        Panel.GivenUser(fixture.WorkspaceId, "announce-window@example.com", SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.215", "announce-window@example.com");

        var html = await client.GetStringAsync("/");

        html.Should().Contain($@"data-announcement=""{active.Id}""");
        html.Should().NotContain($@"data-announcement=""{future.Id}""");
        html.Should().NotContain($@"data-announcement=""{past.Id}""");
    }

    // --- dismissal is per-user and per-announcement -------------------------------------------------

    [Fact]
    public async Task Dismissing_one_announcement_leaves_the_next_one_showing()
    {
        var a = Seed("dismiss-a-" + Guid.CreateVersion7());
        var b = Seed("dismiss-b-" + Guid.CreateVersion7());
        Panel.GivenUser(fixture.WorkspaceId, "announce-dismiss-one@example.com", SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.216", "announce-dismiss-one@example.com");

        var before = await client.GetStringAsync("/");
        before.Should().Contain($@"data-announcement=""{a.Id}""").And.Contain($@"data-announcement=""{b.Id}""");

        var token = await client.AntiforgeryTokenFrom("/");
        await client.PostFormAsync($"/announcements/{a.Id}/dismiss", token, ("returnUrl", "/"));

        var after = await client.GetStringAsync("/");
        after.Should().NotContain($@"data-announcement=""{a.Id}""",
            "dismissing A must not dismiss B — the bug this design exists to avoid");
        after.Should().Contain($@"data-announcement=""{b.Id}""");
    }

    [Fact]
    public async Task Dismissal_survives_navigating_to_a_different_page()
    {
        var a = Seed("dismiss-nav-" + Guid.CreateVersion7());
        Panel.GivenUser(fixture.WorkspaceId, "announce-dismiss-nav@example.com", SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.217", "announce-dismiss-nav@example.com");

        var token = await client.AntiforgeryTokenFrom("/");
        await client.PostFormAsync($"/announcements/{a.Id}/dismiss", token, ("returnUrl", "/"));

        // A second, independent request — not the same response the POST redirected — proving the
        // dismissal is a persisted row, not something that only lasted for the one round trip.
        var elsewhere = await client.GetStringAsync("/notifications");
        elsewhere.Should().NotContain($@"data-announcement=""{a.Id}""");
    }

    [Fact]
    public async Task Dismissal_does_not_leak_to_a_different_person()
    {
        var a = Seed("dismiss-leak-" + Guid.CreateVersion7());
        Panel.GivenUser(fixture.WorkspaceId, "announce-dismiss-mine@example.com", SystemRole.Member);
        Panel.GivenUser(fixture.WorkspaceId, "announce-dismiss-other@example.com", SystemRole.Member);

        var mine = await Panel.SignedInAs("203.0.113.218", "announce-dismiss-mine@example.com");
        var theirs = await Panel.SignedInAs("203.0.113.219", "announce-dismiss-other@example.com");

        var token = await mine.AntiforgeryTokenFrom("/");
        await mine.PostFormAsync($"/announcements/{a.Id}/dismiss", token, ("returnUrl", "/"));

        (await mine.GetStringAsync("/")).Should().NotContain($@"data-announcement=""{a.Id}""");
        (await theirs.GetStringAsync("/")).Should().Contain($@"data-announcement=""{a.Id}""",
            "one member closing their own banner must not close a colleague's copy of the same one");
    }

    [Fact]
    public async Task Dismissing_the_same_announcement_twice_does_not_throw_or_duplicate()
    {
        var a = Seed("dismiss-twice-" + Guid.CreateVersion7());
        Panel.GivenUser(fixture.WorkspaceId, "announce-dismiss-twice@example.com", SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.220", "announce-dismiss-twice@example.com");

        var token = await client.AntiforgeryTokenFrom("/");
        var first = await client.PostFormAsync($"/announcements/{a.Id}/dismiss", token, ("returnUrl", "/"));
        var token2 = await client.AntiforgeryTokenFrom("/");
        var second = await client.PostFormAsync($"/announcements/{a.Id}/dismiss", token2, ("returnUrl", "/"));

        first.StatusCode.Should().Be(HttpStatusCode.Found);
        second.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.AnnouncementDismissals.Count(d => d.AnnouncementId == a.Id)).Should().Be(1);
    }

    // --- Simple mode still shows announcements — not PanelMode material ----------------------------

    [Fact]
    public async Task Simple_mode_still_shows_the_banner()
    {
        var a = Seed("simple-mode-" + Guid.CreateVersion7());
        var user = Panel.GivenUser(fixture.WorkspaceId, "announce-simple@example.com", SystemRole.Member);
        Panel.Seed(db => db.Users.First(u => u.Id == user.Id).PanelMode = PanelMode.Simple);
        var client = await Panel.SignedInAs("203.0.113.221", "announce-simple@example.com");

        var html = await client.GetStringAsync("/");

        html.Should().Contain($@"data-announcement=""{a.Id}""",
            "operational information is never folded — do-not-change item 23 is about advanced material, not this");
    }

    // --- severity Warning fans out through the existing N3 in-app path -----------------------------

    [Fact]
    public async Task A_warning_severity_announcement_writes_an_in_app_row_for_a_workspace_member()
    {
        var tenant = new Workspace { Name = "announce-warn-fanout", Slug = "announce-warn-fanout" };
        Panel.Seed(db => db.Workspaces.Add(tenant));
        var member = Panel.GivenUser(tenant.Id, "announce-warn-member@example.com", SystemRole.Member);

        Panel.GivenUser(fixture.WorkspaceId, "announce-warn-admin@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.222", "announce-warn-admin@example.com");
        var token = await client.AntiforgeryTokenFrom("/announcements");

        var response = await client.PostFormAsync("/announcements/create", token,
            ("title", "Scheduled maintenance"), ("body", "The panel will be briefly unavailable."),
            ("titleFa", "تعمیرات برنامه‌ریزی‌شده"), ("bodyFa", "پنل به‌طور کوتاه در دسترس نخواهد بود."),
            ("severity", "Warning"));
        response.StatusCode.Should().Be(HttpStatusCode.Found);

        Panel.Read(db => db.UserNotifications.Any(n =>
                n.WorkspaceId == tenant.Id && n.UserId == member.Id && n.Severity == AlertSeverity.Warning))
            .Should().BeTrue("Warning severity reuses the existing N3 fan-out — every workspace's own members");
    }

    [Fact]
    public async Task An_info_severity_announcement_writes_no_in_app_row()
    {
        var tenant = new Workspace { Name = "announce-info-no-fanout", Slug = "announce-info-no-fanout" };
        Panel.Seed(db => db.Workspaces.Add(tenant));
        var member = Panel.GivenUser(tenant.Id, "announce-info-member@example.com", SystemRole.Member);

        Panel.GivenUser(fixture.WorkspaceId, "announce-info-admin@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.223", "announce-info-admin@example.com");
        var token = await client.AntiforgeryTokenFrom("/announcements");

        var response = await client.PostFormAsync("/announcements/create", token,
            ("title", "Just an FYI"), ("body", "Nothing urgent."),
            ("titleFa", "فقط یک اطلاع‌رسانی"), ("bodyFa", "چیز فوری‌ای نیست."),
            ("severity", "Info"));
        response.StatusCode.Should().Be(HttpStatusCode.Found);

        Panel.Read(db => db.UserNotifications.Any(n => n.WorkspaceId == tenant.Id && n.UserId == member.Id))
            .Should().BeFalse("Info stays banner-only — no in-app row for a workspace that never asked for one");
    }
}
