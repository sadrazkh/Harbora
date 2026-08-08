using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Web.Controllers.Api;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The status-code table in <c>docs/cli-deploy.md</c> §6, asserted rather than promised.
///
/// <para>
/// It is published as a public contract that third-party clients are invited to build against, and
/// until now nothing in the repository checked a single row of it. Every case below drives a real
/// request through the real pipeline — bearer authentication, the capability policies and the
/// controller — because the codes come from all three and no controller test can see that.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class ApiV1ContractHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    /// <summary>An owner, their token, and one app to point requests at.</summary>
    private (string Token, string Slug, Guid AppId) GivenOwnerWithApp(string label)
    {
        var owner = Panel.GivenUser(fixture.WorkspaceId, $"owner-{label}@example.com", SystemRole.Owner);
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            Name = "app-" + label,
            Slug = "app-" + label,
            SourceType = AppSourceType.Upload,
            Status = AppStatus.Created
        };
        Panel.Seed(db => db.Apps.Add(app));
        return (Panel.GivenApiToken(owner.Id), app.Slug, app.Id);
    }

    // ---- 200 -----------------------------------------------------------------------------------

    [Fact]
    public async Task Version_is_anonymous_and_reports_the_one_number_the_product_shares()
    {
        var response = await Panel.ClientFrom("203.0.113.20").GetAsync("/api/v1/version");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.JsonAsync();
        body.GetProperty("server").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("cli").GetString().Should().Be(body.GetProperty("server").GetString());
    }

    [Fact]
    public async Task A_valid_token_is_accepted_and_names_the_callers_workspace()
    {
        var (token, _, _) = GivenOwnerWithApp("whoami");

        var response = await Panel.BearerClientFrom("203.0.113.21", token).GetAsync("/api/v1/whoami");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.JsonAsync();
        body.GetProperty("email").GetString().Should().Be("owner-whoami@example.com");
        body.GetProperty("workspaceId").GetGuid().Should().Be(fixture.WorkspaceId);
    }

    [Fact]
    public async Task Deploy_is_accepted_and_reaches_the_engine()
    {
        var (token, slug, appId) = GivenOwnerWithApp("deploy-ok");
        var client = Panel.BearerClientFrom("203.0.113.22", token);

        var response = await client.PostAsync($"/api/v1/apps/{slug}/deploy",
            HttpConversation.Json(new { gitRef = "main" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.JsonAsync()).GetProperty("deploymentId").GetGuid().Should().NotBeEmpty();
        Panel.Deployments.Queued.Should().ContainSingle(r => r.AppId == appId)
            .Which.GitRef.Should().Be("main");
    }

    // ---- 400 -----------------------------------------------------------------------------------

    [Fact]
    public async Task An_archive_push_with_no_body_is_a_400_that_says_what_to_send()
    {
        var (token, slug, _) = GivenOwnerWithApp("empty-archive");
        var client = Panel.BearerClientFrom("203.0.113.23", token);

        var empty = new ByteArrayContent([]);
        empty.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        var response = await client.PostAsync($"/api/v1/apps/{slug}/deploy/archive", empty);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.DocumentedErrorAsync())
            .Should().Be("Send the project as a gzipped tar in the request body.");
    }

    [Fact]
    public async Task A_malformed_json_body_is_a_400_before_the_action_runs()
    {
        // [ApiController]'s automatic model-state response — a pipeline behaviour, not a controller
        // one. Its body is a ProblemDetails rather than the documented {"error": …}; see the report.
        var (token, slug, appId) = GivenOwnerWithApp("bad-json");
        var client = Panel.BearerClientFrom("203.0.113.24", token);

        var response = await client.PostAsync($"/api/v1/apps/{slug}/deploy",
            new StringContent("{ this is not json", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Panel.Deployments.Queued.Should().NotContain(r => r.AppId == appId,
            "the body never bound, so the action never ran");
    }

    // ---- 401 -----------------------------------------------------------------------------------

    [Fact]
    public async Task No_token_is_a_401()
    {
        var response = await Panel.ClientFrom("203.0.113.25").GetAsync("/api/v1/whoami");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_invented_token_is_a_401()
    {
        var response = await Panel
            .BearerClientFrom("203.0.113.26", "hbr_cli_deadbeef_" + new string('x', 40))
            .GetAsync("/api/v1/whoami");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_cookie_session_is_not_a_bearer_token()
    {
        // The API demands the Token scheme by name. A signed-in browser session must not be able to
        // drive the CLI's endpoints, and only a request can show that: the controller sees a
        // ClaimsPrincipal either way.
        Panel.GivenUser(fixture.WorkspaceId, "cookie-caller@example.com", SystemRole.Owner);
        var browser = await Panel.SignedInAs("203.0.113.27", "cookie-caller@example.com");

        var response = await browser.GetAsync("/api/v1/whoami");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Wrong_credentials_answer_the_documented_wording_and_nothing_else()
    {
        Panel.GivenUser(fixture.WorkspaceId, "real-person@example.com", SystemRole.Owner);
        var client = Panel.ClientFrom("203.0.113.28");

        var wrongPassword = await client.PostAsync("/api/v1/auth/token",
            HttpConversation.Json(new { email = "real-person@example.com", password = "not-it" }));
        var unknownAddress = await client.PostAsync("/api/v1/auth/token",
            HttpConversation.Json(new { email = "nobody@example.com", password = "not-it" }));

        wrongPassword.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unknownAddress.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await wrongPassword.DocumentedErrorAsync()).Should().Be("Invalid email or password.");
        (await unknownAddress.DocumentedErrorAsync()).Should().Be("Invalid email or password.",
            "the two answers being identical is what stops this being an account-enumeration oracle");
    }

    [Fact]
    public async Task The_right_credentials_return_a_token_that_then_works()
    {
        Panel.GivenUser(fixture.WorkspaceId, "cli-user@example.com", SystemRole.Owner);
        var client = Panel.ClientFrom("203.0.113.29");

        var issued = await client.PostAsync("/api/v1/auth/token", HttpConversation.Json(new
        {
            email = "cli-user@example.com",
            password = HarboraWebFactory.TestPassword,
            name = "a laptop"
        }));

        issued.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await issued.JsonAsync()).GetProperty("token").GetString()!;

        // The whole round trip: issued over HTTP, then presented over HTTP and believed.
        var whoami = await Panel.BearerClientFrom("203.0.113.30", token).GetAsync("/api/v1/whoami");
        whoami.StatusCode.Should().Be(HttpStatusCode.OK);
        (await whoami.JsonAsync()).GetProperty("email").GetString().Should().Be("cli-user@example.com");
    }

    // ---- 403 -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_viewers_token_authenticates_but_cannot_deploy()
    {
        var viewer = Panel.GivenUser(fixture.WorkspaceId, "viewer-api@example.com", SystemRole.Viewer);
        var token = Panel.GivenApiToken(viewer.Id);
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId, Name = "viewer-app", Slug = "viewer-app",
            SourceType = AppSourceType.Upload
        };
        Panel.Seed(db => db.Apps.Add(app));
        var client = Panel.BearerClientFrom("203.0.113.31", token);

        var readable = await client.GetAsync("/api/v1/apps");
        var deploy = await client.PostAsync("/api/v1/apps/viewer-app/deploy", HttpConversation.Json(new { }));

        readable.StatusCode.Should().Be(HttpStatusCode.OK, "a viewer may read");
        deploy.StatusCode.Should().Be(HttpStatusCode.Forbidden, "and may not deploy");
        Panel.Deployments.Queued.Should().NotContain(r => r.AppId == app.Id);
    }

    // ---- 404 -----------------------------------------------------------------------------------

    [Fact]
    public async Task An_app_that_is_not_there_and_an_app_that_is_not_yours_answer_the_same()
    {
        var (token, _, _) = GivenOwnerWithApp("not-mine");
        var otherWorkspace = Guid.CreateVersion7();
        Panel.Seed(db => db.Apps.Add(new App
        {
            WorkspaceId = otherWorkspace, Name = "someone-elses", Slug = "someone-elses",
            SourceType = AppSourceType.Upload
        }));
        var client = Panel.BearerClientFrom("203.0.113.32", token);

        var missing = await client.PostAsync("/api/v1/apps/no-such-app/deploy", HttpConversation.Json(new { }));
        var theirs = await client.PostAsync("/api/v1/apps/someone-elses/deploy", HttpConversation.Json(new { }));

        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        theirs.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "another workspace's app must be indistinguishable from one that does not exist");
        (await missing.DocumentedErrorAsync()).Should().Be("App not found.");
        (await theirs.DocumentedErrorAsync()).Should().Be("App not found.");
    }

    // ---- 409 -----------------------------------------------------------------------------------

    [Fact]
    public async Task Cancelling_a_deployment_that_already_ended_is_a_409_naming_the_state()
    {
        var (token, slug, _) = GivenOwnerWithApp("already-ended");
        var deploymentId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            var app = db.Apps.Single(a => a.Slug == slug);
            db.Deployments.Add(new Deployment
            {
                Id = deploymentId, AppId = app.Id, WorkspaceId = fixture.WorkspaceId,
                Number = 42, Status = DeploymentStatus.Succeeded, Trigger = DeploymentTrigger.Cli
            });
        });

        var response = await Panel.BearerClientFrom("203.0.113.33", token)
            .PostAsync($"/api/v1/deployments/{deploymentId}/cancel", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.DocumentedErrorAsync()).Should().Be(
            "Deployment #42 had already ended (Succeeded), so there was nothing to cancel.");
        Panel.Deployments.Cancelled.Should().NotContain(deploymentId,
            "a deployment that ended on its own must not be handed to the engine");
    }

    [Fact]
    public async Task A_deploy_the_engine_refuses_is_a_409_carrying_its_reason()
    {
        var (token, slug, _) = GivenOwnerWithApp("rollback-in-flight");
        Panel.Deployments.RefuseWith = "A rollback is already running for this app.";
        try
        {
            var response = await Panel.BearerClientFrom("203.0.113.34", token)
                .PostAsync($"/api/v1/apps/{slug}/deploy", HttpConversation.Json(new { }));

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await response.DocumentedErrorAsync()).Should().Be("A rollback is already running for this app.");
        }
        finally
        {
            Panel.Deployments.RefuseWith = null;
        }
    }

    // ---- 413 -----------------------------------------------------------------------------------

    [Fact]
    public void The_archive_endpoint_carries_the_size_limit_the_documentation_publishes()
    {
        // The one row of the table this lane cannot drive: 413 is produced by the server enforcing
        // MaxRequestBodySize while the body streams, and TestServer has no such limit to enforce.
        // What can be pinned here is the number the documentation names, so the two cannot drift —
        // the live proof belongs to the end-to-end lane (HARBORA-0022).
        var limit = typeof(ApiV1Controller)
            .GetMethod(nameof(ApiV1Controller.DeployArchive))!
            .GetCustomAttributesData()
            .Single(a => a.AttributeType == typeof(RequestSizeLimitAttribute))
            .ConstructorArguments[0].Value;

        limit.Should().Be(512L * 1024 * 1024, "docs/cli-deploy.md §6 publishes 512 MB compressed");
    }

    // ---- the rest of the documented surface ----------------------------------------------------

    [Fact]
    public async Task The_apps_list_carries_the_field_a_client_is_told_to_decide_on()
    {
        var (token, slug, _) = GivenOwnerWithApp("apps-list");

        var response = await Panel.BearerClientFrom("203.0.113.35", token).GetAsync("/api/v1/apps");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var app = (await response.JsonAsync()).EnumerateArray().Single(a => a.GetProperty("slug").GetString() == slug);
        app.GetProperty("canServerPull").GetBoolean().Should().BeFalse(
            "an app with no repository is the flow the CLI exists for, and the field is how a client knows");
        app.GetProperty("status").ValueKind.Should().Be(JsonValueKind.String);
        app.GetProperty("source").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task Deployment_status_and_logs_are_confined_to_the_callers_workspace()
    {
        var (token, _, _) = GivenOwnerWithApp("log-scope");
        var otherWorkspace = Guid.CreateVersion7();
        var strangersDeployment = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            var app = new App
            {
                WorkspaceId = otherWorkspace, Name = "stranger", Slug = "stranger-app",
                SourceType = AppSourceType.Upload
            };
            db.Apps.Add(app);
            db.Deployments.Add(new Deployment
            {
                Id = strangersDeployment, AppId = app.Id, WorkspaceId = otherWorkspace,
                Number = 1, Status = DeploymentStatus.Building
            });
        });
        var client = Panel.BearerClientFrom("203.0.113.36", token);

        (await client.GetAsync($"/api/v1/deployments/{strangersDeployment}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/v1/deployments/{strangersDeployment}/logs")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }
}
