using System.Net;
using System.Net.Http.Headers;
using AngleSharp;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Networking;
using Harbora.Infrastructure.Proxy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Maintenance mode (P5, 2026-08-20 platform-options plan) driven as real HTTP requests through the
/// shipped pipeline — capability enforcement, antiforgery, and the anonymous 503 a visitor to a
/// maintaining app's own host actually receives, in both languages.
///
/// <para>
/// <c>AppOperationsServiceMaintenanceModeTests</c> already proves the toggle itself (including the
/// honesty-on-failure paths, against <c>RecordingProxyEngine</c>) at the service layer. This file
/// proves the two things only a real request can: that <c>Capabilities.AppsOperate</c> is actually
/// wired to the route, and that <c>MaintenanceModeMiddleware</c> + <c>MaintenanceController</c>
/// actually render the themed page — through the REAL <see cref="TraefikProxyEngine"/>
/// (<see cref="HarboraWebFactory"/> does not substitute <c>IProxyEngine</c>), so the YAML file this
/// toggle produces is asserted from disk, not merely a database flag.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class MaintenanceModeHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private (Guid AppId, Guid RouteId) GivenAppWithDomain(string slug, string host)
    {
        var appId = Guid.CreateVersion7();
        var routeId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.Apps.Add(new App
            {
                Id = appId, WorkspaceId = fixture.WorkspaceId, EnvironmentId = fixture.DefaultEnvironmentId,
                Name = slug, Slug = slug, SourceType = AppSourceType.PrebuiltImage,
                PrebuiltImage = "ghcr.io/example/" + slug + ":1.0"
            });
            db.Domains.Add(new DomainName { AppId = appId, Host = host, IsPrimary = true });
            db.Routes.Add(new Route
            {
                Id = routeId, WorkspaceId = fixture.WorkspaceId, AppId = appId, Host = host,
                TargetService = "harbora-" + slug + "-1", TargetPort = 3000, IsEnabled = true
            });
        });
        return (appId, routeId);
    }

    private string TraefikYaml() =>
        File.ReadAllText(Panel.Resolve<IOptions<TraefikOptions>>().Value.DynamicConfigPath);

    /// <summary>
    /// Razor's default HTML encoder writes Persian (and every other non-Latin) glyph out as a
    /// numeric character reference (<c>&amp;#x67E;</c>, …) — correct HTML, but a raw
    /// <c>string.Contains</c> against the literal Persian text then never matches, for a reason that
    /// has nothing to do with whether the page actually shows the message. Parsed through AngleSharp
    /// instead, the idiom the plan's own build discipline asks for on structural assertions —
    /// <c>.TextContent</c> gives back the decoded text a person actually reads.
    /// </summary>
    private static async Task<AngleSharp.Dom.IDocument> ParseAsync(string html) =>
        await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html));

    // ---- capability enforcement ----

    [Fact]
    public async Task An_operator_can_turn_maintenance_on_and_the_rendered_config_names_the_panel()
    {
        Panel.GivenUser(fixture.WorkspaceId, "maint-operator@example.com", SystemRole.Operator);
        var (appId, routeId) = GivenAppWithDomain("maint-op-app", "maint-op.example.com");
        var client = await Panel.SignedInAs("203.0.113.100", "maint-operator@example.com");
        var token = await client.AntiforgeryTokenFrom($"/apps/details/{appId}");

        var response = await client.PostFormAsync($"/Apps/EnableMaintenance/{appId}", token,
            ("maintenanceMessage", "Back soon"), ("maintenanceMessageFa", "به‌زودی برمی‌گردیم"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().StartWith("/Apps/Details");

        var app = Panel.Read(db => db.Apps.IgnoreQueryFilters().Single(a => a.Id == appId));
        app.MaintenanceMode.Should().BeTrue();
        app.MaintenanceMessage.Should().Be("Back soon");
        app.MaintenanceSince.Should().NotBeNull();

        var route = Panel.Read(db => db.Routes.IgnoreQueryFilters().Single(r => r.Id == routeId));
        route.TargetService.Should().Be("harbora-panel");
        route.TargetPort.Should().Be(8080);

        // Not merely the database row: the real TraefikProxyEngine actually wrote this to disk.
        var routerName = "r-" + routeId.ToString("N")[..12];
        var yaml = TraefikYaml();
        yaml.Should().Contain(routerName).And.Contain("harbora-panel:8080");
    }

    [Fact]
    public async Task An_operator_can_turn_maintenance_off_again()
    {
        Panel.GivenUser(fixture.WorkspaceId, "maint-operator2@example.com", SystemRole.Operator);
        var (appId, routeId) = GivenAppWithDomain("maint-op-app2", "maint-op2.example.com");
        var client = await Panel.SignedInAs("203.0.113.101", "maint-operator2@example.com");
        var onToken = await client.AntiforgeryTokenFrom($"/apps/details/{appId}");
        await client.PostFormAsync($"/Apps/EnableMaintenance/{appId}", onToken);

        var offToken = await client.AntiforgeryTokenFrom($"/apps/details/{appId}");
        var response = await client.PostFormAsync($"/Apps/DisableMaintenance/{appId}", offToken);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.Apps.IgnoreQueryFilters().Single(a => a.Id == appId).MaintenanceMode)
            .Should().BeFalse();
        var route = Panel.Read(db => db.Routes.IgnoreQueryFilters().Single(r => r.Id == routeId));
        route.TargetService.Should().Be("harbora-maint-op-app2-1", "the real upstream must be restored");
    }

    [Fact]
    public async Task A_viewer_is_refused_and_nothing_changes()
    {
        Panel.GivenUser(fixture.WorkspaceId, "maint-viewer@example.com", SystemRole.Viewer);
        var (appId, routeId) = GivenAppWithDomain("maint-viewer-app", "maint-viewer.example.com");
        var client = await Panel.SignedInAs("203.0.113.102", "maint-viewer@example.com");

        // A Viewer cannot even reach the app's own page to spend a token from it, so the token comes
        // from a page the role can open — the same shape CapabilityPolicyHttpTests uses for a Deploy
        // refusal it also cannot fetch a form for.
        var token = await client.AntiforgeryTokenFrom("/apps");

        var response = await client.PostFormAsync($"/Apps/EnableMaintenance/{appId}", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/denied");
        Panel.Read(db => db.Apps.IgnoreQueryFilters().Single(a => a.Id == appId).MaintenanceMode)
            .Should().BeFalse();
        Panel.Read(db => db.Routes.IgnoreQueryFilters().Single(r => r.Id == routeId).TargetService)
            .Should().Be("harbora-maint-viewer-app-1");
    }

    [Fact]
    public async Task An_anonymous_request_is_sent_to_sign_in_rather_than_toggling_anything()
    {
        var (appId, _) = GivenAppWithDomain("maint-anon-app", "maint-anon.example.com");

        var response = await Panel.ClientFrom("203.0.113.103")
            .PostFormWithoutTokenAsync($"/Apps/EnableMaintenance/{appId}");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.Apps.IgnoreQueryFilters().Single(a => a.Id == appId).MaintenanceMode)
            .Should().BeFalse();
    }

    // ---- the anonymous 503 a visitor to the app's own host actually receives ----

    /// <summary>
    /// One literal, referenced everywhere it is needed, rather than typed twice — two Persian
    /// literals typed separately can look identical and still not be byte-for-byte the same string
    /// (a stray zero-width joiner, or a Yeh/Kaf in its Arabic rather than Persian form), and a
    /// <c>Contain</c> assertion against the wrong one of those fails for a reason that has nothing to
    /// do with the page under test.
    /// </summary>
    private const string FaMessage = "پیام تعمیر و نگهداری";

    [Fact]
    public async Task A_visitor_to_a_maintaining_apps_host_gets_a_503_naming_the_app_in_Persian_by_default()
    {
        Panel.GivenUser(fixture.WorkspaceId, "maint-visitor1@example.com", SystemRole.Operator);
        var (appId, _) = GivenAppWithDomain("maint-page-app", "maint-page.example.com");
        var owner = await Panel.SignedInAs("203.0.113.104", "maint-visitor1@example.com");
        await owner.PostFormAsync($"/Apps/EnableMaintenance/{appId}",
            await owner.AntiforgeryTokenFrom($"/apps/details/{appId}"),
            ("maintenanceMessageFa", FaMessage));

        var visitor = Panel.ClientFrom("203.0.113.105");
        var request = new HttpRequestMessage(HttpMethod.Get, "https://localhost/anything/at/all")
        {
            Headers = { Host = "maint-page.example.com" }
        };
        var response = await visitor.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.Should().NotBeNull();
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("data-maintenance-page=\"true\"");
        html.Should().Contain($"data-app-id=\"{appId}\"");
        html.Should().Contain("""<html lang="fa" dir="rtl""", "the panel renders Persian by default");
        var document = await ParseAsync(html);
        document.Body!.TextContent.Should().Contain(FaMessage);
    }

    [Fact]
    public async Task The_same_page_renders_in_English_and_left_to_right_when_asked()
    {
        Panel.GivenUser(fixture.WorkspaceId, "maint-visitor2@example.com", SystemRole.Operator);
        var (appId, _) = GivenAppWithDomain("maint-page-app-en", "maint-page-en.example.com");
        var owner = await Panel.SignedInAs("203.0.113.106", "maint-visitor2@example.com");
        await owner.PostFormAsync($"/Apps/EnableMaintenance/{appId}",
            await owner.AntiforgeryTokenFrom($"/apps/details/{appId}"),
            ("maintenanceMessage", "We are improving things"));

        var visitor = Panel.ClientFrom("203.0.113.107");
        visitor.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));
        var request = new HttpRequestMessage(HttpMethod.Get, "https://localhost/")
        {
            Headers = { Host = "maint-page-en.example.com" }
        };
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));
        var response = await visitor.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("""<html lang="en" dir="ltr""");
        html.Should().Contain("We are improving things");
    }

    [Fact]
    public async Task A_host_that_is_not_in_maintenance_never_reaches_the_maintenance_page()
    {
        var (_, _) = GivenAppWithDomain("maint-off-app", "maint-off.example.com");

        var visitor = Panel.ClientFrom("203.0.113.108");
        var request = new HttpRequestMessage(HttpMethod.Get, "https://localhost/")
        {
            Headers = { Host = "maint-off.example.com" }
        };
        var response = await visitor.SendAsync(request);

        response.StatusCode.Should().NotBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("data-maintenance-page");
    }

    [Fact]
    public async Task Turning_maintenance_back_off_stops_the_503_on_the_same_host()
    {
        Panel.GivenUser(fixture.WorkspaceId, "maint-visitor3@example.com", SystemRole.Operator);
        var (appId, _) = GivenAppWithDomain("maint-toggle-app", "maint-toggle.example.com");
        var owner = await Panel.SignedInAs("203.0.113.109", "maint-visitor3@example.com");
        await owner.PostFormAsync($"/Apps/EnableMaintenance/{appId}",
            await owner.AntiforgeryTokenFrom($"/apps/details/{appId}"));
        await owner.PostFormAsync($"/Apps/DisableMaintenance/{appId}",
            await owner.AntiforgeryTokenFrom($"/apps/details/{appId}"));

        var visitor = Panel.ClientFrom("203.0.113.110");
        var request = new HttpRequestMessage(HttpMethod.Get, "https://localhost/")
        {
            Headers = { Host = "maint-toggle.example.com" }
        };
        var response = await visitor.SendAsync(request);

        response.StatusCode.Should().NotBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Hitting_the_rewritten_maintenance_path_directly_without_a_maintaining_host_404s()
    {
        // Nobody was routed here through the middleware's own rewrite — HttpContext.Items carries
        // nothing — so the honest answer is 404, not a generic maintenance page for no app at all.
        var response = await Panel.ClientFrom("203.0.113.111").GetAsync("/__maintenance");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
