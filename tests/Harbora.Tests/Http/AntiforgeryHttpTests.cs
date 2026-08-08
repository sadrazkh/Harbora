using System.Net;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Antiforgery, which is a property of the request and of nothing else.
///
/// <para>
/// No controller test can observe it: the token is validated by a filter that runs before the action
/// and is issued by a tag helper that runs inside a view. Remove <c>AddAntiforgery</c> or drop a
/// <c>[ValidateAntiForgeryToken]</c> and every existing test in this repository still passes.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AntiforgeryHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    [Fact]
    public async Task A_form_post_with_no_token_is_refused()
    {
        var client = Panel.ClientFrom("203.0.113.50");

        var response = await client.PostFormWithoutTokenAsync("/account/language",
            ("culture", "en"), ("returnUrl", "/"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_same_form_with_the_token_the_page_issued_is_accepted()
    {
        var client = Panel.ClientFrom("203.0.113.51");
        var token = await client.AntiforgeryTokenFrom("/account/login");

        var response = await client.PostFormAsync("/account/language", token,
            ("culture", "en"), ("returnUrl", "/"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/");
    }

    [Fact]
    public async Task A_token_without_the_cookie_that_belongs_to_it_is_refused()
    {
        // The field alone is not the proof — it is the pair. A page scraped by one client and posted
        // by another is exactly the cross-site case the token exists for.
        var reader = Panel.ClientFrom("203.0.113.52");
        var stranger = Panel.ClientFrom("203.0.113.53");
        var token = await reader.AntiforgeryTokenFrom("/account/login");

        var response = await stranger.PostFormAsync("/account/language", token, ("culture", "en"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_privileged_post_the_policy_would_allow_is_still_refused_without_a_token()
    {
        // The owner holds apps.deploy, so authorization passes and the antiforgery filter is the only
        // thing left between the request and a deployment. Nothing must be queued.
        Panel.GivenUser(fixture.WorkspaceId, "csrf-owner@example.com", SystemRole.Owner);
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId, Name = "csrf-app", Slug = "csrf-app",
            SourceType = AppSourceType.Upload
        };
        Panel.Seed(db => db.Apps.Add(app));
        var client = await Panel.SignedInAs("203.0.113.54", "csrf-owner@example.com");

        var response = await client.PostFormWithoutTokenAsync($"/Apps/Deploy/{app.Id}", ("gitRef", "main"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Panel.Deployments.Queued.Should().NotContain(r => r.AppId == app.Id);
    }

    [Fact]
    public async Task The_API_is_not_behind_antiforgery_because_a_bearer_token_is_not_ambient()
    {
        // Stated as a test rather than left as an assumption: a CLI cannot fetch a hidden field, and
        // it does not need to — the credential it presents is not one a browser attaches by itself.
        var owner = Panel.GivenUser(fixture.WorkspaceId, "csrf-cli@example.com", SystemRole.Owner);
        var token = Panel.GivenApiToken(owner.Id);
        Panel.Seed(db => db.Apps.Add(new App
        {
            WorkspaceId = fixture.WorkspaceId, Name = "csrf-cli-app", Slug = "csrf-cli-app",
            SourceType = AppSourceType.Upload
        }));

        var response = await Panel.BearerClientFrom("203.0.113.55", token)
            .PostAsync("/api/v1/apps/csrf-cli-app/deploy", HttpConversation.Json(new { }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
