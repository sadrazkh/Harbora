using System.Net;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Networking;
using Harbora.Domain.Settings;
using Harbora.Domain.Status;
using Harbora.Infrastructure.Proxy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The status page's own two hosts — the platform subdomain and a customer's custom domain — proven
/// through real HTTP requests against the real <see cref="TraefikProxyEngine"/> (sub-project 8,
/// 2026-08-20 platform-options plan). <see cref="HarboraWebFactory"/> does not substitute
/// <c>IProxyEngine</c>, so every assertion on router content here is read back from the YAML file on
/// disk, not merely a database row — the same idiom <c>MaintenanceModeHttpTests</c> established.
///
/// <para>
/// Sub-project 7 shipped the anonymous route and its host-regex middleware but left the platform
/// subdomain unrouted in production Traefik — this file is the proof that gap is closed, and that
/// attaching a custom domain walks the identical <c>Route</c>/<c>IProxyEngine</c> writer rather than a
/// second one.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class StatusPageDomainHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;
    private const string RootDomain = "apps.example.test";

    private (Guid WorkspaceId, Guid EnvironmentId, string PlatformHost) GivenWorkspace(string slug)
    {
        var workspaceId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.Workspaces.Add(new Workspace { Id = workspaceId, Name = slug, Slug = slug });
            db.Projects.Add(new Harbora.Domain.Projects.Project
            {
                Id = projectId, WorkspaceId = workspaceId, Name = "App", Slug = "app"
            });
            db.Environments.Add(new Harbora.Domain.Projects.Environment
            {
                Id = environmentId, WorkspaceId = workspaceId, ProjectId = projectId,
                Name = "Production", Slug = "production", IsDefault = true
            });

            var setting = db.Settings.FirstOrDefault(s => s.Key == SettingKeys.PlatformRootDomain);
            if (setting is null)
                db.Settings.Add(new Setting { Key = SettingKeys.PlatformRootDomain, Value = RootDomain });
            else
                setting.Value = RootDomain;
        });
        return (workspaceId, environmentId, $"status-{slug}.{RootDomain}");
    }

    private string TraefikYaml() =>
        File.ReadAllText(Panel.Resolve<IOptions<TraefikOptions>>().Value.DynamicConfigPath);

    private static string RouterNameFor(Guid routeId) => "r-" + routeId.ToString("N")[..12];

    // ---- the platform subdomain now genuinely routes ------------------------------------------

    [Fact]
    public async Task Enabling_writes_a_router_for_the_platform_subdomain_pointed_at_the_panel()
    {
        var (workspaceId, _, host) = GivenWorkspace("routes-platform");
        Panel.GivenUser(workspaceId, "sp8-enable@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.40", "sp8-enable@example.com");
        await client.GetAsync("/status-page");

        var token = await client.AntiforgeryTokenFrom("/status-page");
        var response = await client.PostFormAsync("/status-page/enable", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var route = Panel.Read(db => db.Routes.IgnoreQueryFilters().Single(r => r.Host == host));
        route.AppId.Should().BeNull();
        route.TargetService.Should().Be("harbora-panel");
        route.TargetPort.Should().Be(8080);

        var yaml = TraefikYaml();
        yaml.Should().Contain(RouterNameFor(route.Id)).And.Contain("harbora-panel:8080")
            .And.Contain($"Host(`{host}`)");
    }

    [Fact]
    public async Task Disabling_removes_the_platform_subdomains_router_from_the_rendered_config()
    {
        var (workspaceId, _, host) = GivenWorkspace("unroutes-platform");
        Panel.GivenUser(workspaceId, "sp8-disable@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.41", "sp8-disable@example.com");
        await client.GetAsync("/status-page");
        var enableToken = await client.AntiforgeryTokenFrom("/status-page");
        await client.PostFormAsync("/status-page/enable", enableToken);
        var routeId = Panel.Read(db => db.Routes.IgnoreQueryFilters().Single(r => r.Host == host).Id);

        var disableToken = await client.AntiforgeryTokenFrom("/status-page");
        var response = await client.PostFormAsync("/status-page/disable", disableToken);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.Routes.IgnoreQueryFilters().Any(r => r.Host == host)).Should().BeFalse();
        TraefikYaml().Should().NotContain(RouterNameFor(routeId),
            "removal must leave no router behind in the rendered config, not merely delete the row");
    }

    // ---- the custom domain walks the same flow -------------------------------------------------

    [Fact]
    public async Task Attaching_a_custom_domain_renders_a_router_with_a_cert_resolver_through_the_real_engine()
    {
        var (workspaceId, _, _) = GivenWorkspace("attach-renders");
        Panel.GivenUser(workspaceId, "sp8-attach@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.42", "sp8-attach@example.com");
        await client.GetAsync("/status-page");

        var token = await client.AntiforgeryTokenFrom("/status-page");
        var response = await client.PostFormAsync("/status-page/domain", token, ("host", "status.attach-renders.example"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var domain = Panel.Read(db => db.Domains.IgnoreQueryFilters().Single(d => d.Host == "status.attach-renders.example"));
        domain.AppId.Should().BeNull();
        domain.StatusPageId.Should().NotBeNull();

        var route = Panel.Read(db => db.Routes.IgnoreQueryFilters().Single(r => r.Host == "status.attach-renders.example"));
        route.TargetService.Should().Be("harbora-panel");

        var yaml = TraefikYaml();
        yaml.Should().Contain(RouterNameFor(route.Id))
            .And.Contain("harbora-panel:8080")
            .And.Contain("certResolver:", "SslEnabled is on, the same as any app domain the designer creates");
    }

    [Fact]
    public async Task Both_hosts_serve_the_page_once_a_custom_domain_is_attached()
    {
        var (workspaceId, _, platformHost) = GivenWorkspace("both-hosts-serve");
        Panel.GivenUser(workspaceId, "sp8-both@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.43", "sp8-both@example.com");
        await client.GetAsync("/status-page");
        await client.PostFormAsync("/status-page/enable", await client.AntiforgeryTokenFrom("/status-page"));
        await client.PostFormAsync("/status-page/domain",
            await client.AntiforgeryTokenFrom("/status-page"), ("host", "status.both-hosts.example"));

        var visitor = Panel.ClientFrom("203.0.113.44");
        var viaPlatform = await visitor.GetWithHostAsync("/", platformHost);
        var viaCustom = await visitor.GetWithHostAsync("/", "status.both-hosts.example");

        viaPlatform.StatusCode.Should().Be(HttpStatusCode.OK);
        viaCustom.StatusCode.Should().Be(HttpStatusCode.OK,
            "StatusPageHostMiddleware's custom-domain lookup must resolve this host to the same workspace");
        (await viaPlatform.Content.ReadAsStringAsync()).Should().Contain("both-hosts-serve");
        (await viaCustom.Content.ReadAsStringAsync()).Should().Contain("both-hosts-serve");
    }

    [Fact]
    public async Task Removing_the_custom_domain_leaves_no_router_behind_and_the_host_stops_answering()
    {
        var (workspaceId, _, platformHost) = GivenWorkspace("detach-cleanly");
        Panel.GivenUser(workspaceId, "sp8-detach@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.45", "sp8-detach@example.com");
        await client.GetAsync("/status-page");
        await client.PostFormAsync("/status-page/enable", await client.AntiforgeryTokenFrom("/status-page"));
        await client.PostFormAsync("/status-page/domain",
            await client.AntiforgeryTokenFrom("/status-page"), ("host", "status.detach-cleanly.example"));
        var routeId = Panel.Read(db =>
            db.Routes.IgnoreQueryFilters().Single(r => r.Host == "status.detach-cleanly.example").Id);

        var response = await client.PostFormAsync("/status-page/domain/remove",
            await client.AntiforgeryTokenFrom("/status-page"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.Domains.IgnoreQueryFilters().Any(d => d.Host == "status.detach-cleanly.example"))
            .Should().BeFalse();
        Panel.Read(db => db.Routes.IgnoreQueryFilters().Any(r => r.Host == "status.detach-cleanly.example"))
            .Should().BeFalse();
        TraefikYaml().Should().NotContain(RouterNameFor(routeId),
            "assert on the rendered proxy configuration that the route is gone, not merely that the row was deleted");

        // A host nothing recognises is not rewritten at all, so it falls through to whatever ordinary
        // routing answers "/" with (a marketing page, here) rather than a 404 from StatusPageController
        // — the same shape a browser would see once Traefik itself no longer has a router for this
        // host at all. What proves detachment is that the status page's own content is gone, not the
        // raw status code of an arbitrary Host header hitting Kestrel directly.
        var visitor = Panel.ClientFrom("203.0.113.46");
        var afterRemoval = await visitor.GetWithHostAsync("/", "status.detach-cleanly.example");
        (await afterRemoval.Content.ReadAsStringAsync()).Should().NotContain("data-status-page",
            "the middleware's custom-domain lookup must find nothing now, so this host no longer resolves to the status page");

        var stillPlatform = await visitor.GetWithHostAsync("/", platformHost);
        stillPlatform.StatusCode.Should().Be(HttpStatusCode.OK,
            "the platform subdomain keeps working after the custom domain is detached");
        (await stillPlatform.Content.ReadAsStringAsync()).Should().Contain("data-status-page");
    }

    // ---- refusals -------------------------------------------------------------------------------

    [Fact]
    public async Task Attaching_the_platforms_own_reserved_status_prefix_is_refused()
    {
        var (workspaceId, _, _) = GivenWorkspace("refuse-reserved-prefix");
        Panel.GivenUser(workspaceId, "sp8-reserved@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.47", "sp8-reserved@example.com");
        await client.GetAsync("/status-page");

        var token = await client.AntiforgeryTokenFrom("/status-page");
        var response = await client.PostFormAsync("/status-page/domain", token,
            ("host", $"status-somebody-elses-workspace.{RootDomain}"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.Domains.IgnoreQueryFilters()
            .Any(d => d.Host == $"status-somebody-elses-workspace.{RootDomain}")).Should().BeFalse();
    }

    /// <summary>
    /// Discovered while building this sub-project: <c>AppsController.AddDomain</c>'s own reserved-host
    /// guard only ever called <c>ReservedHosts.IsReserved</c> (the platform's exact own names), never
    /// <c>ReservedHosts.IsReservedPrefix</c> — so an app could claim <c>status-anything.&lt;root
    /// domain&gt;</c> by typing it into "Add domain", even though <c>AppAddress.Decide</c> (the
    /// create-app path) already refuses the identical string. Closed alongside this sub-project's own
    /// use of the same prefix rather than left for someone to find it re-broken by drift.
    /// </summary>
    [Fact]
    public async Task An_app_cannot_claim_the_status_prefix_through_AddDomain_either()
    {
        var (workspaceId, environmentId, _) = GivenWorkspace("refuse-app-claims-prefix");
        var appId = Guid.CreateVersion7();
        Panel.Seed(db => db.Apps.Add(new App
        {
            Id = appId, WorkspaceId = workspaceId, EnvironmentId = environmentId,
            Name = "web", Slug = "refuse-app-claims-prefix-app", SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/web:1.0"
        }));
        Panel.GivenUser(workspaceId, "sp8-app-prefix@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.53", "sp8-app-prefix@example.com");

        var token = await client.AntiforgeryTokenFrom($"/apps/details/{appId}");
        var response = await client.PostFormAsync($"/apps/{appId}/domains", token,
            ("host", $"status-squatting-attempt.{RootDomain}"), ("ssl", "true"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.Domains.IgnoreQueryFilters()
            .Any(d => d.Host == $"status-squatting-attempt.{RootDomain}")).Should().BeFalse(
            "the status- prefix under the platform's root domain is reserved for status pages, the same rule AppAddress.Decide already enforces on app creation");
    }

    [Fact]
    public async Task Attaching_a_host_already_used_by_an_app_is_refused()
    {
        var (workspaceId, environmentId, _) = GivenWorkspace("refuse-taken-host");
        var appId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.Apps.Add(new App
            {
                Id = appId, WorkspaceId = workspaceId, EnvironmentId = environmentId,
                Name = "web", Slug = "refuse-taken-host-app", SourceType = AppSourceType.PrebuiltImage,
                PrebuiltImage = "ghcr.io/example/web:1.0"
            });
            db.Domains.Add(new DomainName { AppId = appId, Host = "already-an-app.example.com", IsPrimary = true });
        });
        Panel.GivenUser(workspaceId, "sp8-taken@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.48", "sp8-taken@example.com");
        await client.GetAsync("/status-page");

        var token = await client.AntiforgeryTokenFrom("/status-page");
        var response = await client.PostFormAsync("/status-page/domain", token, ("host", "already-an-app.example.com"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.Domains.IgnoreQueryFilters()
            .Count(d => d.Host == "already-an-app.example.com")).Should().Be(1, "the app's own row, untouched");
    }

    [Fact]
    public async Task Attaching_a_second_domain_while_one_is_already_attached_is_refused()
    {
        var (workspaceId, _, _) = GivenWorkspace("refuse-second-domain");
        Panel.GivenUser(workspaceId, "sp8-second@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.49", "sp8-second@example.com");
        await client.GetAsync("/status-page");
        await client.PostFormAsync("/status-page/domain",
            await client.AntiforgeryTokenFrom("/status-page"), ("host", "status.first.example"));

        var response = await client.PostFormAsync("/status-page/domain",
            await client.AntiforgeryTokenFrom("/status-page"), ("host", "status.second.example"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.Domains.IgnoreQueryFilters().Any(d => d.Host == "status.second.example")).Should().BeFalse();
        Panel.Read(db => db.Domains.IgnoreQueryFilters().Any(d => d.Host == "status.first.example")).Should().BeTrue();
    }

    [Fact]
    public async Task A_member_without_alerts_manage_cannot_attach_a_domain()
    {
        var (workspaceId, _, _) = GivenWorkspace("refuse-member-attach");
        Panel.GivenUser(workspaceId, "sp8-member@example.com", SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.50", "sp8-member@example.com");

        var token = await client.AntiforgeryTokenFrom("/status-page");
        var response = await client.PostFormAsync("/status-page/domain", token, ("host", "status.member-denied.example"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/denied");
        Panel.Read(db => db.Domains.IgnoreQueryFilters().Any(d => d.Host == "status.member-denied.example"))
            .Should().BeFalse();
    }

    // ---- tenancy ----------------------------------------------------------------------------------

    [Fact]
    public async Task Removing_one_workspaces_domain_never_touches_another_workspaces()
    {
        var (workspaceA, _, _) = GivenWorkspace("tenancy-remover-a");
        var (workspaceB, _, _) = GivenWorkspace("tenancy-remover-b");
        Panel.GivenUser(workspaceA, "sp8-tenancy-a@example.com", SystemRole.Owner);
        Panel.GivenUser(workspaceB, "sp8-tenancy-b@example.com", SystemRole.Owner);
        var clientA = await Panel.SignedInAs("203.0.113.51", "sp8-tenancy-a@example.com");
        var clientB = await Panel.SignedInAs("203.0.113.52", "sp8-tenancy-b@example.com");
        await clientA.GetAsync("/status-page");
        await clientB.GetAsync("/status-page");
        await clientA.PostFormAsync("/status-page/domain",
            await clientA.AntiforgeryTokenFrom("/status-page"), ("host", "status.tenant-a-http.example"));
        await clientB.PostFormAsync("/status-page/domain",
            await clientB.AntiforgeryTokenFrom("/status-page"), ("host", "status.tenant-b-http.example"));

        var response = await clientB.PostFormAsync("/status-page/domain/remove",
            await clientB.AntiforgeryTokenFrom("/status-page"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.Domains.IgnoreQueryFilters().Any(d => d.Host == "status.tenant-b-http.example"))
            .Should().BeFalse();
        Panel.Read(db => db.Domains.IgnoreQueryFilters().Any(d => d.Host == "status.tenant-a-http.example"))
            .Should().BeTrue("workspace B removing its own domain must not touch workspace A's");
    }
}
