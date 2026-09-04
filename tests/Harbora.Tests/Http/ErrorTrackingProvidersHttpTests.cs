using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.ErrorTracking;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// BYO Sentry/GlitchTip DSNs attaching to apps end to end (1.8, 2026-09 market-gaps round two) — the
/// real pipeline routes, a real cookie, real Razor. Mirrors <see cref="EmailProvidersHttpTests"/>
/// (F6) exactly: provenance is never hidden on the app's own env page, a secret entry stays masked,
/// and deleting an attached provider refuses with the named list. Precedence and the actual container
/// environment are proven at the assembly seam by
/// <see cref="ErrorTrackingProviderMergeTests"/>/<see cref="ErrorTrackingProviderPipelineTests"/>;
/// this class proves the same facts reach the pages a person actually reads.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class ErrorTrackingProvidersHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private static readonly Regex ErrorBanner = new(
        """<div class="alert-danger[^>]*>(?<text>.*?)</div>""", RegexOptions.Singleline | RegexOptions.Compiled);

    private static string ErrorBannerText(string html)
    {
        var match = ErrorBanner.Match(html);
        match.Success.Should().BeTrue("a refused delete must render the TempData[\"Error\"] banner");
        return match.Groups["text"].Value;
    }

    private Guid SeedApp(string slug)
    {
        var projectId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId, ServerId = Guid.CreateVersion7(), EnvironmentId = environmentId,
            Name = slug, Slug = slug, SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0", Status = AppStatus.Running
        };
        Panel.Seed(db =>
        {
            db.Projects.Add(new Harbora.Domain.Projects.Project
            {
                Id = projectId, WorkspaceId = fixture.WorkspaceId, Name = "Shop", Slug = "et-" + slug
            });
            db.Environments.Add(new Harbora.Domain.Projects.Environment
            {
                Id = environmentId, WorkspaceId = fixture.WorkspaceId, ProjectId = projectId,
                Name = "Production", Slug = "production", IsDefault = true
            });
            db.Apps.Add(app);
        });
        return app.Id;
    }

    private Guid SeedProvider(string name)
    {
        var provider = new ErrorTrackingProvider
        {
            WorkspaceId = fixture.WorkspaceId, Name = name, EncryptedDsn = "cipher:" + name
        };
        Panel.Seed(db => db.ErrorTrackingProviders.Add(provider));
        return provider.Id;
    }

    [Fact]
    public async Task Attaching_a_provider_makes_sentry_dsn_show_on_the_apps_env_page_with_provenance()
    {
        var appId = SeedApp("api");
        var providerId = SeedProvider("glitchtip");
        Panel.GivenUser(fixture.WorkspaceId, "et-attach@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.160", "et-attach@example.com");

        var token = await client.AntiforgeryTokenFrom("/error-tracking");
        var attach = await client.PostFormAsync($"/error-tracking/{providerId}/attach", token,
            ("appId", appId.ToString()));
        attach.StatusCode.Should().Be(HttpStatusCode.Found);

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();
        html.Should().Contain("SENTRY_DSN");
        html.Should().Contain("data-env-source=\"error-tracking\"", "an error-tracking row must say it came from error tracking, not the app or a group");
        html.Should().Contain("glitchtip", "the row must name the specific provider it came from");
        html.Should().Contain("data-attached-error-tracking-provider=\"glitchtip\"");
    }

    [Fact]
    public async Task The_providers_dsn_stays_masked_on_the_apps_env_page()
    {
        var appId = SeedApp("secretive");
        var providerId = SeedProvider("with-secret");
        Panel.Seed(db => db.AppErrorTrackingProviders.Add(new AppErrorTrackingProvider
        {
            AppId = appId, ErrorTrackingProviderId = providerId, AttachOrder = 1
        }));
        Panel.GivenUser(fixture.WorkspaceId, "et-mask@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.161", "et-mask@example.com");

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();

        html.Should().Contain("SENTRY_DSN");
        html.Should().NotContain("cipher:with-secret", "a provider's ciphertext must never reach the page either");
        html.Should().Contain("&#x2022;&#x2022;&#x2022;&#x2022;&#x2022;&#x2022;",
            "SENTRY_DSN masks with the same bullet every other secret env var uses");
    }

    [Fact]
    public async Task Detaching_a_provider_removes_sentry_dsn_from_the_apps_effective_env_page()
    {
        var appId = SeedApp("detach-me");
        var providerId = SeedProvider("goes-away");
        Panel.Seed(db => db.AppErrorTrackingProviders.Add(new AppErrorTrackingProvider
        {
            AppId = appId, ErrorTrackingProviderId = providerId, AttachOrder = 1
        }));
        Panel.GivenUser(fixture.WorkspaceId, "et-detach@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.162", "et-detach@example.com");

        (await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync())
            .Should().Contain("SENTRY_DSN");

        var token = await client.AntiforgeryTokenFrom($"/apps/details/{appId}");
        var detach = await client.PostFormAsync($"/error-tracking/{providerId}/detach", token,
            ("appId", appId.ToString()), ("returnUrl", $"/apps/details/{appId}"));
        detach.StatusCode.Should().Be(HttpStatusCode.Found);

        Panel.Read(db => db.AppErrorTrackingProviders.Any(x => x.AppId == appId && x.ErrorTrackingProviderId == providerId)).Should().BeFalse();
        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();
        html.Should().NotContain("SENTRY_DSN");
    }

    [Fact]
    public async Task Deleting_a_provider_still_attached_to_an_app_is_refused_and_names_the_app()
    {
        var appId = SeedApp("checkout");
        var providerId = SeedProvider("attached-provider");
        Panel.Seed(db => db.AppErrorTrackingProviders.Add(new AppErrorTrackingProvider
        {
            AppId = appId, ErrorTrackingProviderId = providerId, AttachOrder = 1
        }));
        Panel.GivenUser(fixture.WorkspaceId, "et-delete-refused@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.163", "et-delete-refused@example.com");

        var token = await client.AntiforgeryTokenFrom("/error-tracking");
        var response = await client.PostFormAsync($"/error-tracking/{providerId}/delete", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/error-tracking");

        var html = await (await client.GetAsync(response.RedirectPath())).Content.ReadAsStringAsync();
        ErrorBannerText(html).Should().Contain("checkout",
            "the refusal must name the app blocking the delete, not merely count it");

        Panel.Read(db => db.ErrorTrackingProviders.Any(p => p.Id == providerId)).Should().BeTrue(
            "the provider must still exist — the delete was refused, not silently applied anyway");
    }

    [Fact]
    public async Task An_unattached_provider_deletes_cleanly()
    {
        var providerId = SeedProvider("unattached-provider");
        Panel.GivenUser(fixture.WorkspaceId, "et-delete-clean@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.164", "et-delete-clean@example.com");

        var token = await client.AntiforgeryTokenFrom("/error-tracking");
        var response = await client.PostFormAsync($"/error-tracking/{providerId}/delete", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.ErrorTrackingProviders.Any(p => p.Id == providerId)).Should().BeFalse();
    }

    [Fact]
    public async Task A_viewer_cannot_attach_a_provider_to_an_app()
    {
        var appId = SeedApp("viewer-app");
        var providerId = SeedProvider("viewer-provider");
        Panel.GivenUser(fixture.WorkspaceId, "et-viewer@example.com", SystemRole.Viewer);
        var client = await Panel.SignedInAs("203.0.113.165", "et-viewer@example.com");

        var token = await client.AntiforgeryTokenFrom("/error-tracking");
        var attachResponse = await client.PostFormAsync($"/error-tracking/{providerId}/attach", token,
            ("appId", appId.ToString()));
        attachResponse.RedirectPath().Should().Be("/account/denied");
        Panel.Read(db => db.AppErrorTrackingProviders.Any(x => x.AppId == appId)).Should().BeFalse();
    }

    [Fact]
    public async Task No_providers_or_attachments_cross_workspaces()
    {
        var otherWorkspaceId = Guid.CreateVersion7();
        var theirProviderId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.Workspaces.Add(new Harbora.Domain.Identity.Workspace { Id = otherWorkspaceId, Name = "Other", Slug = "other-et-ws" });
            db.ErrorTrackingProviders.Add(new ErrorTrackingProvider
            {
                Id = theirProviderId, WorkspaceId = otherWorkspaceId, Name = "not-yours", EncryptedDsn = "cipher:other"
            });
        });
        var appId = SeedApp("tenancy-app");
        Panel.GivenUser(fixture.WorkspaceId, "et-tenancy@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.166", "et-tenancy@example.com");

        var html = await (await client.GetAsync("/error-tracking")).Content.ReadAsStringAsync();
        html.Should().NotContain("not-yours");

        var token = await client.AntiforgeryTokenFrom("/error-tracking");
        var attachAttempt = await client.PostFormAsync($"/error-tracking/{theirProviderId}/attach", token,
            ("appId", appId.ToString()));
        attachAttempt.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "another workspace's provider id must not resolve, even for a signed-in owner of a different workspace");

        var deleteAttempt = await client.PostFormAsync($"/error-tracking/{theirProviderId}/delete", token);
        deleteAttempt.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Panel.Read(db => db.ErrorTrackingProviders.Any(p => p.Id == theirProviderId)).Should().BeTrue("the other workspace's row must be untouched");
    }

    [Fact]
    public async Task Creating_a_provider_encrypts_the_dsn_at_rest()
    {
        Panel.GivenUser(fixture.WorkspaceId, "et-create@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.167", "et-create@example.com");

        var token = await client.AntiforgeryTokenFrom("/error-tracking");
        var response = await client.PostFormAsync("/error-tracking", token,
            ("name", "GlitchTip"), ("dsn", "https://raw-key-value@glitchtip.example/1"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var stored = Panel.Read(db => db.ErrorTrackingProviders.Single(p => p.Name == "GlitchTip"));
        stored.WorkspaceId.Should().Be(fixture.WorkspaceId);
        stored.EncryptedDsn.Should().NotBe("https://raw-key-value@glitchtip.example/1", "the DSN must be encrypted, not stored as typed");
    }

    [Fact]
    public async Task Creating_a_provider_with_a_malformed_dsn_is_refused()
    {
        Panel.GivenUser(fixture.WorkspaceId, "et-bad-dsn@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.168", "et-bad-dsn@example.com");

        var token = await client.AntiforgeryTokenFrom("/error-tracking");
        var response = await client.PostFormAsync("/error-tracking", token,
            ("name", "Bad"), ("dsn", "not-a-dsn"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var html = await (await client.GetAsync(response.RedirectPath())).Content.ReadAsStringAsync();
        html.Should().Contain("alert-danger");
        Panel.Read(db => db.ErrorTrackingProviders.Any(p => p.Name == "Bad")).Should().BeFalse();
    }
}
