using System.Net;
using AngleSharp;
using AngleSharp.Dom;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Features;
using Harbora.Domain.Functions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Draft protection, split the way the task that asked for it split it: check what happens today
/// before adding anything.
///
/// <para>
/// The server half was already correct — <c>FunctionsController.TryBuildCandidate</c> re-renders
/// <c>EditFunction</c> from the posted <see cref="Harbora.Web.ViewModels.FunctionFormModel"/> on a
/// refused save, never by reloading the old row from the database, so the typed code was never
/// actually lost on a failed save. <see cref="A_function_that_fails_validation_keeps_the_authors_own_code_on_screen"/>
/// is the regression test that guarantee never had.
/// </para>
///
/// <para>
/// The real gap was leaving the page before saving at all — Cancel discarding silently, and Run now
/// (a submit of this same form, via <c>formaction</c>) navigating away without ever persisting the
/// buffer it does not run. That half is a <c>beforeunload</c> guard and a confirm on both exits,
/// which cannot be driven through <see cref="System.Net.Http.HttpClient"/> — there is no browser
/// here to fire those events — so this class asserts the server-rendered hooks the script depends
/// on: <c>[data-action="cancel"]</c> on the link the script attaches to, and that the code posted to
/// a failed save is exactly what comes back, not what the database still holds.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class FunctionDraftProtectionHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private sealed record World(Guid AppId, Guid FunctionId);

    private World GivenFunctionApp(string slug, string savedCode)
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
                FunctionRuntime = FunctionRuntime.JavaScript,
                DockerfilePath = "Dockerfile.harbora"
            });

            db.FunctionDefinitions.Add(new FunctionDefinition
            {
                Id = functionId, AppId = appId, WorkspaceId = fixture.WorkspaceId,
                Name = "Hello", Slug = "hello", Trigger = FunctionTrigger.Http, Code = savedCode
            });
        });

        return new World(appId, functionId);
    }

    private static async Task<IDocument> ParseAsync(string html) =>
        await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html));

    [Fact]
    public async Task A_function_that_fails_validation_keeps_the_authors_own_code_on_screen()
    {
        var world = GivenFunctionApp("fn-draft-fail", "export default async () => ({ hello: 'world' });");
        Panel.GivenUser(fixture.WorkspaceId, "fn-draft-fail@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.180", "fn-draft-fail@example.com");
        var token = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");

        var typedButUnsaved = "export default async () => ({ hello: 'a work in progress' });";

        // An empty cron expression on a Cron-triggered function is refused by
        // FunctionAppService.Validate — the failure path this class exists to prove does not cost
        // the buffer.
        var response = await client.PostFormAsync(
            $"/functions/{world.AppId}/save", token,
            ("functionId", world.FunctionId.ToString()),
            ("Name", "Hello"), ("Trigger", ((int)FunctionTrigger.Cron).ToString()),
            ("CronExpression", ""), ("Code", typedButUnsaved), ("IsEnabled", "true"));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a refused save re-renders the editor in place rather than redirecting");

        var document = await ParseAsync(await response.Content.ReadAsStringAsync());
        var textarea = document.QuerySelector("[data-island='function-code-editor'] textarea");
        textarea.Should().NotBeNull();
        textarea!.TextContent.Should().Be(typedButUnsaved,
            "the page must show back exactly what was typed, not the last value the database had");

        var stored = Panel.Read(db => db.FunctionDefinitions.AsNoTracking().First(f => f.Id == world.FunctionId));
        stored.Code.Should().Be("export default async () => ({ hello: 'world' });",
            "a refused save must not have written anything either");
    }

    [Fact]
    public async Task The_cancel_link_carries_the_hook_the_pages_draft_guard_attaches_to()
    {
        var world = GivenFunctionApp("fn-draft-cancel-hook", "export default async () => ({});");
        Panel.GivenUser(fixture.WorkspaceId, "fn-draft-cancel-hook@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.181", "fn-draft-cancel-hook@example.com");

        var html = await (await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}"))
            .Content.ReadAsStringAsync();
        var document = await ParseAsync(html);

        var cancel = document.QuerySelector("[data-action='cancel']");
        cancel.Should().NotBeNull("the inline script asks before discarding through this exact selector");
        cancel!.GetAttribute("href").Should().Be($"/functions/{world.AppId}",
            "with script off or blocked, Cancel must still be a plain, working link back to the app");
    }
}
