using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 4.2 (round-2 market-gaps plan): Meilisearch as a managed service, proven through the real pipeline
/// routes rather than unit-testing the catalogue in isolation — mirrors
/// <see cref="AppManagedServicesHttpTests"/>, whose provenance/masking/refusal proofs this reuses
/// unchanged, just against a Meilisearch-typed <see cref="ManagedService"/> instead of PostgreSQL, to
/// confirm the generic attach/detach/delete machinery actually treats the new engine the same way
/// rather than only "in theory" because the code path is shared.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class MeilisearchHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

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

    private ManagedService SeedMeilisearch(string name, string masterKey = "meili-http-master-key-01")
    {
        var protector = Panel.Resolve<ISecretProtector>();
        var def = ServiceCatalog.All[ManagedServiceType.Meilisearch];
        var service = new ManagedService
        {
            WorkspaceId = fixture.WorkspaceId, EnvironmentId = fixture.DefaultEnvironmentId,
            ServerId = Guid.CreateVersion7(), Name = name, Type = ManagedServiceType.Meilisearch,
            Version = def.Versions[0], Status = ServiceStatus.Running, ContainerName = "harbora-svc-" + name,
            InternalPort = def.Port, Username = "harbora", DatabaseName = string.Empty,
            VolumeName = "harbora-svc-" + name + "-data",
            EncryptedPassword = protector.Protect(masterKey)
        };
        Panel.Seed(db => db.ManagedServices.Add(service));
        return service;
    }

    [Fact]
    public async Task Attaching_a_meilisearch_instance_shows_MEILI_star_on_the_apps_env_page_with_provenance()
    {
        var app = SeedApp("search-consumer");
        var svc = SeedMeilisearch("catalog-search");
        Panel.GivenUser(fixture.WorkspaceId, "meili-attach@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.160", "meili-attach@example.com");

        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var attach = await client.PostFormAsync($"/databases/{svc.Id}/attach", token,
            ("appId", app.Id.ToString()));
        attach.StatusCode.Should().Be(HttpStatusCode.Found);

        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();
        html.Should().Contain("MEILI_MASTER_KEY");
        html.Should().Contain("MEILI_URL");
        html.Should().Contain("data-env-source=\"database\"", "a Meilisearch attachment must say it came from a database, not the app or a group");
        html.Should().Contain("catalog-search", "the row must name the specific instance it came from");
    }

    [Fact]
    public async Task The_master_key_stays_masked_on_the_apps_env_page_until_revealed()
    {
        var app = SeedApp("search-secretive");
        var svc = SeedMeilisearch("secretive-search", masterKey: "meili-http-secret-value-02");
        Panel.GivenUser(fixture.WorkspaceId, "meili-mask@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.161", "meili-mask@example.com");

        var token = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        await client.PostFormAsync($"/databases/{svc.Id}/attach", token, ("appId", app.Id.ToString()));

        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();
        html.Should().Contain("MEILI_MASTER_KEY");
        html.Should().NotContain("meili-http-secret-value-02", "the plaintext master key must never reach the page");
        html.Should().Contain("&#x2022;&#x2022;&#x2022;&#x2022;&#x2022;&#x2022;",
            "a Meilisearch attachment's master key masks with the same bullet every other secret env var uses");
    }

    [Fact]
    public async Task Detaching_a_meilisearch_instance_removes_its_keys_from_the_apps_env_page()
    {
        var app = SeedApp("search-detach-me");
        var svc = SeedMeilisearch("goes-away-search");
        Panel.GivenUser(fixture.WorkspaceId, "meili-detach@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.162", "meili-detach@example.com");

        var attachToken = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        await client.PostFormAsync($"/databases/{svc.Id}/attach", attachToken, ("appId", app.Id.ToString()));
        (await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync())
            .Should().Contain("MEILI_MASTER_KEY");

        var detachToken = await client.AntiforgeryTokenFrom($"/apps/details/{app.Id}");
        var detach = await client.PostFormAsync($"/databases/{svc.Id}/detach", detachToken,
            ("appId", app.Id.ToString()), ("returnUrl", $"/apps/details/{app.Id}"));
        detach.StatusCode.Should().Be(HttpStatusCode.Found);

        Panel.Read(db => db.AppManagedServices.Any(x => x.AppId == app.Id && x.ManagedServiceId == svc.Id)).Should().BeFalse();
        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();
        html.Should().NotContain("MEILI_MASTER_KEY");
    }

    [Fact]
    public async Task Deleting_a_meilisearch_instance_still_attached_to_an_app_is_refused_and_names_the_app()
    {
        var app = SeedApp("search-checkout");
        var svc = SeedMeilisearch("attached-search");
        Panel.GivenUser(fixture.WorkspaceId, "meili-delete-refused@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.163", "meili-delete-refused@example.com");

        var attachToken = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        await client.PostFormAsync($"/databases/{svc.Id}/attach", attachToken, ("appId", app.Id.ToString()));

        var removeToken = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var response = await client.PostFormAsync($"/databases/{svc.Id}/remove", removeToken, ("deleteData", "false"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var html = await (await client.GetAsync(response.RedirectPath()!)).Content.ReadAsStringAsync();
        ErrorBannerText(html).Should().Contain("search-checkout",
            "the refusal must name the app blocking the delete, not merely count it");

        Panel.Read(db => db.ManagedServices.Any(s => s.Id == svc.Id)).Should().BeTrue(
            "the instance must still exist — the delete was refused, not silently applied anyway");
    }

    [Fact]
    public async Task An_unattached_meilisearch_instance_deletes_cleanly()
    {
        var svc = SeedMeilisearch("lonely-search");
        Panel.GivenUser(fixture.WorkspaceId, "meili-delete-ok@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.164", "meili-delete-ok@example.com");

        var removeToken = await client.AntiforgeryTokenFrom($"/databases/{svc.Id}");
        var response = await client.PostFormAsync($"/databases/{svc.Id}/remove", removeToken, ("deleteData", "false"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.ManagedServices.Any(s => s.Id == svc.Id)).Should().BeFalse(
            "nothing was attached, so nothing should have refused the delete");
    }

    [Fact]
    public async Task The_create_form_offers_meilisearch_as_an_engine()
    {
        Panel.GivenUser(fixture.WorkspaceId, "meili-create-form@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.165", "meili-create-form@example.com");

        var html = await (await client.GetAsync("/databases/create")).Content.ReadAsStringAsync();
        html.Should().Contain("Meilisearch");
        html.Should().Contain("data-template-key=\"meilisearch\"",
            "the engine card must draw the real mark rather than falling back to the generic database one");
    }
}
