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
/// The Event trigger's own half of F3 (2026-08-21 functions-and-services plan, "Custom events from
/// customer apps"): the editor lists custom.* keys this workspace has actually seen, and typing a new
/// one saves under the forced <c>custom.</c> namespace — through a real request, since
/// <see cref="FunctionEditorHttpTests"/>' own doc explains why the rendered DOM, not the Razor source,
/// is what proves a form actually wires up the way it looks like it does.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class FunctionCustomEventEditorHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private sealed record World(Guid AppId, Guid FunctionId);

    private World GivenFunctionApp(string slug, FunctionTrigger trigger = FunctionTrigger.Event, string? eventKey = null)
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
                FunctionRuntime = FunctionRuntime.JavaScript, DockerfilePath = "Dockerfile.harbora"
            });

            db.FunctionDefinitions.Add(new FunctionDefinition
            {
                Id = functionId, AppId = appId, WorkspaceId = fixture.WorkspaceId,
                Name = "listener", Slug = "listener", Trigger = trigger, EventKey = eventKey,
                Code = "export default async () => {}"
            });
        });

        return new World(appId, functionId);
    }

    private void GivenSeenCustomKey(string key, int timesSeen = 1) =>
        Panel.Seed(db => db.FunctionCustomEventKeys.Add(new FunctionCustomEventKey
        {
            WorkspaceId = fixture.WorkspaceId, Key = key, TimesSeen = timesSeen
        }));

    private static async Task<IDocument> ParseAsync(string html) =>
        await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html));

    private static Task<HttpResponseMessage> SaveAsync(
        HttpClient client, string token, World world, string? eventKey, string? customEventKey) =>
        client.PostFormAsync(
            $"/functions/{world.AppId}/save", token,
            ("functionId", world.FunctionId.ToString()),
            ("Name", "listener"), ("Trigger", ((int)FunctionTrigger.Event).ToString()),
            ("Route", ""), ("CronExpression", ""), ("EventKey", eventKey ?? ""),
            ("CustomEventKey", customEventKey ?? ""), ("Code", "// code"), ("IsEnabled", "true"));

    [Fact]
    public async Task The_editor_carries_a_free_text_box_for_a_brand_new_custom_key()
    {
        var world = GivenFunctionApp("fn-custom-box");
        Panel.GivenUser(fixture.WorkspaceId, "fn-custom-box@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.190", "fn-custom-box@example.com");

        var document = await ParseAsync(
            await (await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}")).Content.ReadAsStringAsync());

        document.QuerySelector("[data-custom-event-key]").Should().NotBeNull(
            "a workspace must be able to subscribe to a key nobody has emitted yet, not only ones already seen");
    }

    [Fact]
    public async Task A_seen_custom_key_appears_as_a_selectable_option()
    {
        GivenSeenCustomKey("custom.order.paid");
        var world = GivenFunctionApp("fn-custom-seen");
        Panel.GivenUser(fixture.WorkspaceId, "fn-custom-seen@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.191", "fn-custom-seen@example.com");

        var document = await ParseAsync(
            await (await client.GetAsync($"/functions/{world.AppId}/{world.FunctionId}")).Content.ReadAsStringAsync());

        var select = document.QuerySelector("[data-event-select]");
        select.Should().NotBeNull();
        select!.QuerySelector("option[value='custom.order.paid']").Should().NotBeNull(
            "a key this workspace's own apps already emitted must be pickable, not just typeable blind");
    }

    [Fact]
    public async Task Typing_a_new_custom_key_saves_it_under_the_forced_namespace()
    {
        var world = GivenFunctionApp("fn-custom-save", eventKey: FunctionEvents.DeploymentSucceeded);
        Panel.GivenUser(fixture.WorkspaceId, "fn-custom-save@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.192", "fn-custom-save@example.com");
        var token = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");

        var response = await SaveAsync(client, token, world,
            eventKey: FunctionEvents.DeploymentSucceeded, customEventKey: "shipment.created");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var fn = Panel.Read(db => db.FunctionDefinitions.AsNoTracking().First(f => f.Id == world.FunctionId));
        // The free-text box must win over the select's own posted value — the whole reason it exists
        // is to let somebody subscribe to a key the dropdown does not yet offer.
        fn.EventKey.Should().Be("custom.shipment.created");
    }

    [Fact]
    public async Task Choosing_a_seen_key_from_the_dropdown_saves_it_unchanged()
    {
        GivenSeenCustomKey("custom.order.paid");
        var world = GivenFunctionApp("fn-custom-dropdown");
        Panel.GivenUser(fixture.WorkspaceId, "fn-custom-dropdown@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.193", "fn-custom-dropdown@example.com");
        var token = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");

        var response = await SaveAsync(client, token, world, eventKey: "custom.order.paid", customEventKey: null);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var fn = Panel.Read(db => db.FunctionDefinitions.AsNoTracking().First(f => f.Id == world.FunctionId));
        fn.EventKey.Should().Be("custom.order.paid");
    }

    [Fact]
    public async Task A_caller_cannot_impersonate_a_platform_event_from_the_editor_either()
    {
        var world = GivenFunctionApp("fn-custom-spoof");
        Panel.GivenUser(fixture.WorkspaceId, "fn-custom-spoof@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.194", "fn-custom-spoof@example.com");
        var token = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");

        var response = await SaveAsync(client, token, world, eventKey: null, customEventKey: "deployment.succeeded");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var fn = Panel.Read(db => db.FunctionDefinitions.AsNoTracking().First(f => f.Id == world.FunctionId));
        fn.EventKey.Should().Be("custom.deployment.succeeded");
        fn.EventKey.Should().NotBe(FunctionEvents.DeploymentSucceeded);
    }

    [Fact]
    public async Task A_custom_key_that_normalises_to_nothing_is_refused_like_any_other_missing_event()
    {
        var world = GivenFunctionApp("fn-custom-junk");
        Panel.GivenUser(fixture.WorkspaceId, "fn-custom-junk@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.195", "fn-custom-junk@example.com");
        var token = await client.AntiforgeryTokenFrom($"/functions/{world.AppId}/{world.FunctionId}");

        var response = await SaveAsync(client, token, world, eventKey: null, customEventKey: "!!!");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a refusal re-renders the form rather than redirecting");
        var document = await ParseAsync(await response.Content.ReadAsStringAsync());
        document.QuerySelector(".text-danger")!.TextContent.Should().NotBeEmpty();
    }

    // ------------------------------------------------------- the Functions index's own visibility

    [Fact]
    public async Task The_functions_index_lists_an_unclaimed_seen_key()
    {
        GivenSeenCustomKey("custom.cart.abandoned");
        // An app must exist for the page to have anything to list at all — the card sits below it.
        GivenFunctionApp("fn-index-unclaimed", trigger: FunctionTrigger.Http);
        Panel.GivenUser(fixture.WorkspaceId, "fn-index-unclaimed@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.196", "fn-index-unclaimed@example.com");

        var document = await ParseAsync(await (await client.GetAsync("/functions")).Content.ReadAsStringAsync());

        var row = document.QuerySelector("[data-custom-event-key='custom.cart.abandoned']");
        row.Should().NotBeNull("a key nobody has subscribed to yet must still show up, never vanish behind the ingest 200");
        row!.QuerySelector("[data-custom-event-unclaimed]").Should().NotBeNull();
        row.QuerySelector("[data-custom-event-subscribed]").Should().BeNull();
    }

    [Fact]
    public async Task The_functions_index_shows_a_subscriber_count_once_a_function_listens()
    {
        GivenSeenCustomKey("custom.order.paid");
        GivenFunctionApp("fn-index-claimed", eventKey: "custom.order.paid");
        Panel.GivenUser(fixture.WorkspaceId, "fn-index-claimed@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.197", "fn-index-claimed@example.com");

        var document = await ParseAsync(await (await client.GetAsync("/functions")).Content.ReadAsStringAsync());

        var row = document.QuerySelector("[data-custom-event-key='custom.order.paid']");
        row.Should().NotBeNull();
        row!.QuerySelector("[data-custom-event-subscribed]").Should().NotBeNull();
        row.QuerySelector("[data-custom-event-unclaimed]").Should().BeNull();
    }

    // "No card when nothing has been seen" is proven at the view-model level instead of here: this
    // fixture's workspace is shared and cumulative across every test in the collection (the same
    // reason every other seeding helper in this file hands out its own fresh Guid app/function ids),
    // so by the time later tests run, earlier ones in this very file have already left seen keys
    // behind — asserting silence on a workspace under active, shared use is not this level's job.
}
