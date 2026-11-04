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

        var token = await client.AntiforgeryTokenFrom($"/apps/details/{app.Id}");
        var response = await client.PostFormAsync($"/apps/{app.Id}/verify", token);

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
        response.RedirectPath().Should().NotBe($"/apps/details/{app.Id}");
        StoredFor(app.Id).Should().BeNull();
    }

    /// <summary>
    /// The panel's own registered client over a real socket, because what is being proved lives in how
    /// that client is configured and a stub handler cannot follow anything. An absolute redirect is the
    /// case that matters: an app builds it from the Host it was sent, so following it would leave through
    /// the proxy again and read the proxy's 404 as the app's.
    /// </summary>
    [Fact]
    public async Task Asking_the_app_directly_does_not_follow_a_redirect_back_out_and_sends_what_the_route_would()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = AnswerOnceAsync(listener,
            "HTTP/1.1 302 Found\r\nLocation: https://shop.example.invalid/login\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        var h = new Verification.VerifierHarness();
        h.Db.Servers.Add(new Harbora.Domain.Servers.Server
        {
            Id = h.ServerId, Name = "node-2", Hostname = "127.0.0.1", IsLocal = false
        });
        var verified = h.GivenDeployedApp();
        h.GivenDomain(verified, "shop.example.invalid");
        h.Db.Routes.Add(new Harbora.Domain.Networking.Route
        {
            WorkspaceId = verified.WorkspaceId, AppId = verified.Id, Host = "shop.example.invalid",
            TargetService = "127.0.0.1", TargetPort = port
        });
        await h.Db.SaveChangesAsync();
        h.Probe.Answer = new UptimeProbeResult(ProbeOutcome.Down, 404, 5, "answered 404, expected 200.");

        var report = await h.Verifier(http: Panel.Resolve<IHttpClientFactory>()).RunAsync(
            verified.Id, null, Verification.VerifierHarness.Now.AddMinutes(5), default);

        var url = report.Checks.Single(c => c.Key == "url");
        url.Status.Should().Be(Harbora.Infrastructure.Verification.AppCheckStatus.Failed,
            "the app answered, so that 404 was not its own");
        url.Detail.Should().Contain("302");

        var request = await received.WaitAsync(TimeSpan.FromSeconds(30));
        request.Should().StartWith("GET / HTTP/1.1");
        request.Should().Contain("Host: shop.example.invalid");
        request.Should().Contain("X-Forwarded-Host: shop.example.invalid");
        request.Should().Contain("X-Forwarded-Proto: https");
    }

    private static async Task<string> AnswerOnceAsync(System.Net.Sockets.TcpListener listener, string response)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();

        var buffer = new byte[4096];
        var received = new System.Text.StringBuilder();
        while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            received.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, read));
        }

        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(response));
        return received.ToString();
    }
}
