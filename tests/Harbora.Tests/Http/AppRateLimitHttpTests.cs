using System.Net;
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
/// Per-app rate limiting (C3, 2026-08-27 what's-left plan) driven as real HTTP requests through the
/// shipped pipeline — capability enforcement and the actual YAML this toggle produces, through the
/// REAL <see cref="TraefikProxyEngine"/> (<see cref="HarboraWebFactory"/> does not substitute
/// <c>IProxyEngine</c>), exactly as <c>MaintenanceModeHttpTests</c> proves maintenance mode.
/// <see cref="AppOperationsServiceRateLimitTests"/> already proves the toggle itself (including the
/// honesty-on-failure paths) at the service layer.
///
/// <para>
/// Not to be confused with the platform's OWN per-IP request limiter on its login/webhook endpoints
/// (<c>RateLimitHttpTests</c>, elsewhere in this folder) — that is Program.cs protecting the panel's
/// own routes; this is a customer protecting theirs, which is exactly the gap C3 closes.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AppRateLimitHttpTests(HarboraHttpFixture fixture)
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

    // ---- capability enforcement + the actual rendered config ----

    [Fact]
    public async Task An_operator_can_turn_rate_limiting_on_and_the_rendered_config_carries_the_numbers()
    {
        Panel.GivenUser(fixture.WorkspaceId, "arl-operator@example.com", SystemRole.Operator);
        var (appId, routeId) = GivenAppWithDomain("arl-op-app", "arl-op.example.com");
        var client = await Panel.SignedInAs("203.0.113.130", "arl-operator@example.com");
        var token = await client.AntiforgeryTokenFrom($"/apps/details/{appId}");

        var response = await client.PostFormAsync($"/Apps/SetRateLimit/{appId}", token,
            ("rateLimitAverage", "300"), ("rateLimitBurst", "150"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().StartWith("/Apps/Details");

        var app = Panel.Read(db => db.Apps.IgnoreQueryFilters().Single(a => a.Id == appId));
        app.RateLimitEnabled.Should().BeTrue();
        app.RateLimitAverage.Should().Be(300);
        app.RateLimitBurst.Should().Be(150);

        var route = Panel.Read(db => db.Routes.IgnoreQueryFilters().Single(r => r.Id == routeId));
        route.RateLimitEnabled.Should().BeTrue();

        // Not merely the database row: the real TraefikProxyEngine actually wrote this to disk.
        var routerName = "r-" + routeId.ToString("N")[..12];
        var yaml = TraefikYaml();
        yaml.Should().Contain($"{routerName}-ratelimit:").And.Contain("average: 300").And.Contain("burst: 150");
    }

    [Fact]
    public async Task An_operator_can_reconfigure_the_numbers_while_it_is_already_on()
    {
        Panel.GivenUser(fixture.WorkspaceId, "arl-reconfig@example.com", SystemRole.Operator);
        var (appId, routeId) = GivenAppWithDomain("arl-reconfig-app", "arl-reconfig.example.com");
        var client = await Panel.SignedInAs("203.0.113.131", "arl-reconfig@example.com");
        await client.PostFormAsync($"/Apps/SetRateLimit/{appId}",
            await client.AntiforgeryTokenFrom($"/apps/details/{appId}"),
            ("rateLimitAverage", "300"), ("rateLimitBurst", "150"));

        var response = await client.PostFormAsync($"/Apps/SetRateLimit/{appId}",
            await client.AntiforgeryTokenFrom($"/apps/details/{appId}"),
            ("rateLimitAverage", "600"), ("rateLimitBurst", "50"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var route = Panel.Read(db => db.Routes.IgnoreQueryFilters().Single(r => r.Id == routeId));
        route.RateLimitAverage.Should().Be(600);
        route.RateLimitBurst.Should().Be(50);
        TraefikYaml().Should().Contain("average: 600").And.Contain("burst: 50");
    }

    [Fact]
    public async Task An_operator_can_turn_rate_limiting_off_again()
    {
        Panel.GivenUser(fixture.WorkspaceId, "arl-operator2@example.com", SystemRole.Operator);
        var (appId, routeId) = GivenAppWithDomain("arl-op-app2", "arl-op2.example.com");
        var client = await Panel.SignedInAs("203.0.113.132", "arl-operator2@example.com");
        await client.PostFormAsync($"/Apps/SetRateLimit/{appId}",
            await client.AntiforgeryTokenFrom($"/apps/details/{appId}"),
            ("rateLimitAverage", "300"), ("rateLimitBurst", "150"));

        var response = await client.PostFormAsync($"/Apps/DisableRateLimit/{appId}",
            await client.AntiforgeryTokenFrom($"/apps/details/{appId}"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.Apps.IgnoreQueryFilters().Single(a => a.Id == appId).RateLimitEnabled)
            .Should().BeFalse();
        var route = Panel.Read(db => db.Routes.IgnoreQueryFilters().Single(r => r.Id == routeId));
        route.RateLimitEnabled.Should().BeFalse();
        var routerName = "r-" + routeId.ToString("N")[..12];
        TraefikYaml().Should().NotContain($"{routerName}-ratelimit:");
    }

    [Fact]
    public async Task A_viewer_is_refused_and_nothing_changes()
    {
        Panel.GivenUser(fixture.WorkspaceId, "arl-viewer@example.com", SystemRole.Viewer);
        var (appId, routeId) = GivenAppWithDomain("arl-viewer-app", "arl-viewer.example.com");
        var client = await Panel.SignedInAs("203.0.113.133", "arl-viewer@example.com");
        var token = await client.AntiforgeryTokenFrom("/apps");

        var response = await client.PostFormAsync($"/Apps/SetRateLimit/{appId}", token,
            ("rateLimitAverage", "300"), ("rateLimitBurst", "150"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/denied");
        Panel.Read(db => db.Apps.IgnoreQueryFilters().Single(a => a.Id == appId).RateLimitEnabled)
            .Should().BeFalse();
        Panel.Read(db => db.Routes.IgnoreQueryFilters().Single(r => r.Id == routeId).RateLimitEnabled)
            .Should().BeFalse();
    }

    [Fact]
    public async Task An_anonymous_request_is_sent_to_sign_in_rather_than_toggling_anything()
    {
        var (appId, _) = GivenAppWithDomain("arl-anon-app", "arl-anon.example.com");

        var response = await Panel.ClientFrom("203.0.113.134")
            .PostFormWithoutTokenAsync($"/Apps/SetRateLimit/{appId}");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.Apps.IgnoreQueryFilters().Single(a => a.Id == appId).RateLimitEnabled)
            .Should().BeFalse();
    }

    // ---- input validation reaches the real HTTP path too ----

    [Fact]
    public async Task An_out_of_range_average_is_refused_and_leaves_the_route_unlimited()
    {
        Panel.GivenUser(fixture.WorkspaceId, "arl-badinput@example.com", SystemRole.Operator);
        var (appId, routeId) = GivenAppWithDomain("arl-badinput-app", "arl-badinput.example.com");
        var client = await Panel.SignedInAs("203.0.113.135", "arl-badinput@example.com");
        var token = await client.AntiforgeryTokenFrom($"/apps/details/{appId}");

        var response = await client.PostFormAsync($"/Apps/SetRateLimit/{appId}", token,
            ("rateLimitAverage", "0"), ("rateLimitBurst", "150"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.Apps.IgnoreQueryFilters().Single(a => a.Id == appId).RateLimitEnabled)
            .Should().BeFalse("an invalid number must never be accepted as though it turned the limit on");
        Panel.Read(db => db.Routes.IgnoreQueryFilters().Single(r => r.Id == routeId).RateLimitEnabled)
            .Should().BeFalse();
    }

    // ---- the view itself: data attributes, never a sentence, per the panel's Persian-by-default tests ----

    [Fact]
    public async Task The_apps_page_shows_the_current_setting_through_data_attributes()
    {
        Panel.GivenUser(fixture.WorkspaceId, "arl-view@example.com", SystemRole.Operator);
        var (appId, _) = GivenAppWithDomain("arl-view-app", "arl-view.example.com");
        var client = await Panel.SignedInAs("203.0.113.136", "arl-view@example.com");
        await client.PostFormAsync($"/Apps/SetRateLimit/{appId}",
            await client.AntiforgeryTokenFrom($"/apps/details/{appId}"),
            ("rateLimitAverage", "300"), ("rateLimitBurst", "150"));

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-ratelimit-section");
        html.Should().Contain("data-ratelimit-enabled=\"true\"");
        html.Should().Contain("data-ratelimit-average=\"300\"");
        html.Should().Contain("data-ratelimit-burst=\"150\"");
    }

    [Fact]
    public async Task A_freshly_created_apps_page_shows_rate_limiting_off()
    {
        Panel.GivenUser(fixture.WorkspaceId, "arl-view-off@example.com", SystemRole.Operator);
        var (appId, _) = GivenAppWithDomain("arl-view-off-app", "arl-view-off.example.com");
        var client = await Panel.SignedInAs("203.0.113.137", "arl-view-off@example.com");

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-ratelimit-enabled=\"false\"");
    }
}
