using System.Net;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Features;
using Harbora.Domain.Functions;
using Harbora.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The editor page, rendered through a real request — the lane that never existed before this defect
/// was traced (doc <c>2026-08-18-functions-design.md</c> §3a). <c>EditFunction.cshtml</c> nested a
/// second <c>&lt;form&gt;</c> inside the one Save opens; a spec-compliant HTML parser silently drops a
/// form nested inside another, so both buttons ended up posting to Save and Run now answered "Saved.
/// Press Publish to make it live." No assertion over the Razor source could ever have caught that —
/// the inner <c>&lt;form&gt;</c> tag genuinely is in the file — which is why this class parses the
/// rendered DOM with AngleSharp instead, the idiom doc 9 of the spec asks for.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class FunctionEditorHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private sealed record World(Guid AppId, Guid FunctionId);

    /// <summary>
    /// A function app with one HTTP function, in a workspace actually entitled to Functions — the
    /// grant is workspace-scoped so it does not leak into any other test sharing this fixture's plan.
    /// </summary>
    private World GivenFunctionApp(string slug)
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
                Name = "Hello", Slug = "hello", Trigger = FunctionTrigger.Http,
                Code = "export default async () => ({ hello: 'world' });"
            });
        });

        return new World(appId, functionId);
    }

    private static async Task<IDocument> ParseAsync(string html) =>
        await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html));

    /// <summary>An element's effective submit target: its own <c>formaction</c> if it carries one,
    /// otherwise its owning form's <c>action</c> — the same resolution a browser performs when
    /// deciding where a click on this button goes.</summary>
    private static string? EffectiveAction(IElement button) =>
        button.GetAttribute("formaction") is { Length: > 0 } overridden
            ? overridden
            : (button as IHtmlButtonElement)?.Form?.GetAttribute("action");

    [Fact]
    public async Task The_save_and_run_now_buttons_submit_to_different_actions()
    {
        var world = GivenFunctionApp("fn-dom-owner");
        Panel.GivenUser(fixture.WorkspaceId, "fn-dom-owner@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.150", "fn-dom-owner@example.com");

        var response = await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var document = await ParseAsync(await response.Content.ReadAsStringAsync());

        // Exactly one <form> is what a browser actually builds from this page — the historical bug
        // was never that a second one was missing from the file, it was that the parser throws a
        // nested one away, so counting forms would not have caught it. What has to differ is where
        // each button's own submit goes.
        var save = document.QuerySelector("[data-action='save']");
        var run = document.QuerySelector("[data-action='run']");
        save.Should().NotBeNull("the editor must render a Save control");
        run.Should().NotBeNull("the editor must render a Run now control for an existing function");

        var saveAction = EffectiveAction(save!);
        var runAction = EffectiveAction(run!);

        saveAction.Should().Be($"/functions/{world.AppId}/save");
        runAction.Should().Be($"/functions/{world.AppId}/{world.FunctionId}/run");
        runAction.Should().NotBe(saveAction,
            "pressing Run now must not silently submit to the same place Save does");
    }

    [Fact]
    public async Task Pressing_run_now_saves_the_buffer_and_starts_a_deployment_that_will_run_it()
    {
        var world = GivenFunctionApp("fn-run-owner");
        Panel.GivenUser(fixture.WorkspaceId, "fn-run-owner@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.151", "fn-run-owner@example.com");

        var token = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");

        var response = await client.PostFormAsync(
            $"/functions/{world.AppId}/{world.FunctionId}/run", token,
            ("functionId", world.FunctionId.ToString()),
            ("Name", "Hello"), ("Trigger", ((int)FunctionTrigger.Http).ToString()),
            ("Route", ""), ("Code", "export default async () => ({ hello: 'buffer' });"),
            ("IsEnabled", "true"));

        response.StatusCode.Should().Be(HttpStatusCode.Found,
            "a successful run redirects to the deployment it just started");
        response.RedirectPath().Should().StartWith("/Deployments/Details/",
            "Run now opens the deployment's own progress page rather than claiming an instant result");

        var stored = Panel.Read(db => db.FunctionDefinitions.AsNoTracking().First(f => f.Id == world.FunctionId));
        stored.Code.Should().Be("export default async () => ({ hello: 'buffer' });",
            "the code that gets published has to be what was on screen when Run now was pressed");

        Panel.Deployments.Queued.Should().Contain(r => r.AppId == world.AppId,
            "making the buffer live is the only way this platform can run code that was never built");

        // Not an instant call: nothing here has actually executed the new code yet, so the recorded
        // history must not gain a row claiming it did.
        var invocations = Panel.Read(db => db.FunctionInvocations.Count(i => i.FunctionId == world.FunctionId));
        invocations.Should().Be(0);
    }

    [Fact]
    public async Task Run_now_with_invalid_code_redisplays_the_editor_and_starts_no_deployment()
    {
        var world = GivenFunctionApp("fn-run-invalid");
        Panel.GivenUser(fixture.WorkspaceId, "fn-run-invalid@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.152", "fn-run-invalid@example.com");

        var token = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");

        var response = await client.PostFormAsync(
            $"/functions/{world.AppId}/{world.FunctionId}/run", token,
            ("functionId", world.FunctionId.ToString()),
            ("Name", "Hello"), ("Trigger", ((int)FunctionTrigger.Http).ToString()),
            ("Route", ""), ("Code", ""), // empty code is refused by FunctionAppService.Validate
            ("IsEnabled", "true"));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a refused edit re-renders the editor rather than redirecting anywhere");

        var stored = Panel.Read(db => db.FunctionDefinitions.AsNoTracking().First(f => f.Id == world.FunctionId));
        stored.Code.Should().NotBeEmpty("a rejected edit must not overwrite the function's saved code");

        Panel.Deployments.Queued.Should().NotContain(r => r.AppId == world.AppId,
            "an edit the validator refused must never reach the deploy engine");
    }

    [Fact]
    public async Task An_operator_cannot_run_now_because_it_now_saves_and_deploys()
    {
        var world = GivenFunctionApp("fn-run-operator");
        Panel.Seed(db => db.FeatureGrants.Add(new FeatureGrant
        {
            Scope = FeatureScope.Workspace, TargetId = fixture.WorkspaceId,
            FeatureKey = PlatformFeatures.Functions, State = FeatureState.Enabled
        }));
        Panel.GivenUser(fixture.WorkspaceId, "fn-run-op@example.com", SystemRole.Operator);
        var client = await Panel.SignedInAs("203.0.113.153", "fn-run-op@example.com");

        // An operator can still reach the editor (read access), so the antiforgery token comes from
        // the same page a real attempt would use.
        var token = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");

        var response = await client.PostFormAsync(
            $"/functions/{world.AppId}/{world.FunctionId}/run", token,
            ("functionId", world.FunctionId.ToString()),
            ("Name", "Hello"), ("Trigger", ((int)FunctionTrigger.Http).ToString()),
            ("Route", ""), ("Code", "export default async () => ({ hello: 'buffer' });"),
            ("IsEnabled", "true"));

        // Run now now saves (apps.env) and deploys (apps.deploy) — neither is an operator's capability
        // (RolePermissions: Operator gets only apps.operate + backups.run) — so the cookie scheme's
        // AccessDeniedPath is what answers, the same as any other capability refusal.
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/denied");
        Panel.Deployments.Queued.Should().NotContain(r => r.AppId == world.AppId);
    }
}
