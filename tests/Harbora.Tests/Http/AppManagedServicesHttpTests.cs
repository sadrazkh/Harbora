using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Databases attaching to apps end to end (C1, 2026-08-22 config-delivery plan) — the real pipeline
/// routes, a real cookie, real Razor. Mirrors <see cref="StorageBucketsHttpTests"/>: provenance is
/// never hidden on the app's own env page, a secret entry stays masked, and deleting an attached
/// database refuses with the named list (the <c>ProjectsController.Delete</c> idiom
/// <c>StorageController.Delete</c> already reused for buckets, now reused a third time). Precedence
/// and the actual container environment are proven at the assembly seam by
/// <c>AppManagedServiceMergeTests</c>/<c>AppManagedServicePipelineTests</c>; this class proves the same
/// facts reach the pages a person actually reads.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AppManagedServicesHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    // ConfirmRemove.cshtml (unlike the shared _Shell banner StorageBucketsHttpTests reads) renders
    // TempData["Error"] itself, in a bg-danger-soft div rather than an alert-danger one.
    private static readonly Regex ErrorBanner = new(
        """<div class="mb-4 rounded-lg bg-danger-soft[^>]*>(?<text>.*?)</div>""", RegexOptions.Singleline | RegexOptions.Compiled);

    private static string ErrorBannerText(string html)
    {
        var match = ErrorBanner.Match(html);
        match.Success.Should().BeTrue("a refused delete must render the TempData[\"Error\"] banner");
        return match.Groups["text"].Value;
    }

    private App SeedApp(string slug)
    {
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId, ServerId = Guid.CreateVersion7(),
            EnvironmentId = fixture.DefaultEnvironmentId,
            Name = slug, Slug = slug, SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0", Status = AppStatus.Running
        };
        Panel.Seed(db => db.Apps.Add(app));
        return app;
    }

    private ManagedService SeedDatabase(string name)
    {
        var protector = Panel.Resolve<ISecretProtector>();
        var service = new ManagedService
        {
            WorkspaceId = fixture.WorkspaceId, EnvironmentId = fixture.DefaultEnvironmentId,
            ServerId = Guid.CreateVersion7(), Name = name, Type = ManagedServiceType.PostgreSql,
            Version = "16", Status = ServiceStatus.Running, ContainerName = "harbora-svc-" + name,
            InternalPort = 5432, Username = "harbora", DatabaseName = name,
            VolumeName = "harbora-svc-" + name + "-data",
            EncryptedPassword = protector.Protect("ams-http-password-01")
        };
        Panel.Seed(db => db.ManagedServices.Add(service));
        return service;
    }

    [Fact]
    public async Task Attaching_a_database_makes_its_connection_string_show_on_the_apps_env_page_with_provenance()
    {
        var app = SeedApp("api");
        var svc = SeedDatabase("orders");
        Panel.GivenUser(fixture.WorkspaceId, "ams-attach@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.250", "ams-attach@example.com");

        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var attach = await client.PostFormAsync($"/databases/{svc.Id}/attach", token,
            ("appId", app.Id.ToString()));
        attach.StatusCode.Should().Be(HttpStatusCode.Found);

        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();
        html.Should().Contain("DATABASE_URL");
        html.Should().Contain("PGHOST");
        html.Should().Contain("data-env-source=\"database\"", "a database-provided row must say it came from a database, not the app or a group");
        html.Should().Contain("orders", "the row must name the specific database it came from");
    }

    [Fact]
    public async Task An_explicit_alias_becomes_the_prefix_the_connection_string_reaches_the_page_under()
    {
        var app = SeedApp("aliased-app");
        var svc = SeedDatabase("billing");
        Panel.GivenUser(fixture.WorkspaceId, "ams-alias@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.251", "ams-alias@example.com");

        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        await client.PostFormAsync($"/databases/{svc.Id}/attach", token,
            ("appId", app.Id.ToString()), ("alias", "PRIMARY"));

        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();
        html.Should().Contain("PRIMARY_DATABASE_URL",
            "the alias typed on the attach form must be the prefix the app actually receives, not the service's own name");
    }

    [Fact]
    public async Task The_databases_secret_entries_stay_masked_on_the_apps_env_page()
    {
        var app = SeedApp("secretive");
        var svc = SeedDatabase("with-secret");
        Panel.GivenUser(fixture.WorkspaceId, "ams-mask@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.252", "ams-mask@example.com");

        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        await client.PostFormAsync($"/databases/{svc.Id}/attach", token, ("appId", app.Id.ToString()));

        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();
        html.Should().Contain("PGPASSWORD");
        html.Should().NotContain("ams-http-password-01", "the plaintext password must never reach the page");
        html.Should().Contain("&#x2022;&#x2022;&#x2022;&#x2022;&#x2022;&#x2022;",
            "a database's secret entries mask with the same bullet every other secret env var uses");
    }

    [Fact]
    public async Task Detaching_a_database_removes_its_keys_from_the_apps_effective_env_page()
    {
        var app = SeedApp("detach-me");
        var svc = SeedDatabase("goes-away");
        Panel.GivenUser(fixture.WorkspaceId, "ams-detach@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.253", "ams-detach@example.com");

        var attachToken = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        await client.PostFormAsync($"/databases/{svc.Id}/attach", attachToken, ("appId", app.Id.ToString()));
        (await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync())
            .Should().Contain("DATABASE_URL");

        var detachToken = await client.AntiforgeryTokenFrom($"/apps/details/{app.Id}");
        var detach = await client.PostFormAsync($"/databases/{svc.Id}/detach", detachToken,
            ("appId", app.Id.ToString()), ("returnUrl", $"/apps/details/{app.Id}"));
        detach.StatusCode.Should().Be(HttpStatusCode.Found);

        Panel.Read(db => db.AppManagedServices.Any(x => x.AppId == app.Id && x.ManagedServiceId == svc.Id)).Should().BeFalse();
        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();
        html.Should().NotContain("data-env-source=\"database\"");
    }

    [Fact]
    public async Task Deleting_a_database_still_attached_to_an_app_is_refused_and_names_the_app()
    {
        var app = SeedApp("checkout");
        var svc = SeedDatabase("attached-db");
        Panel.GivenUser(fixture.WorkspaceId, "ams-delete-refused@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.254", "ams-delete-refused@example.com");

        var attachToken = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        await client.PostFormAsync($"/databases/{svc.Id}/attach", attachToken, ("appId", app.Id.ToString()));

        var removeToken = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var response = await client.PostFormAsync($"/databases/{svc.Id}/remove", removeToken, ("deleteData", "false"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var html = await (await client.GetAsync(response.RedirectPath()!)).Content.ReadAsStringAsync();
        ErrorBannerText(html).Should().Contain("checkout",
            "the refusal must name the app blocking the delete, not merely count it");

        Panel.Read(db => db.ManagedServices.Any(s => s.Id == svc.Id)).Should().BeTrue(
            "the database must still exist — the delete was refused, not silently applied anyway");
    }

    [Fact]
    public async Task A_viewer_cannot_attach_a_database_to_an_app()
    {
        var app = SeedApp("viewer-app");
        var svc = SeedDatabase("viewer-db");
        Panel.GivenUser(fixture.WorkspaceId, "ams-viewer@example.com", SystemRole.Viewer);
        var client = await Panel.SignedInAs("203.0.113.255", "ams-viewer@example.com");

        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var attachResponse = await client.PostFormAsync($"/databases/{svc.Id}/attach", token,
            ("appId", app.Id.ToString()));
        attachResponse.RedirectPath().Should().Be("/account/denied");
        Panel.Read(db => db.AppManagedServices.Any(x => x.AppId == app.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Two_databases_attached_to_the_same_app_both_stay_reachable_under_distinct_names()
    {
        var app = SeedApp("multi-db");
        var orders = SeedDatabase("orders-multi");
        var customers = SeedDatabase("customers-multi");
        Panel.GivenUser(fixture.WorkspaceId, "ams-multi@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.256", "ams-multi@example.com");

        var t1 = await client.AntiforgeryTokenFrom($"/databases/{orders.Id}");
        await client.PostFormAsync($"/databases/{orders.Id}/attach", t1, ("appId", app.Id.ToString()));
        var t2 = await client.AntiforgeryTokenFrom($"/databases/{customers.Id}");
        await client.PostFormAsync($"/databases/{customers.Id}/attach", t2, ("appId", app.Id.ToString()));

        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();
        html.Should().Contain("ORDERS_MULTI_DATABASE_URL");
        html.Should().Contain("CUSTOMERS_MULTI_DATABASE_URL");
    }
}
