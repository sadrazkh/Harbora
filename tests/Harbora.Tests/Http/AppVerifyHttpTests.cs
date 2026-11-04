using System.Net;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Launch;
using Harbora.Infrastructure.Deployments;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// "Is it working?" as a request: the button, its refusals, and the card it comes back to. The real
/// <c>AppVerifier</c> is behind these, resolved from the panel's own container, so a missing registration
/// or a filtered read in the wrong place shows up here and not in production.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AppVerifyHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private App GivenApp(string slug, Guid? workspaceId = null)
    {
        var workspace = workspaceId ?? fixture.WorkspaceId;
        var projectId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();
        var app = new App
        {
            WorkspaceId = workspace,
            ServerId = Guid.CreateVersion7(),
            EnvironmentId = environmentId,
            Name = slug,
            Slug = slug,
            Kind = ServiceKind.Web,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Running
        };
        Panel.Seed(db =>
        {
            db.Projects.Add(new Harbora.Domain.Projects.Project
            {
                Id = projectId, WorkspaceId = workspace, Name = "Shop", Slug = "verify-" + slug
            });
            db.Environments.Add(new Harbora.Domain.Projects.Environment
            {
                Id = environmentId, WorkspaceId = workspace, ProjectId = projectId,
                Name = "Production", Slug = "production", IsDefault = true
            });
            db.Apps.Add(app);
        });
        return app;
    }

    private async Task<HttpClient> SignedIn(string slug, string ip, SystemRole role = SystemRole.Owner)
    {
        var email = $"{slug}@example.com";
        Panel.GivenUser(fixture.WorkspaceId, email, role);
        return await Panel.SignedInAs(ip, email);
    }

    private AppVerification? StoredFor(Guid appId) =>
        Panel.Read(db => db.AppVerifications.IgnoreQueryFilters().FirstOrDefault(v => v.AppId == appId));

    [Fact]
    public async Task Never_checked_is_a_sentence_and_the_button_with_no_empty_box()
    {
        var app = GivenApp("verify-never");
        var client = await SignedIn("verify-never", "203.0.113.150");

        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-verification-state=\"never\"");
        html.Should().Contain("data-verification-never");
        html.Should().Contain("data-verify-now");
        html.Should().Contain($"action=\"/apps/{app.Id}/verify\"");
        html.Should().NotContain("data-check=", "a list with nothing in it reads as nothing wrong");
    }

    [Fact]
    public async Task Pressing_it_runs_the_checks_stores_them_and_comes_back_to_the_card()
    {
        var app = GivenApp("verify-press");
        var client = await SignedIn("verify-press", "203.0.113.151");

        var response = await client.PostFormAsync($"/apps/{app.Id}/verify",
            await client.AntiforgeryTokenFrom($"/apps/details/{app.Id}"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.ToString().Should().BeEquivalentTo($"/apps/details/{app.Id}#app-checks");

        StoredFor(app.Id).Should().NotBeNull("the page reads what was stored, so a run that stored nothing shows nothing");

        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();
        html.Should().Contain("data-check=\"running\" data-check-status=\"Failed\"", "nothing is deployed");
        html.Should().Contain("data-check=\"restarts\" data-check-status=\"Skipped\"");
        html.Should().Contain("data-verification-ran");
        html.Should().Contain("data-verification-state=\"problems\"");
    }

    [Fact]
    public async Task A_deployed_app_is_really_read_from_its_engine()
    {
        var app = GivenApp("verify-live");
        var deployment = new Deployment
        {
            AppId = app.Id, WorkspaceId = app.WorkspaceId, Number = 3, Status = DeploymentStatus.Succeeded
        };
        Panel.Seed(db =>
        {
            db.Deployments.Add(deployment);
            db.Apps.Single(a => a.Id == app.Id).ActiveDeploymentId = deployment.Id;
        });
        var container = DeploymentPlanning.ContainerName(app.WorkspaceId, app.Slug, 3);
        Panel.Docker.SeedContainer(container, app.Slug, workspaceId: app.WorkspaceId);
        Panel.Docker.SeedDetail(container, new ContainerDetail(
            container, container, "img", null, "running", "Up", true, 0, DateTimeOffset.UtcNow.AddHours(-1)));

        var client = await SignedIn("verify-live", "203.0.113.152");
        await client.PostFormAsync($"/apps/{app.Id}/verify",
            await client.AntiforgeryTokenFrom($"/apps/details/{app.Id}"));

        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-check=\"running\" data-check-status=\"Passed\"");
        html.Should().Contain("data-check=\"restarts\" data-check-status=\"Passed\"");
        html.Should().Contain("data-check=\"url\" data-check-status=\"Skipped\"", "it has no domain");
        html.Should().Contain("data-check=\"logs\" data-check-status=\"Passed\"");
    }

    [Fact]
    public async Task A_stored_report_is_drawn_with_skipped_in_words_and_not_as_a_pass()
    {
        var app = GivenApp("verify-stored");
        Panel.Seed(db => db.AppVerifications.Add(new AppVerification
        {
            AppId = app.Id,
            WorkspaceId = app.WorkspaceId,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ChecksJson = "[{\"key\":\"running\",\"status\":\"Skipped\",\"title\":\"Not asked\",\"titleFa\":\"پرسیده نشد\",\"detail\":\"why\",\"detailFa\":\"چرا\"}]"
        }));
        var client = await SignedIn("verify-stored", "203.0.113.153");

        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-check=\"running\" data-check-status=\"Skipped\"");
        html.Should().Contain("data-verification-state=\"done\"");
        html.Should().NotContain("data-verification-never");

        // Persian renders as numeric references: «بررسی نشد», the status in words beside the title.
        html.Should().Contain("&#x628;&#x631;&#x631;&#x633;&#x6CC; &#x646;&#x634;&#x62F;");
    }

    [Fact]
    public async Task A_report_still_waiting_says_to_press_again_and_a_second_press_settles_it()
    {
        var app = GivenApp("verify-waiting");
        var started = DateTimeOffset.UtcNow.AddMinutes(-2);
        Panel.Seed(db => db.AppVerifications.Add(new AppVerification
        {
            AppId = app.Id,
            WorkspaceId = app.WorkspaceId,
            StartedAt = started,
            ChecksJson = "[{\"key\":\"url\",\"status\":\"Pending\",\"title\":\"Waiting\",\"titleFa\":\"منتظر\"}]"
        }));
        var client = await SignedIn("verify-waiting", "203.0.113.154");

        var page = await client.GetAsync($"/apps/details/{app.Id}");
        (await page.Content.ReadAsStringAsync()).Should().Contain("data-verification-waiting");

        await client.PostFormAsync($"/apps/{app.Id}/verify",
            await client.AntiforgeryTokenFrom($"/apps/details/{app.Id}"));

        var row = StoredFor(app.Id)!;
        row.StartedAt.Should().BeCloseTo(started, TimeSpan.FromSeconds(1),
            "it continued the run that was waiting, or a press would forever begin its own thirty seconds");
        row.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Without_a_token_it_is_refused_and_runs_nothing()
    {
        var app = GivenApp("verify-forged");
        var client = await SignedIn("verify-forged", "203.0.113.155");

        var response = await client.PostFormWithoutTokenAsync($"/apps/{app.Id}/verify");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        StoredFor(app.Id).Should().BeNull();
    }

    [Fact]
    public async Task Another_workspaces_app_is_a_404_and_runs_nothing()
    {
        var mine = GivenApp("verify-mine");
        var foreign = Guid.CreateVersion7();
        Panel.Seed(db => db.Workspaces.Add(new Harbora.Domain.Identity.Workspace
        {
            Id = foreign, Name = "Other", Slug = "verify-other"
        }));
        var theirs = GivenApp("verify-theirs", foreign);
        var client = await SignedIn("verify-intruder", "203.0.113.156");

        var response = await client.PostFormAsync($"/apps/{theirs.Id}/verify",
            await client.AntiforgeryTokenFrom($"/apps/details/{mine.Id}"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        StoredFor(theirs.Id).Should().BeNull();
    }

    [Fact]
    public async Task A_viewer_may_read_the_card_and_may_not_start_the_containers()
    {
        var app = GivenApp("verify-viewer");
        var client = await SignedIn("verify-viewer", "203.0.113.157", SystemRole.Viewer);

        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();
        html.Should().Contain("data-verification-state=\"never\"", "the card is theirs to read");
        html.Should().NotContain("data-verify-now", "a button that can only be refused is not an offer");
        html.Should().NotContain($"action=\"/apps/{app.Id}/verify\"");

        var token = await client.AntiforgeryTokenFrom($"/apps/details/{app.Id}");
        var response = await client.PostFormAsync($"/apps/{app.Id}/verify", token);

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
        response.RedirectPath().Should().NotBe($"/apps/details/{app.Id}");
        StoredFor(app.Id).Should().BeNull();
    }
}
