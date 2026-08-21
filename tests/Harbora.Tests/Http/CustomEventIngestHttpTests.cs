using System.Net;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Features;
using Harbora.Domain.Functions;
using Harbora.Infrastructure.Functions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// F3's ingest endpoint (2026-08-21 functions-and-services plan, "Custom events from customer apps"),
/// through a real request — the wiring <see cref="CustomEventIngestServiceTests"/> cannot show: that
/// <c>[AllowAnonymous]</c> actually lets a request with no session or cookie through, that the header
/// name the controller reads is the one the generated host's own <see cref="FunctionProject.SecretHeader"/>
/// constant names, and that the panel's global workspace filter — which resolves to "no tenant" for a
/// request with no cookie — does not turn a genuine call into "unknown app", the same trap
/// <c>GitWebhookHttpTests</c> exists to catch on the git webhook door.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class CustomEventIngestHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private sealed record World(Guid AppId, string Secret);

    private World GivenFunctionApp(string label, string? subscribeTo = "custom.order.paid")
    {
        var appId = Guid.CreateVersion7();
        var plaintext = "plaintext-secret-" + label;
        var protector = Panel.Resolve<ISecretProtector>();

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
                Name = "ingest-" + label, Slug = "ingest-" + label, SourceType = AppSourceType.InlineCode,
                FunctionRuntime = FunctionRuntime.CSharp, DockerfilePath = "Dockerfile.harbora",
                // Standing in for what a real deploy injects into the container as HARBORA_FN_SECRET —
                // the test cannot read that back from a container (no Docker here), so it is planted
                // the way DeploymentPipeline.BuildEnv would have produced it: through the same
                // protector the running panel actually uses.
                FunctionInvokeSecret = protector.Protect(plaintext),
                ActiveDeploymentId = Guid.CreateVersion7()
            });

            if (subscribeTo is not null)
            {
                db.FunctionDefinitions.Add(new FunctionDefinition
                {
                    AppId = appId, WorkspaceId = fixture.WorkspaceId, Name = "listener", Slug = "listener",
                    Trigger = FunctionTrigger.Event, EventKey = subscribeTo,
                    Code = "// code", IsEnabled = true
                });
            }
        });

        return new World(appId, plaintext);
    }

    private static HttpRequestMessage Post(Guid appId, string? secret, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/events/ingest/{appId}")
        {
            Content = HttpConversation.Json(body)
        };
        if (secret is not null) request.Headers.TryAddWithoutValidation(FunctionProject.SecretHeader, secret);
        return request;
    }

    [Fact]
    public async Task A_correctly_signed_event_is_accepted_with_no_session_at_all()
    {
        var world = GivenFunctionApp("ok");
        var client = Panel.ClientFrom("203.0.113.180");

        var response = await client.SendAsync(Post(world.AppId, world.Secret, new { key = "order.paid" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.JsonAsync()).GetProperty("key").GetString().Should().Be("custom.order.paid");
    }

    [Fact]
    public async Task The_posted_key_lands_under_the_custom_namespace_even_unprefixed()
    {
        var world = GivenFunctionApp("prefix", subscribeTo: null);
        var client = Panel.ClientFrom("203.0.113.181");

        var response = await client.SendAsync(Post(world.AppId, world.Secret, new { key = "shipment.created" }));

        (await response.JsonAsync()).GetProperty("key").GetString().Should().Be("custom.shipment.created");
    }

    [Fact]
    public async Task A_caller_cannot_impersonate_a_platform_event_over_the_wire()
    {
        var world = GivenFunctionApp("spoof", subscribeTo: null);
        var client = Panel.ClientFrom("203.0.113.182");

        var response = await client.SendAsync(
            Post(world.AppId, world.Secret, new { key = FunctionEvents.DeploymentSucceeded }));

        var key = (await response.JsonAsync()).GetProperty("key").GetString();
        key.Should().Be("custom.deployment.succeeded");
        key.Should().NotBe(FunctionEvents.DeploymentSucceeded);
    }

    [Fact]
    public async Task A_missing_secret_header_is_refused()
    {
        var world = GivenFunctionApp("nosecret");
        var client = Panel.ClientFrom("203.0.113.183");

        var response = await client.SendAsync(Post(world.AppId, secret: null, new { key = "order.paid" }));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_wrong_secret_is_refused()
    {
        var world = GivenFunctionApp("wrongsecret");
        var client = Panel.ClientFrom("203.0.113.184");

        var response = await client.SendAsync(Post(world.AppId, "not-the-real-secret", new { key = "order.paid" }));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_unknown_app_id_is_refused_with_no_session_needed_to_prove_it()
    {
        var client = Panel.ClientFrom("203.0.113.185");

        var response = await client.SendAsync(
            Post(Guid.CreateVersion7(), "whatever-secret", new { key = "order.paid" }));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Another_workspaces_own_genuine_secret_cannot_reach_this_apps_id()
    {
        var mine = GivenFunctionApp("tenant-mine");
        var theirs = GivenFunctionApp("tenant-theirs");
        var client = Panel.ClientFrom("203.0.113.186");

        // theirs.Secret is a real, currently-valid secret — just not for mine.AppId.
        var response = await client.SendAsync(Post(mine.AppId, theirs.Secret, new { key = "order.paid" }));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_key_nobody_subscribes_to_is_accepted_and_stays_findable()
    {
        var world = GivenFunctionApp("unclaimed", subscribeTo: null);
        var client = Panel.ClientFrom("203.0.113.187");

        var response = await client.SendAsync(Post(world.AppId, world.Secret, new { key = "cart.abandoned" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var seen = Panel.Read(db => db.FunctionCustomEventKeys.AsNoTracking()
            .Where(k => k.WorkspaceId == fixture.WorkspaceId && k.Key == "custom.cart.abandoned")
            .ToList());
        seen.Should().ContainSingle("an unsubscribed key must still be visible, not vanish behind the 200");
    }

    [Fact]
    public async Task An_unsigned_request_never_creates_a_function_invocation_row()
    {
        var world = GivenFunctionApp("noinvocation");
        var before = Panel.Read(db => db.FunctionInvocations.IgnoreQueryFilters().Count());
        var client = Panel.ClientFrom("203.0.113.188");

        await client.SendAsync(Post(world.AppId, "wrong", new { key = "order.paid" }));

        var after = Panel.Read(db => db.FunctionInvocations.IgnoreQueryFilters().Count());
        after.Should().Be(before, "an unauthorized attempt must never reach the invoker");
    }
}
