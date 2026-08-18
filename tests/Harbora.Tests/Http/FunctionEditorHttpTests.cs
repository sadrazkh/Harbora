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
    public async Task Run_now_invokes_the_published_code_and_never_starts_a_deployment()
    {
        var world = GivenFunctionApp("fn-run-owner");
        Panel.GivenUser(fixture.WorkspaceId, "fn-run-owner@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.151", "fn-run-owner@example.com");

        var token = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");

        var response = await client.PostFormAsync(
            $"/functions/{world.AppId}/{world.FunctionId}/run", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Contain(world.FunctionId.ToString(),
            "Run now comes back to the editor it was pressed from, where the history it wrote is shown");

        // The whole point of keeping this button separate from Publish: it must not rebuild anything.
        // Making it save-and-deploy was tried and reverted precisely because that made it a second
        // name for Publish and cost the one way to test a cron function without waiting for 03:00.
        Panel.Deployments.Queued.Should().NotContain(r => r.AppId == world.AppId,
            "running the published version is an operating act, not a deploy");
    }

    [Fact]
    public async Task Run_now_never_writes_the_editor_buffer_over_the_saved_code()
    {
        var world = GivenFunctionApp("fn-run-nosave");
        Panel.GivenUser(fixture.WorkspaceId, "fn-run-nosave@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.152", "fn-run-nosave@example.com");

        var token = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");

        // Run now shares the Save form, so the POST genuinely carries every field on screen. The
        // action must ignore them: a button labelled "run" that quietly saved would be the same class
        // of lie as the nested form this class was written to catch, only in the other direction.
        await client.PostFormAsync(
            $"/functions/{world.AppId}/{world.FunctionId}/run", token,
            ("functionId", world.FunctionId.ToString()),
            ("Name", "Renamed by a run"), ("Trigger", ((int)FunctionTrigger.Http).ToString()),
            ("Route", ""), ("Code", "export default async () => ({ hello: 'buffer' });"),
            ("IsEnabled", "true"));

        var stored = Panel.Read(db => db.FunctionDefinitions.AsNoTracking().First(f => f.Id == world.FunctionId));
        stored.Code.Should().Be("export default async () => ({ hello: 'world' });");
        stored.Name.Should().Be("Hello");
    }

    [Fact]
    public async Task An_operator_may_run_the_published_function()
    {
        var world = GivenFunctionApp("fn-run-operator");
        Panel.GivenUser(fixture.WorkspaceId, "fn-run-op@example.com", SystemRole.Operator);
        var client = await Panel.SignedInAs("203.0.113.153", "fn-run-op@example.com");

        var token = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");

        var response = await client.PostFormAsync(
            $"/functions/{world.AppId}/{world.FunctionId}/run", token);

        // apps.operate is exactly an Operator's capability (RolePermissions gives them apps.operate +
        // backups.run). Running published code is the operating act, so this door stays open to them —
        // the reverted save-and-deploy version had closed it.
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().NotBe("/account/denied");
    }

    [Fact]
    public async Task The_editor_says_which_code_run_now_will_reach_when_edits_are_unpublished()
    {
        var world = GivenFunctionApp("fn-run-stale");
        Panel.Seed(db =>
        {
            var app = db.Apps.First(a => a.Id == world.AppId);
            app.ActiveDeploymentId = Guid.CreateVersion7();          // published at least once
            db.FunctionDefinitions.First(f => f.Id == world.FunctionId).HasUnpublishedChanges = true;
        });
        Panel.GivenUser(fixture.WorkspaceId, "fn-run-stale@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.154", "fn-run-stale@example.com");

        var document = await ParseAsync(
            await (await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}")).Content.ReadAsStringAsync());

        document.QuerySelector("[data-run-state='stale']").Should().NotBeNull(
            "the panel must say plainly that Run now reaches the published code, not the edits on screen");
        document.QuerySelector("[data-action='run']")!.HasAttribute("disabled").Should().BeFalse(
            "unpublished edits do not stop the published version from being runnable");
    }

    [Fact]
    public async Task Run_now_is_disabled_before_the_app_has_ever_been_published()
    {
        var world = GivenFunctionApp("fn-run-unpublished");
        Panel.GivenUser(fixture.WorkspaceId, "fn-run-unpub@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.155", "fn-run-unpub@example.com");

        var document = await ParseAsync(
            await (await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}")).Content.ReadAsStringAsync());

        // FunctionInvoker.QueueAsync returns null when ActiveDeploymentId is null, so pressing this
        // could only ever fail. Saying so on the button is the same fact, one step earlier.
        document.QuerySelector("[data-run-state='never-published']").Should().NotBeNull();
        document.QuerySelector("[data-action='run']")!.HasAttribute("disabled").Should().BeTrue();
    }
}
