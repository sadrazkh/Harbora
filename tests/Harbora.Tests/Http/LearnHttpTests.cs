using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Settings;
using Harbora.Infrastructure.Learning;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The Learning Centre's two screens and its guarded image route, over real HTTP.
///
/// <para>
/// The panel renders Persian by default in this harness, so every assertion below reads a route
/// fragment, a <c>data-</c> attribute or a file name — never a sentence — the same discipline
/// <see cref="AppAddressHttpTests"/> and <see cref="DeploymentsRollbackDepthHttpTests"/> already use.
/// Chapter titles are read fresh from <c>docs/tutorial</c> through <see cref="LearningLibrary"/> rather
/// than kept as a literal here, for the same reason <c>LearningLibraryTests</c> does: a hard-coded copy
/// is the thing this suite exists to catch drifting.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class LearnHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;
    private static LearningLibrary RealLibrary() => new(TestPaths.DocsRoot);

    private static readonly Regex ChapterCardCount = new("data-chapter-slug=\"", RegexOptions.Compiled);

    /// <summary>Minimal app row — the same shape <c>AppAddressHttpTests.SeedApp</c> uses, without a
    /// domain this suite never looks at.</summary>
    private Guid SeedApp(string slug)
    {
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = slug,
            Slug = slug,
            Kind = ServiceKind.Web,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Running
        };

        Panel.Seed(db => db.Apps.Add(app));
        return app.Id;
    }

    [Fact]
    public async Task An_unauthenticated_visitor_is_sent_to_sign_in_rather_than_shown_the_chapters()
    {
        var client = Panel.ClientFrom("203.0.113.220");

        var response = await client.GetAsync("/learn");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/login");
    }

    [Fact]
    public async Task The_index_lists_every_chapter_on_disk()
    {
        Panel.GivenUser(fixture.WorkspaceId, "learn-index@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.221", "learn-index@example.com");
        var expected = RealLibrary().Chapters();

        var html = await (await client.GetAsync("/learn")).Content.ReadAsStringAsync();

        ChapterCardCount.Matches(html).Count.Should().Be(expected.Count,
            "the index must offer exactly the chapters LearningLibrary found on disk, not a fixed count");
        foreach (var chapter in expected)
            html.Should().Contain($"data-chapter-slug=\"{chapter.Slug}\"",
                $"chapter {chapter.Slug} is on disk and must be reachable from the index");
    }

    [Fact]
    public async Task A_chapter_page_renders_its_own_heading_and_is_marked_by_its_own_slug()
    {
        Panel.GivenUser(fixture.WorkspaceId, "learn-chapter@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.222", "learn-chapter@example.com");
        var chapter = RealLibrary().Chapters().Single(c => c.Number == 3);

        var response = await client.GetAsync($"/learn/{chapter.Slug}");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain($"data-chapter-slug=\"{chapter.Slug}\"");
        html.Should().Contain(chapter.Title, "the rendered page still carries the chapter's own heading text");
    }

    [Fact]
    public async Task A_chapter_that_does_not_exist_answers_404_and_still_offers_the_index()
    {
        Panel.GivenUser(fixture.WorkspaceId, "learn-missing@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.223", "learn-missing@example.com");

        var response = await client.GetAsync("/learn/no-such-chapter");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "ReadAsync answers null for a slug nothing on disk matches, and the controller must turn " +
            "that into a 404 rather than let a NullReferenceException reach the generic error handler");
        html.Should().Contain("data-learn-chapter-missing",
            "not the platform's generic error page (which only offers the dashboard) — this one offers the index");
        html.Should().Contain("href=\"/learn\"",
            "the honest answer to a chapter that is not there is a way back to the ones that are");
    }

    [Fact]
    public async Task A_raw_capture_name_404s_through_the_guarded_image_route_even_though_it_never_says_forbidden()
    {
        Panel.GivenUser(fixture.WorkspaceId, "learn-raw-image@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.224", "learn-raw-image@example.com");

        // Not ".annotated.png" — MayServeImage refuses it purely on the name, independent of whether
        // a raw file by this name happens to sit in a developer's own working directory.
        var response = await client.GetAsync("/learn/img/01-dashboard.png");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a 403 would confirm a file by this name exists to be forbidden — the same reasoning the " +
            "image guard itself documents (LearningLibrary.MayServeImage)");
    }

    [Fact]
    public async Task An_annotated_capture_is_served_as_an_image()
    {
        Panel.GivenUser(fixture.WorkspaceId, "learn-image@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.225", "learn-image@example.com");

        var response = await client.GetAsync("/learn/img/01-dashboard.annotated.png");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        bytes.Should().NotBeEmpty();
    }

    /// <summary>
    /// The property Markdig's <c>DisableHtml()</c> exists for: a chapter is trusted content in the
    /// repository today, but the render path outlives that assumption. Needs a chapter this test
    /// controls rather than one of the real nine, so it boots a panel of its own pointed at a temporary
    /// chapters directory — the same reason <c>DeploymentsRollbackDepthHttpTests</c> boots one of its
    /// own for a configured <c>ImageRetentionCount</c> rather than reusing the shared fixture's.
    /// </summary>
    [Fact]
    public async Task Markup_embedded_in_a_chapter_is_shown_as_text_rather_than_executed()
    {
        var tempChapters = Directory.CreateTempSubdirectory("harbora-learn-chapter-");
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempChapters.FullName, "01-injected.md"),
                "# Injected chapter\n\n" +
                "<script>window.__harboraChapterScriptRan = true;</script>\n\n" +
                "Ordinary paragraph text.\n");

            await using var scripted = new HarboraWebFactory(learningChaptersRoot: tempChapters.FullName);
            var workspaceId = Guid.CreateVersion7();
            scripted.Seed(db =>
            {
                db.Workspaces.Add(new Workspace
                {
                    Id = workspaceId, Name = "Harbora", Slug = "harbora-learn-xss", IsDefault = true
                });
                db.Settings.Add(new Setting { Key = SettingKeys.SetupCompleted, Value = "true" });
            });
            scripted.GivenUser(workspaceId, "learn-xss@example.com", SystemRole.Owner);
            var client = await scripted.SignedInAs("203.0.113.226", "learn-xss@example.com");

            var html = await (await client.GetAsync("/learn/01-injected")).Content.ReadAsStringAsync();

            // Not a page-wide NotContain("<script>") — the panel's own shell legitimately carries
            // several (main.ts, the theme toggle) that have nothing to do with the chapter. What must
            // never appear is THIS script, live: DisableHtml() must have turned it into text.
            html.Should().NotContain("<script>window.__harboraChapterScriptRan",
                "DisableHtml() must strip raw markup rather than pass it through — a chapter is " +
                "trusted content today, but the render path outlives that assumption");
            html.Should().Contain("&lt;script&gt;window.__harboraChapterScriptRan",
                "escaped to text is the expected outcome, not silently dropped — proving the markup " +
                "survived as inert content rather than vanishing for an unrelated reason");
            html.Should().Contain("Injected chapter", "the rest of the chapter still renders normally");
        }
        finally
        {
            tempChapters.Delete(recursive: true);
        }
    }

    // ---- the topbar's Help control (HelpMap) ----

    [Fact]
    public async Task An_app_page_offers_the_help_control_for_the_applications_chapter()
    {
        var appId = SeedApp("learn-help-app-page");
        Panel.GivenUser(fixture.WorkspaceId, "learn-help-app@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.227", "learn-help-app@example.com");
        var expectedSlug = HelpMap.ChapterFor($"/apps/details/{appId}");
        expectedSlug.Should().NotBeNull("an app page is exactly the screen HelpMap exists to answer for");

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-help-state=\"chapter\"",
            "a mapped screen's Help control must say so rather than looking identical to an unmapped one");
        html.Should().Contain($"href=\"/learn/{expectedSlug}\"",
            "the Help control must open the chapter HelpMap actually resolved for this request path");
    }

    /// <summary>
    /// The mechanism the whole sub-project rests on, proven over real HTTP rather than only against
    /// <see cref="HelpMap.ChapterFor"/> directly: the volumes tab and the rest of the app page share
    /// the <c>/apps</c> prefix but must not share a Help target, or "longest prefix wins" would be
    /// true of the map and false of the control built on it.
    /// </summary>
    [Fact]
    public async Task The_volumes_tab_offers_a_different_help_chapter_than_the_rest_of_the_app_page()
    {
        var appId = SeedApp("learn-help-app-volumes");
        Panel.GivenUser(fixture.WorkspaceId, "learn-help-volumes@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.228", "learn-help-volumes@example.com");
        var overviewSlug = HelpMap.ChapterFor($"/apps/details/{appId}");
        var volumesSlug = HelpMap.ChapterFor($"/apps/{appId}/volumes");
        volumesSlug.Should().NotBe(overviewSlug, "otherwise this test would not be exercising longest-prefix at all");

        var html = await (await client.GetAsync($"/apps/{appId}/volumes")).Content.ReadAsStringAsync();

        html.Should().Contain($"href=\"/learn/{volumesSlug}\"",
            "the volumes tab's own screen, not the app page's overview, decides which chapter is offered");
    }

    [Fact]
    public async Task A_screen_with_no_mapped_chapter_opens_the_index_and_says_so()
    {
        Panel.GivenUser(fixture.WorkspaceId, "learn-help-unmapped@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.229", "learn-help-unmapped@example.com");
        // /workspaces carries no HelpMap entry (LearningCensusTests pins that directly) — the honest
        // gap this test exists to prove the control handles without 404ing or opening chapter one.
        HelpMap.ChapterFor("/workspaces").Should().BeNull();

        var html = await (await client.GetAsync("/workspaces")).Content.ReadAsStringAsync();

        html.Should().Contain("data-help-state=\"index\"",
            "an unmapped screen's Help control must mark itself as the index fallback, not look like a hit");
        html.Should().Contain("href=\"/learn\"",
            "the honest answer for a screen with no chapter is the index, not a guess at the closest one");
    }
}
