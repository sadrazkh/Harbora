using System.Net;
using AngleSharp;
using AngleSharp.Dom;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Sub-project 12's "last drill" surface, rendered through a real request against
/// <c>/admin/settings</c>. The panel renders Persian by default in tests, so every assertion below
/// reads the <c>data-dr-drill-status</c>/<c>data-dr-drill-stale</c> attributes the view writes
/// (<c>Views/AdminSettings/Index.cshtml</c>) rather than a sentence — the same rule every other HTTP
/// test in this suite follows.
///
/// <para>
/// <c>Dr*</c> is a platform-wide singleton, not a per-workspace row like most of what this shared
/// fixture holds — so every test here clears it before establishing its own state
/// (<see cref="SeedDrillStateAsync"/>), rather than assuming a blank database. Two tests in this
/// class racing to <c>Add</c> the same key against the fixture's one shared database is exactly the
/// collision <see cref="RestoreDrillRecord.WriteAsync"/> itself is written to avoid with an upsert —
/// the seeding here has to follow the same rule for the same reason.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AdminSettingsDrillHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;
    private const string Path = "/admin/settings";

    private static async Task<IDocument> ParseAsync(string html) =>
        await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html));

    /// <summary>Clears any drill state a sibling test left behind, then writes exactly this one's.</summary>
    private void SeedDrillState(DateTimeOffset? at, string? verdict, string? detail)
    {
        Panel.Seed(db =>
        {
            var stale = db.Settings.IgnoreQueryFilters().Where(s =>
                s.Key == SettingKeys.DrLastDrillAt || s.Key == SettingKeys.DrLastDrillVerdict
                || s.Key == SettingKeys.DrLastDrillDetail);
            db.Settings.RemoveRange(stale);

            if (at is not null)
                db.Settings.Add(new Setting { Key = SettingKeys.DrLastDrillAt, Value = at.Value.ToString("O") });
            if (verdict is not null)
                db.Settings.Add(new Setting { Key = SettingKeys.DrLastDrillVerdict, Value = verdict });
            if (detail is not null)
                db.Settings.Add(new Setting { Key = SettingKeys.DrLastDrillDetail, Value = detail });
        });
    }

    [Fact]
    public async Task No_drill_ever_recorded_shows_the_honest_never_run_state()
    {
        SeedDrillState(at: null, verdict: null, detail: null);

        var email = "dr-never-" + Guid.NewGuid().ToString("N")[..8] + "@example.com";
        Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.160", email);

        var response = await client.GetAsync(Path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var document = await ParseAsync(await response.Content.ReadAsStringAsync());
        var surface = document.QuerySelector("[data-dr-drill-status]");

        surface.Should().NotBeNull("the admin settings page must always render the drill surface, even with nothing to show");
        surface!.GetAttribute("data-dr-drill-status").Should().Be("never",
            "no Setting row exists for this test's run — that must read as \"never run\", not a fabricated pass or a blank");
    }

    [Fact]
    public async Task A_recorded_pass_renders_the_pass_state_with_its_date()
    {
        SeedDrillState(
            at: DateTimeOffset.UtcNow.AddDays(-2), verdict: "pass",
            detail: "restored manual-x.sql.gz — 5 migrations, 3 workspaces");

        var email = "dr-pass-" + Guid.NewGuid().ToString("N")[..8] + "@example.com";
        Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.161", email);

        var response = await client.GetAsync(Path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var document = await ParseAsync(await response.Content.ReadAsStringAsync());
        var surface = document.QuerySelector("[data-dr-drill-status]");

        surface.Should().NotBeNull();
        surface!.GetAttribute("data-dr-drill-status").Should().Be("pass");
        surface.GetAttribute("data-dr-drill-stale").Should().Be("false", "two days old is well inside the 30-day window");
    }

    [Fact]
    public async Task A_recorded_fail_renders_the_fail_state_rather_than_being_hidden()
    {
        SeedDrillState(
            at: DateTimeOffset.UtcNow, verdict: "fail", detail: "no backup found: no *.sql.gz file");

        var email = "dr-fail-" + Guid.NewGuid().ToString("N")[..8] + "@example.com";
        Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.162", email);

        var response = await client.GetAsync(Path);

        var document = await ParseAsync(await response.Content.ReadAsStringAsync());
        var surface = document.QuerySelector("[data-dr-drill-status]");

        surface!.GetAttribute("data-dr-drill-status").Should().Be("fail");
    }

    [Fact]
    public async Task A_drill_older_than_30_days_is_marked_stale()
    {
        SeedDrillState(at: DateTimeOffset.UtcNow.AddDays(-45), verdict: "pass", detail: null);

        var email = "dr-stale-" + Guid.NewGuid().ToString("N")[..8] + "@example.com";
        Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.163", email);

        var response = await client.GetAsync(Path);

        var document = await ParseAsync(await response.Content.ReadAsStringAsync());
        var surface = document.QuerySelector("[data-dr-drill-status]");

        surface!.GetAttribute("data-dr-drill-stale").Should().Be("true",
            "45 days is past the 30-day window RestoreDrillRecord.StaleAfter defines");
    }

    [Fact]
    public async Task A_workspace_member_cannot_reach_the_platform_settings_page_at_all()
    {
        // The drill surface lives on /admin/settings, which is platform-admin-only end to end — this
        // proves the whole page (not just the drill panel) still refuses an ordinary member, the same
        // as every other platform-only page in this controller.
        var email = "dr-member-" + Guid.NewGuid().ToString("N")[..8] + "@example.com";
        Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.164", email);

        var response = await client.GetAsync(Path);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/denied");
    }
}
