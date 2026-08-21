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
/// F1 reversal's ingest endpoint (2026-08-21 functions-and-services plan follow-up), through a real
/// request — the wiring <see cref="FunctionInvocationReportServiceTests"/> cannot show: that
/// <c>[AllowAnonymous]</c> actually lets a request with no session through, that the header the
/// controller reads is the one the generated host's own <see cref="FunctionProject.SecretHeader"/>
/// constant names, and that a genuine call reaches the invoker's own row shape end to end. Mirrors
/// <c>CustomEventIngestHttpTests</c>, the worked example this door was built to copy.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class FunctionInvocationReportHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private sealed record World(Guid AppId, Guid FunctionId, string Secret);

    private World GivenFunctionApp(string label)
    {
        var appId = Guid.CreateVersion7();
        var functionId = Guid.CreateVersion7();
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
                Name = "report-" + label, Slug = "report-" + label, SourceType = AppSourceType.InlineCode,
                FunctionRuntime = FunctionRuntime.CSharp, DockerfilePath = "Dockerfile.harbora",
                // Standing in for what a real deploy injects as HARBORA_FN_SECRET — the test cannot read
                // that back from a container (no Docker here), so it is planted the way
                // DeploymentPipeline.BuildEnv would have produced it: through the same protector the
                // running panel actually uses.
                FunctionInvokeSecret = protector.Protect(plaintext),
                ActiveDeploymentId = Guid.CreateVersion7()
            });

            db.FunctionDefinitions.Add(new FunctionDefinition
            {
                Id = functionId, AppId = appId, WorkspaceId = fixture.WorkspaceId,
                Name = "Hello", Slug = "hello", Trigger = FunctionTrigger.Http,
                Code = "// code", IsEnabled = true, IsPublic = true
            });
        });

        return new World(appId, functionId, plaintext);
    }

    private static HttpRequestMessage Post(Guid appId, string? secret, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/functions/{appId}/report")
        {
            Content = HttpConversation.Json(body)
        };
        if (secret is not null) request.Headers.TryAddWithoutValidation(FunctionProject.SecretHeader, secret);
        return request;
    }

    [Fact]
    public async Task A_correctly_signed_report_is_accepted_with_no_session_at_all()
    {
        var world = GivenFunctionApp("ok");
        var client = Panel.ClientFrom("203.0.113.190");

        var response = await client.SendAsync(
            Post(world.AppId, world.Secret, new { slug = "hello", statusCode = 200, durationMs = 7 }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var invocation = Panel.Read(db => db.FunctionInvocations.IgnoreQueryFilters()
            .Single(i => i.FunctionId == world.FunctionId));
        invocation.Origin.Should().Be(FunctionInvocationOrigin.PublicCall);
        invocation.StatusCode.Should().Be(200);
        invocation.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task A_missing_secret_header_is_refused()
    {
        var world = GivenFunctionApp("nosecret");
        var client = Panel.ClientFrom("203.0.113.191");

        var response = await client.SendAsync(Post(world.AppId, secret: null, new { slug = "hello", statusCode = 200 }));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_wrong_secret_is_refused()
    {
        var world = GivenFunctionApp("wrongsecret");
        var client = Panel.ClientFrom("203.0.113.192");

        var response = await client.SendAsync(
            Post(world.AppId, "not-the-real-secret", new { slug = "hello", statusCode = 200 }));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_unknown_app_id_is_refused_with_no_session_needed_to_prove_it()
    {
        var client = Panel.ClientFrom("203.0.113.193");

        var response = await client.SendAsync(
            Post(Guid.CreateVersion7(), "whatever-secret", new { slug = "hello", statusCode = 200 }));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Another_workspaces_own_genuine_secret_cannot_reach_this_apps_id()
    {
        var mine = GivenFunctionApp("tenant-mine");
        var theirs = GivenFunctionApp("tenant-theirs");
        var client = Panel.ClientFrom("203.0.113.194");

        // theirs.Secret is a real, currently-valid secret — just not for mine.AppId.
        var response = await client.SendAsync(
            Post(mine.AppId, theirs.Secret, new { slug = "hello", statusCode = 200 }));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_unsigned_report_never_writes_a_function_invocation_row()
    {
        var world = GivenFunctionApp("noinvocation");
        var before = Panel.Read(db => db.FunctionInvocations.IgnoreQueryFilters().Count());
        var client = Panel.ClientFrom("203.0.113.195");

        await client.SendAsync(Post(world.AppId, "wrong", new { slug = "hello", statusCode = 200 }));

        var after = Panel.Read(db => db.FunctionInvocations.IgnoreQueryFilters().Count());
        after.Should().Be(before, "an unauthorized report must never reach the panel's own record");
    }

    [Fact]
    public async Task A_slug_this_app_does_not_have_is_a_404_not_a_500()
    {
        var world = GivenFunctionApp("badslug");
        var client = Panel.ClientFrom("203.0.113.196");

        var response = await client.SendAsync(
            Post(world.AppId, world.Secret, new { slug = "no-such-function", statusCode = 200 }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_reported_failure_is_readable_from_the_same_row_a_panel_made_calls_history_uses()
    {
        var world = GivenFunctionApp("failure");
        var client = Panel.ClientFrom("203.0.113.197");

        await client.SendAsync(Post(world.AppId, world.Secret,
            new { slug = "hello", statusCode = 500, durationMs = 9, error = "The function threw." }));

        var invocation = Panel.Read(db => db.FunctionInvocations.IgnoreQueryFilters()
            .Single(i => i.FunctionId == world.FunctionId));
        invocation.Succeeded.Should().BeFalse();
        invocation.Error.Should().Be("The function threw.");
        invocation.Origin.Should().Be(FunctionInvocationOrigin.PublicCall);
    }
}
