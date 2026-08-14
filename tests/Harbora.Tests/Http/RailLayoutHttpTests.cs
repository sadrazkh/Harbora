using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The owner's actual complaint: folding every panel in the right rail shrank the panels but left
/// the 20rem column reserved, so the list never got the space back. <c>_Layout.cshtml</c> now draws
/// the rail only when the page's <c>RightRail</c> section put something on the page — closing every
/// panel on a page like Apps, where the whole section is gated on whether one is open, empties it out.
///
/// <para>
/// Do-not-change item 23 is directly in the way of this: a fold that removes the only way back is not
/// a fold, it is a deletion. So every case here that closes every panel also checks that a control
/// which reopens it — outside the rail, in the list's own toolbar — is still on the page.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class RailLayoutHttpTests
{
    [Fact]
    public async Task With_every_rail_panel_closed_the_app_list_gets_the_whole_width()
    {
        await using var app = new HarboraWebFactory();
        var client = await app.SignedInOwnerAsync();
        await app.CloseEveryRailPanelAsync();

        var html = await client.GetStringAsync("/apps");

        html.Should().NotContain("2xl:w-80", "an empty reserved column is the whole complaint");
    }

    [Fact]
    public async Task A_closed_rail_still_offers_its_way_back_on_the_apps_list()
    {
        // Do-not-change item 23: a setting that makes a feature disappear entirely is one nobody finds
        // their way back from. The rail folds; it is not removed.
        await using var app = new HarboraWebFactory();
        var client = await app.SignedInOwnerAsync();
        await app.CloseEveryRailPanelAsync();

        var html = await client.GetStringAsync("/apps");

        // Reads the toolbar's own form rather than checking for the literal text "SetRail" — that
        // text never appears in the rendered page at all (asp-action compiles to an action="…" URL,
        // not the action name), so the old assertion was satisfied only by a since-removed
        // data-rail-action="SetRail" test hook with no consumer. This fails the way a deleted button
        // should: no match, not a string coincidence.
        var fields = RailTestHelpers.ReopenRailFieldsFrom(html);
        fields.Should().Contain("panel", "Overview", "the toolbar reopens the panel this list actually has");
        fields.Should().ContainKey("open").WhoseValue.Should().Be("true", "the control's whole job is to open the panel back up");
    }

    [Fact]
    public async Task With_every_rail_panel_closed_the_database_list_gets_the_whole_width()
    {
        await using var app = new HarboraWebFactory();
        var client = await app.SignedInOwnerAsync();
        await app.CloseEveryRailPanelAsync();

        var html = await client.GetStringAsync("/databases");

        html.Should().NotContain("2xl:w-80", "the same complaint applies wherever the rail is folded");
    }

    [Fact]
    public async Task A_closed_rail_still_offers_its_way_back_on_the_database_list()
    {
        await using var app = new HarboraWebFactory();
        var client = await app.SignedInOwnerAsync();
        await app.CloseEveryRailPanelAsync();

        var html = await client.GetStringAsync("/databases");

        // See A_closed_rail_still_offers_its_way_back_on_the_apps_list for why this reads the
        // toolbar's own form instead of checking for the literal text "SetRail".
        var fields = RailTestHelpers.ReopenRailFieldsFrom(html);
        fields.Should().Contain("panel", "QuickStart", "Databases only has the one panel to reopen");
        fields.Should().ContainKey("open").WhoseValue.Should().Be("true", "the control's whole job is to open the panel back up");
    }

    /// <summary>
    /// A user who has never touched the rail meets it closed now — Step 5 of the plan flips the
    /// shipped default. This is the case that actually answers the owner: nobody has to know a
    /// setting exists in order to get the wide list.
    /// </summary>
    [Fact]
    public async Task Somebody_who_never_touched_the_rail_gets_the_wide_list_by_default()
    {
        await using var app = new HarboraWebFactory();
        var client = await app.SignedInOwnerAsync();

        var html = await client.GetStringAsync("/apps");

        html.Should().NotContain("2xl:w-80", "the shipped default is closed, not open");
    }

    /// <summary>
    /// A user who explicitly opened a panel before this change keeps seeing it — the stored choice is
    /// never overridden by a change in the shipped default, the same rule <c>RailVisibility.Resolve</c>
    /// has always enforced for every other panel.
    /// </summary>
    [Fact]
    public async Task Somebody_who_already_opened_a_panel_still_sees_it()
    {
        await using var app = new HarboraWebFactory();
        var client = await app.SignedInOwnerAsync();
        app.Seed(db =>
        {
            var user = db.Users.IgnoreQueryFilters().Single();
            user.ShowOverview = true;
        });

        var html = await client.GetStringAsync("/apps");

        html.Should().Contain("2xl:w-80", "an explicit past choice survives the default flipping");
    }

    /// <summary>
    /// Not just a link that says it reopens the rail — actually posting the toolbar's own fields to
    /// <c>Account/SetRail</c> must widen the account's stored choice and bring the column back on the
    /// very next request, the same way clicking it in a browser would.
    /// </summary>
    [Fact]
    public async Task Clicking_the_toolbars_reopen_control_actually_reopens_the_rail()
    {
        await using var app = new HarboraWebFactory();
        var client = await app.SignedInOwnerAsync();
        await app.CloseEveryRailPanelAsync();

        var closed = await client.GetStringAsync("/apps");
        closed.Should().NotContain("2xl:w-80", "closed to begin with");

        // Read out of the page rather than hardcoded: a `panel` value that stopped binding (say,
        // Views/Apps/Index.cshtml:95 changed from "Overview" to something SetRail cannot parse)
        // would leave the real button dead while a hardcoded post here kept passing regardless.
        var fields = RailTestHelpers.ReopenRailFieldsFrom(closed);

        var token = await client.AntiforgeryTokenFrom("/apps");
        var reopen = await client.PostFormAsync("/account/rail", token,
            ("panel", fields["panel"]), ("open", fields["open"]), ("returnUrl", fields["returnUrl"]));
        reopen.StatusCode.Should().Be(HttpStatusCode.Found, "SetRail redirects back to returnUrl on success");

        var html = await client.GetStringAsync("/apps");

        html.Should().Contain("2xl:w-80", "the toolbar's control is a working way back, not just text claiming one exists");
    }

    /// <summary>
    /// The layout change in <c>_Layout.cshtml</c> reaches every page with a <c>RightRail</c> section,
    /// not only Apps and Databases. <c>Templates/Index.cshtml</c> draws its rail unconditionally — the
    /// private-template form lives there and nowhere else — so it must keep rendering exactly as
    /// before, regardless of what this account's Apps/Databases rail panels are set to. Those panels
    /// (<c>RailPanel.QuickStart</c>, <c>RailPanel.Overview</c>) have nothing to do with this page.
    /// </summary>
    [Fact]
    public async Task A_page_whose_rail_is_not_gated_by_any_panel_keeps_it_regardless()
    {
        await using var app = new HarboraWebFactory();
        var client = await app.SignedInOwnerAsync();
        await app.CloseEveryRailPanelAsync();

        var html = await client.GetStringAsync("/templates");

        html.Should().Contain("2xl:w-80", "Templates has no fold control at all — its rail is not this task's business");
        html.Should().Contain("name=\"manifestJson\"", "the private-template form is the only way to add one, and it lives in that rail");
    }
}

