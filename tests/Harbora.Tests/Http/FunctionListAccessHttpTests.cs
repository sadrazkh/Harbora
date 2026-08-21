using System.Net;
using AngleSharp;
using AngleSharp.Dom;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Features;
using Harbora.Domain.Functions;
using Harbora.Domain.Networking;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Part 3 of the owner's own complaint (2026-08-21 functions-and-services plan follow-up): F1 shipped
/// the Public/Protected toggle, default Protected, but the function list — <c>/functions/{appId}</c>,
/// every function of an app in one table — never showed which one a function actually was. This is
/// where a person looks before ever opening one function's editor, so the state has to be visible here
/// too, not only on the function's own page (already covered by
/// <see cref="FunctionPublicUrlHttpTests"/>). Same AngleSharp-over-a-real-request idiom, for the same
/// reason: the panel renders Persian by default in tests, so assertions key off <c>data-</c> attributes,
/// never a sentence.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class FunctionListAccessHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private Guid GivenFunctionApp(string slug, FunctionTrigger trigger, bool isPublic, string? host)
    {
        var appId = Guid.CreateVersion7();

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
                FunctionRuntime = FunctionRuntime.CSharp, DockerfilePath = "Dockerfile.harbora"
            });

            db.FunctionDefinitions.Add(new FunctionDefinition
            {
                AppId = appId, WorkspaceId = fixture.WorkspaceId,
                Name = "Hello", Slug = "hello", Trigger = trigger,
                Code = "// code", IsEnabled = true, IsPublic = isPublic,
                CronExpression = trigger == FunctionTrigger.Cron ? "0 3 * * *" : null,
                EventKey = trigger == FunctionTrigger.Event ? FunctionEvents.DeploymentSucceeded : null
            });

            if (host is { Length: > 0 })
                db.Domains.Add(new DomainName { AppId = appId, Host = host, IsPrimary = true });
        });

        return appId;
    }

    private static async Task<IDocument> ParseAsync(string html) =>
        await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html));

    [Fact]
    public async Task A_public_functions_row_shows_the_public_badge_and_its_copy_ready_url()
    {
        var appId = GivenFunctionApp("fn-list-public", FunctionTrigger.Http, isPublic: true, host: "fn-list-public.example.test");
        Panel.GivenUser(fixture.WorkspaceId, "fn-list-public@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.200", "fn-list-public@example.com");

        var document = await ParseAsync(await (await client.GetAsync($"/functions/{appId}")).Content.ReadAsStringAsync());

        var access = document.QuerySelector("[data-fn-access='public']");
        access.Should().NotBeNull("a public function's row must say so plainly, without opening the editor");
        var url = access!.QuerySelector("[data-copy-url]");
        url.Should().NotBeNull("the URL must be copy-ready right on the list, the same idiom the editor uses");
        url!.GetAttribute("data-copy-url").Should().Be("https://fn-list-public.example.test/hello");
    }

    [Fact]
    public async Task A_private_functions_row_shows_the_private_badge_and_says_what_it_means()
    {
        var appId = GivenFunctionApp("fn-list-private", FunctionTrigger.Http, isPublic: false, host: "fn-list-private.example.test");
        Panel.GivenUser(fixture.WorkspaceId, "fn-list-private@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.201", "fn-list-private@example.com");

        var document = await ParseAsync(await (await client.GetAsync($"/functions/{appId}")).Content.ReadAsStringAsync());

        var access = document.QuerySelector("[data-fn-access='private']");
        access.Should().NotBeNull("Protected is the default, but the row must still say so, not stay blank");
        access!.QuerySelector("[data-copy-url]").Should().BeNull("there is nothing to copy for a function nobody outside the panel can reach");
    }

    [Fact]
    public async Task A_public_function_with_no_host_yet_shows_the_badge_but_no_broken_url()
    {
        var appId = GivenFunctionApp("fn-list-nohost", FunctionTrigger.Http, isPublic: true, host: null);
        Panel.GivenUser(fixture.WorkspaceId, "fn-list-nohost@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.202", "fn-list-nohost@example.com");

        var document = await ParseAsync(await (await client.GetAsync($"/functions/{appId}")).Content.ReadAsStringAsync());

        var access = document.QuerySelector("[data-fn-access='public']");
        access.Should().NotBeNull();
        access!.QuerySelector("[data-copy-url]").Should().BeNull("there is no real address yet — showing one would be a fabricated link");
    }

    [Theory]
    [InlineData(FunctionTrigger.Cron)]
    [InlineData(FunctionTrigger.Event)]
    public async Task A_non_http_functions_row_carries_no_access_badge_at_all(FunctionTrigger trigger)
    {
        // IsPublic is meaningless for a trigger that never sits behind the visitor route it gates
        // (the controller forces it off regardless of what was ever posted) — the list must not
        // invent an access state for something that structurally cannot have one.
        var appId = GivenFunctionApp("fn-list-nonhttp-" + trigger, trigger, isPublic: false, host: "fn-list-nonhttp.example.test");
        Panel.GivenUser(fixture.WorkspaceId, "fn-list-nonhttp-" + trigger + "@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.21" + (int)trigger, "fn-list-nonhttp-" + trigger + "@example.com");

        var document = await ParseAsync(await (await client.GetAsync($"/functions/{appId}")).Content.ReadAsStringAsync());

        document.QuerySelector("[data-fn-access]").Should().BeNull();
    }
}
