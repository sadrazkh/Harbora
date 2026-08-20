using System.Net;
using AngleSharp;
using AngleSharp.Dom;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Features;
using Harbora.Domain.Functions;
using Harbora.Domain.Networking;
using Harbora.Infrastructure.Functions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// F1's editor-side half: the Public toggle, rendered through a real request (the plan's own idiom —
/// AngleSharp over a <c>WebApplicationFactory</c> response, never a sentence match, per the panel's
/// Persian-by-default rendering in tests). The generated-host half — a protected function 401s an
/// unsigned visitor, a public one does not, and the panel's own signed door never changes — is pinned
/// by <see cref="FunctionProjectTests"/>'s text assertions over <c>FunctionProject.Generate</c>
/// output, the established idiom for a host nothing here can actually run (no Docker on this
/// machine). This class is the other half the WIP commit never reached: does the editor tell the
/// truth before the toggle is flipped, and does Save actually persist what was posted.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class FunctionPublicUrlHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private sealed record World(Guid AppId, Guid FunctionId);

    private World GivenFunctionApp(string slug, bool isPublic = false, string? host = null)
    {
        var appId = Guid.CreateVersion7();
        var functionId = Guid.CreateVersion7();

        Panel.Seed(db =>
        {
            db.FeatureGrants.Add(new FeatureGrant
            {
                Scope = FeatureScope.Workspace, TargetId = fixture.WorkspaceId,
                FeatureKey = PlatformFeatures.Functions, State = FeatureState.Enabled
            });

            db.Apps.Add(new App
            {
                Id = appId, WorkspaceId = fixture.WorkspaceId, EnvironmentId = fixture.DefaultEnvironmentId,
                Name = slug, Slug = slug, SourceType = AppSourceType.InlineCode,
                FunctionRuntime = FunctionRuntime.CSharp,
                DockerfilePath = "Dockerfile.harbora"
            });

            db.FunctionDefinitions.Add(new FunctionDefinition
            {
                Id = functionId, AppId = appId, WorkspaceId = fixture.WorkspaceId,
                Name = "Hello", Slug = "hello", Trigger = FunctionTrigger.Http,
                Code = "// code", IsPublic = isPublic
            });

            if (host is { Length: > 0 })
                db.Domains.Add(new DomainName { AppId = appId, Host = host, IsPrimary = true });
        });

        return new World(appId, functionId);
    }

    private static async Task<IDocument> ParseAsync(string html) =>
        await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html));

    private static Task<HttpResponseMessage> SaveAsync(
        HttpClient client, string token, World world, FunctionTrigger trigger, bool isPublic,
        string? cron = null, string? eventKey = null) =>
        client.PostFormAsync(
            $"/functions/{world.AppId}/save", token,
            ("functionId", world.FunctionId.ToString()),
            ("Name", "Hello"), ("Trigger", ((int)trigger).ToString()),
            ("Route", ""), ("CronExpression", cron ?? ""), ("EventKey", eventKey ?? ""),
            ("Code", "// code"), ("IsEnabled", "true"), ("IsPublic", isPublic ? "true" : "false"));

    // ------------------------------------------------------- the warning, always beside the toggle

    [Fact]
    public async Task A_new_functions_editor_shows_the_toggle_off_with_the_warning_beside_it()
    {
        var world = GivenFunctionApp("fn-pub-default");
        Panel.GivenUser(fixture.WorkspaceId, "fn-pub-default@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.160", "fn-pub-default@example.com");

        var document = await ParseAsync(
            await (await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}")).Content.ReadAsStringAsync());

        var toggle = document.QuerySelector("[data-public-toggle]");
        toggle.Should().NotBeNull("the editor must render a way to open the public door");
        toggle!.HasAttribute("checked").Should().BeFalse(
            "a function must start exactly as closed as it was before this flag existed");

        document.QuerySelector("[data-public-warning]").Should().NotBeNull(
            "the honest copy must be present even while the toggle is still off — read before flipping it, not after");
    }

    [Fact]
    public async Task The_warning_is_present_whether_the_function_is_already_public_or_not()
    {
        var world = GivenFunctionApp("fn-pub-warn-both", isPublic: true, host: "warn-both.example.test");
        Panel.GivenUser(fixture.WorkspaceId, "fn-pub-warn-both@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.161", "fn-pub-warn-both@example.com");

        var document = await ParseAsync(
            await (await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}")).Content.ReadAsStringAsync());

        document.QuerySelector("[data-public-toggle]")!.HasAttribute("checked").Should().BeTrue();
        document.QuerySelector("[data-public-warning]").Should().NotBeNull(
            "the same honest sentence belongs next to the toggle in either state, not only the first time");
    }

    // ------------------------------------------------------------------- the copy-ready URL itself

    [Fact]
    public async Task A_public_functions_editor_shows_its_exact_url_when_the_app_has_a_host()
    {
        var world = GivenFunctionApp("fn-pub-url", isPublic: true, host: "fn-pub-url.example.test");
        Panel.GivenUser(fixture.WorkspaceId, "fn-pub-url@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.162", "fn-pub-url@example.com");

        var document = await ParseAsync(
            await (await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}")).Content.ReadAsStringAsync());

        var code = document.QuerySelector("[data-function-url] code");
        code.Should().NotBeNull();
        // The exact text RouteFor + the app's own recorded host would produce — never composed by
        // hand, so the page can never show an address the generated host would not itself answer on.
        code!.TextContent.Trim().Should().Be("https://fn-pub-url.example.test/hello");
    }

    [Fact]
    public async Task A_protected_functions_editor_shows_no_url_box_at_all()
    {
        var world = GivenFunctionApp("fn-pub-hidden", isPublic: false, host: "fn-pub-hidden.example.test");
        Panel.GivenUser(fixture.WorkspaceId, "fn-pub-hidden@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.163", "fn-pub-hidden@example.com");

        var document = await ParseAsync(
            await (await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}")).Content.ReadAsStringAsync());

        document.QuerySelector("[data-function-url]").Should().BeNull(
            "there is nothing to copy for a function nobody outside the panel can reach");
    }

    [Fact]
    public async Task A_public_function_with_no_host_yet_says_the_link_is_pending_not_a_broken_url()
    {
        var world = GivenFunctionApp("fn-pub-nohost", isPublic: true, host: null);
        Panel.GivenUser(fixture.WorkspaceId, "fn-pub-nohost@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.164", "fn-pub-nohost@example.com");

        var document = await ParseAsync(
            await (await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}")).Content.ReadAsStringAsync());

        document.QuerySelector("[data-function-url]").Should().BeNull();
        document.QuerySelector("[data-function-url-pending]").Should().NotBeNull(
            "an app with no address yet must say so plainly, not show a link that cannot work");
    }

    [Fact]
    public async Task A_public_but_unpublished_functions_url_says_it_answers_nothing_yet()
    {
        var world = GivenFunctionApp("fn-pub-unpub", isPublic: true, host: "fn-pub-unpub.example.test");
        Panel.GivenUser(fixture.WorkspaceId, "fn-pub-unpub@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.165", "fn-pub-unpub@example.com");

        var document = await ParseAsync(
            await (await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}")).Content.ReadAsStringAsync());

        document.QuerySelector("[data-function-url]").Should().NotBeNull();
        document.QuerySelector("[data-function-url-not-live]").Should().NotBeNull(
            "the address is shown copy-ready before publish, but it must not be mistaken for already live");
    }

    // ------------------------------------------------------ public-call history points at the logs

    [Fact]
    public async Task A_public_functions_editor_points_at_the_apps_logs_for_call_history()
    {
        var world = GivenFunctionApp("fn-pub-logs", isPublic: true, host: "fn-pub-logs.example.test");
        Panel.GivenUser(fixture.WorkspaceId, "fn-pub-logs@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.166", "fn-pub-logs@example.com");

        var document = await ParseAsync(
            await (await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}")).Content.ReadAsStringAsync());

        var note = document.QuerySelector("[data-public-history-note]");
        note.Should().NotBeNull(
            "the panel never observes a public call, so it must say where the history actually lives rather than leave the question unanswered");
        var link = note!.QuerySelector("a");
        link.Should().NotBeNull();
        link!.GetAttribute("href").Should().Be($"/apps/{world.AppId}/logs",
            "the app's own Logs tab is what already searches these calls — the note must point at the real place, not a stub");
    }

    [Fact]
    public async Task A_protected_functions_editor_carries_no_public_history_note()
    {
        var world = GivenFunctionApp("fn-pub-no-note", isPublic: false, host: "fn-pub-no-note.example.test");
        Panel.GivenUser(fixture.WorkspaceId, "fn-pub-no-note@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.167", "fn-pub-no-note@example.com");

        var document = await ParseAsync(
            await (await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}")).Content.ReadAsStringAsync());

        document.QuerySelector("[data-public-history-note]").Should().BeNull(
            "a protected function's calls already show up in Recent runs — this note is only true once a call can bypass that");
    }

    // --------------------------------------------------------------------------------- saving it

    [Fact]
    public async Task Saving_the_toggle_on_for_an_http_function_persists_it_and_marks_the_app_unpublished()
    {
        var world = GivenFunctionApp("fn-pub-save-on");
        Panel.Seed(db => db.Apps.First(a => a.Id == world.AppId).ActiveDeploymentId = Guid.CreateVersion7());
        Panel.GivenUser(fixture.WorkspaceId, "fn-pub-save-on@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.168", "fn-pub-save-on@example.com");
        var token = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");

        var response = await SaveAsync(client, token, world, FunctionTrigger.Http, isPublic: true);
        response.StatusCode.Should().Be(HttpStatusCode.Found);

        var fn = Panel.Read(db => db.FunctionDefinitions.AsNoTracking().First(f => f.Id == world.FunctionId));
        fn.IsPublic.Should().BeTrue("the posted toggle must actually reach the row Save writes");
        // Exposure is a property of the deployed image — the same whole-app dirtying every other
        // function edit already causes (FunctionAppService.MarkDirtyAsync), reused rather than
        // reinvented.
        fn.HasUnpublishedChanges.Should().BeTrue(
            "flipping exposure ships on publish like every other function change");
    }

    [Fact]
    public async Task Saving_the_toggle_on_for_a_cron_function_is_forced_back_off()
    {
        var world = GivenFunctionApp("fn-pub-save-cron");
        Panel.GivenUser(fixture.WorkspaceId, "fn-pub-save-cron@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.169", "fn-pub-save-cron@example.com");
        var token = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");

        var response = await SaveAsync(
            client, token, world, FunctionTrigger.Cron, isPublic: true, cron: "0 3 * * *");
        response.StatusCode.Should().Be(HttpStatusCode.Found);

        var fn = Panel.Read(db => db.FunctionDefinitions.AsNoTracking().First(f => f.Id == world.FunctionId));
        fn.IsPublic.Should().BeFalse(
            "a Cron function never sits behind the visitor route this flag gates, so posting it true must not stick");
    }

    [Fact]
    public async Task Turning_the_toggle_back_off_persists_too()
    {
        var world = GivenFunctionApp("fn-pub-save-off", isPublic: true, host: "fn-pub-save-off.example.test");
        Panel.GivenUser(fixture.WorkspaceId, "fn-pub-save-off@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.170", "fn-pub-save-off@example.com");
        var token = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");

        var response = await SaveAsync(client, token, world, FunctionTrigger.Http, isPublic: false);
        response.StatusCode.Should().Be(HttpStatusCode.Found);

        var fn = Panel.Read(db => db.FunctionDefinitions.AsNoTracking().First(f => f.Id == world.FunctionId));
        fn.IsPublic.Should().BeFalse("closing the door back must persist exactly as opening it does");
    }
}