/// <summary>
/// A standalone panel per test, the same shape <c>SetupGuardHttpTests</c> uses — each case needs its
/// own account, and the shared <see cref="HarboraHttpFixture"/> panel would leak rail preferences
/// between tests in this class that all touch the same signed-in-owner shape.
/// </summary>
internal static class RailTestHelpers
{
    /// <summary>
    /// A freshly booted panel has no workspace and has never been set up — <see cref="HarboraHttpFixture"/>
    /// does that seeding for the shared collection panel, but these tests each own a panel of their own,
    /// the same reason <c>SetupGuardHttpTests</c> seeds by hand instead of using the fixture.
    /// </summary>
    public static async Task<HttpClient> SignedInOwnerAsync(this HarboraWebFactory panel)
    {
        var workspaceId = Guid.CreateVersion7();

        panel.Seed(db =>
        {
            var planId = db.Plans.Where(p => p.IsDefault).Select(p => p.Id).FirstOrDefault();

            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId,
                Name = "Harbora",
                Slug = "harbora-rail-" + workspaceId.ToString("N")[..8],
                IsDefault = true,
                PlanId = planId == Guid.Empty ? null : planId
            });

            db.Settings.Add(new Setting { Key = SettingKeys.SetupCompleted, Value = "true" });
        });

        panel.GivenUser(workspaceId, "rail-owner@example.com", SystemRole.Owner);
        return await panel.SignedInAs("203.0.113.201", "rail-owner@example.com");
    }

    /// <summary>
    /// Records an explicit "closed" for every rail panel, the way somebody who actually clicked every
    /// toggle would leave the account row — not merely relying on the shipped default, which is a
    /// separate thing this suite checks on its own.
    /// </summary>
    public static Task CloseEveryRailPanelAsync(this HarboraWebFactory panel)
    {
        panel.Seed(db =>
        {
            foreach (var user in db.Users.IgnoreQueryFilters())
            {
                user.ShowQuickStart = false;
                user.ShowOverview = false;
            }
        });
        return Task.CompletedTask;
    }

    private static readonly Regex ReopenForm = new(
        """<form[^>]*action="/account/rail"[^>]*>(?<body>[\s\S]*?)</form>""",
        RegexOptions.Compiled);

    private static readonly Regex HiddenField = new(
        """<input type="hidden" name="(?<name>[a-zA-Z]+)" value="(?<value>[^"]*)"\s*/>""",
        RegexOptions.Compiled);

    /// <summary>
    /// The toolbar's own way-back form (do-not-change item 23), read off the rendered page rather
    /// than assumed. A form is matched on its <c>action="/account/rail"</c> rather than on the text
    /// "SetRail" — the tag helper that builds that URL never writes the action name itself into the
    /// page, so a literal-text assertion could only ever be satisfied by an unrelated marker.
    /// Reused by every test that needs to know the control actually works: a value that drifted from
    /// what <c>AccountController.SetRail</c> expects fails here the same way a deleted button would.
    /// </summary>
    public static Dictionary<string, string> ReopenRailFieldsFrom(string html)
    {
        var form = ReopenForm.Match(html);
        form.Success.Should().BeTrue(
            "the toolbar must offer a form posting to /account/rail once the rail itself has nothing left to fold to");

        return HiddenField.Matches(form.Groups["body"].Value)
            .ToDictionary(m => m.Groups["name"].Value, m => m.Groups["value"].Value);
    }
}
