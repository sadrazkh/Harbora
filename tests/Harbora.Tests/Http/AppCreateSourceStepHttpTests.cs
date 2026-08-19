using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Templates;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The 2026-08-19 create-app redesign: the template catalogue and the "ready-app image addresses"
/// list used to sit above the form as their own region, followed by a divider, before the form's own
/// "Deployment source" step began a second time. They now fold into that same step — one place to
/// answer "what do I deploy?" — so this proves the merge landed rather than just moved the divider.
///
/// <para>
/// Asserted on markup and <c>data-</c>/route fragments rather than sentences: this panel renders
/// Persian by default in this harness (see <c>SizePickerHttpTests</c>), so an English string is not
/// what actually comes back, and Razor writes the Persian out as numeric character references rather
/// than the literal words either.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AppCreateSourceStepHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private async Task<string> CreateFormAsync(string who, string ip)
    {
        Panel.GivenUser(fixture.WorkspaceId, who, SystemRole.Owner);
        var client = await Panel.SignedInAs(ip, who);
        return await (await client.GetAsync("/apps/create")).Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Every_source_type_still_lives_in_the_same_step_the_catalogue_now_shares()
    {
        var html = await CreateFormAsync("create-source-step@example.com", "203.0.113.80");

        // The anchor Apps/Index's "Browse all create options" rail link still points at
        // /apps/create#quick-start — it now has to land on the merged step, not a narrower catalogue
        // region that no longer exists on its own.
        html.Should().Contain("id=\"quick-start\"");

        // Every source type reachable today must still be reachable — none dropped by the reorg.
        foreach (var source in new[]
                 { "GitRepository", "PrebuiltImage", "DockerCompose", "StaticSite", "Upload" })
        {
            html.Should().Contain($"value=\"{source}\"", $"{source} must still be choosable");
        }

        // The catalogue and the manual picker are now one region: both markers appear, and the
        // radio-card grid is not off in a <form> of its own the way the pre-redesign page had it.
        html.Should().Contain("class=\"source-card-grid\"");

        // The full-width divider that used to separate "catalogue" from "form" is gone — what
        // replaced it lives inside the merged step instead of standing between two regions.
        html.Should().NotContain("class=\"create-divider\"");

        // Anchor and radio grid have to be the *same* step, inside the *same* form: the pre-redesign
        // page opened the catalogue above the form entirely, so proving no </form> (or a second
        // <form>) sits between the two markers is what rules that shape back out.
        var anchorAt = html.IndexOf("id=\"quick-start\"", StringComparison.Ordinal);
        var gridAt = html.IndexOf("class=\"source-card-grid\"", StringComparison.Ordinal);
        anchorAt.Should().BeGreaterThan(-1);
        gridAt.Should().BeGreaterThan(anchorAt, "the anchor must sit on or before the step it marks");
        var betweenAnchorAndGrid = html[anchorAt..gridAt];
        Regex.Matches(betweenAnchorAndGrid, "</?form\\b").Count.Should().Be(0,
            "the catalogue and the source picker must be inside one already-open form, not stacked before it");
        html[..anchorAt].Should().Contain("<form ",
            "the merged step must be inside the form, not ahead of where it opens");
    }

    [Fact]
    public async Task The_source_specific_fields_render_visible_with_no_script_to_reveal_them()
    {
        // Progressive enhancement: only client script hides the panels that do not match the checked
        // radio (see the page's own `sync()`). Server-rendered HTML must not pre-hide any of them —
        // otherwise a browser with JavaScript off shows a form with fields it can never reach.
        var html = await CreateFormAsync("create-source-noscript@example.com", "203.0.113.81");

        foreach (var marker in new[]
                 {
                     "data-panel=\"GitRepository,StaticSite,DockerCompose,Dockerfile\"",
                     "data-panel=\"PrebuiltImage\"",
                     "data-panel=\"Upload\""
                 })
        {
            var at = html.IndexOf(marker, StringComparison.Ordinal);
            at.Should().BeGreaterThan(-1, $"{marker} must still be on the page");

            // Look at the enclosing tag's class attribute, not the whole document, so a `hidden`
            // class somewhere unrelated on the page cannot produce a false pass.
            var tagStart = html.LastIndexOf('<', at);
            var tagEnd = html.IndexOf('>', at);
            var tag = html[tagStart..tagEnd];
            tag.Should().NotContain("hidden",
                $"{marker}'s element must render visible; only script may fold it");
        }
    }

    [Fact]
    public async Task A_templates_image_address_is_reachable_from_a_disclosure_inside_the_same_step()
    {
        // The "Ready-app image addresses" region used to be a whole section of its own, between the
        // template grid and the divider that led into the form. It is now a plain <details> folded
        // under the template grid, in the same step — so its content (this template's image tag)
        // still has to be somewhere on the page, and specifically between the template grid and the
        // manual source-card grid rather than off in its own top-level section again.
        Panel.Seed(db =>
        {
            db.AppTemplates.Add(new AppTemplate
            {
                Key = "catalog-disclosure-app",
                Name = "Catalog Disclosure App",
                Category = "app",
                IsEnabled = true,
                WorkspaceId = null, // a platform template — visible to every workspace regardless of Status
                ManifestJson = """{"image":"demo/catalog-disclosure-app:9.9","port":8080}"""
            });
            // The featured-card slots are shared with whatever the built-in catalogue already ships
            // (curated real templates, seeded once for the whole test process) — six of those could
            // easily crowd this test's own template out of the top six on name alone. Pinning the
            // operator's chosen order to just this key is what the real "Featured Templates" admin
            // setting is for, and makes which card shows up deterministic instead of a name-sort bet
            // against a catalogue this test does not own.
            db.Settings.Add(new Harbora.Domain.Settings.Setting
            {
                Key = Harbora.Domain.Settings.SettingKeys.FeaturedTemplates,
                Value = "catalog-disclosure-app"
            });
        });

        var html = await CreateFormAsync("create-source-disclosure@example.com", "203.0.113.82");

        html.Should().Contain("demo/catalog-disclosure-app:9.9",
            "the image tag this template's manifest declares must still be reachable to copy");

        var gridAt = html.IndexOf("class=\"create-template-grid\"", StringComparison.Ordinal);
        var imageAt = html.IndexOf("demo/catalog-disclosure-app:9.9", StringComparison.Ordinal);
        var sourceGridAt = html.IndexOf("class=\"source-card-grid\"", StringComparison.Ordinal);
        gridAt.Should().BeGreaterThan(-1);
        imageAt.Should().BeInRange(gridAt, sourceGridAt,
            "the image address belongs between the template grid and the manual source picker, " +
            "inside the same step 1 — not before the templates or after the manual source cards");
    }
}
