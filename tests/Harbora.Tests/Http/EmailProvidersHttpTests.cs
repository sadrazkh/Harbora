using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Email;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// BYO SMTP providers attaching to apps end to end (F6, 2026-08-21 functions-and-services plan,
/// HARBORA-0038 phase 1) — the real pipeline routes, a real cookie, real Razor. Mirrors
/// <see cref="StorageBucketsHttpTests"/> (F5) exactly: provenance is never hidden on the app's own
/// env page, a secret entry stays masked, and deleting an attached provider refuses with the named
/// list. Precedence and the actual container environment are proven at the assembly seam by
/// <see cref="EmailProviderMergeTests"/>/<see cref="EmailProviderPipelineTests"/>; this class proves
/// the same facts reach the pages a person actually reads. The honest-refusal test-send behaviour has
/// its own class, <see cref="EmailProvidersTestSendHttpTests"/>.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class EmailProvidersHttpTests(HarboraHttpFixture fixture)
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
                Id = projectId, WorkspaceId = fixture.WorkspaceId, Name = "Shop", Slug = "ep-" + slug
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
        var provider = new EmailProvider
        {
            WorkspaceId = fixture.WorkspaceId, Name = name, Host = "smtp." + name + ".example", Port = 587,
            Username = "apikey", EncryptedPassword = "cipher:" + name,
            FromAddress = "noreply@acme.example", UseSsl = true
        };
        Panel.Seed(db => db.EmailProviders.Add(provider));
        return provider.Id;
    }

    [Fact]
    public async Task Attaching_a_provider_makes_its_variables_show_on_the_apps_env_page_with_provenance()
    {
        var appId = SeedApp("api");
        var providerId = SeedProvider("sendgrid");
        Panel.GivenUser(fixture.WorkspaceId, "ep-attach@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.150", "ep-attach@example.com");

        var token = await client.AntiforgeryTokenFrom("/email-providers");
        var attach = await client.PostFormAsync($"/email-providers/{providerId}/attach", token,
            ("appId", appId.ToString()));
        attach.StatusCode.Should().Be(HttpStatusCode.Found);

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();
        html.Should().Contain("SMTP_HOST");
        html.Should().Contain("SMTP_USER");
        html.Should().Contain("SMTP_FROM");
        html.Should().Contain("data-env-source=\"email-provider\"", "an email-provider row must say it came from an email provider, not the app or a group");
        html.Should().Contain("sendgrid", "the row must name the specific provider it came from");
        html.Should().Contain("data-attached-email-provider=\"sendgrid\"");
    }

    [Fact]
    public async Task The_providers_password_stays_masked_on_the_apps_env_page()
    {
        var appId = SeedApp("secretive");
        var providerId = SeedProvider("with-secret");
        Panel.Seed(db => db.AppEmailProviders.Add(new AppEmailProvider
        {
            AppId = appId, EmailProviderId = providerId, AttachOrder = 1
        }));
        Panel.GivenUser(fixture.WorkspaceId, "ep-mask@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.151", "ep-mask@example.com");

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();

        html.Should().Contain("SMTP_PASSWORD");
        html.Should().NotContain("cipher:with-secret", "a provider's ciphertext must never reach the page either");
        html.Should().Contain("&#x2022;&#x2022;&#x2022;&#x2022;&#x2022;&#x2022;",
            "SMTP_PASSWORD masks with the same bullet every other secret env var uses");
    }

    [Fact]
    public async Task Detaching_a_provider_removes_its_variables_from_the_apps_effective_env_page()
    {
        var appId = SeedApp("detach-me");
        var providerId = SeedProvider("goes-away");
        Panel.Seed(db => db.AppEmailProviders.Add(new AppEmailProvider
        {
            AppId = appId, EmailProviderId = providerId, AttachOrder = 1
        }));
        Panel.GivenUser(fixture.WorkspaceId, "ep-detach@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.152", "ep-detach@example.com");

        (await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync())
            .Should().Contain("SMTP_HOST");

        var token = await client.AntiforgeryTokenFrom($"/apps/details/{appId}");
        var detach = await client.PostFormAsync($"/email-providers/{providerId}/detach", token,
            ("appId", appId.ToString()), ("returnUrl", $"/apps/details/{appId}"));
        detach.StatusCode.Should().Be(HttpStatusCode.Found);

        Panel.Read(db => db.AppEmailProviders.Any(x => x.AppId == appId && x.EmailProviderId == providerId)).Should().BeFalse();
        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();
        html.Should().NotContain("SMTP_HOST");
    }

    [Fact]
    public async Task Deleting_a_provider_still_attached_to_an_app_is_refused_and_names_the_app()
    {
        var appId = SeedApp("checkout");
        var providerId = SeedProvider("attached-provider");
        Panel.Seed(db => db.AppEmailProviders.Add(new AppEmailProvider
        {
            AppId = appId, EmailProviderId = providerId, AttachOrder = 1
        }));
        Panel.GivenUser(fixture.WorkspaceId, "ep-delete-refused@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.153", "ep-delete-refused@example.com");

        var token = await client.AntiforgeryTokenFrom("/email-providers");
        var response = await client.PostFormAsync($"/email-providers/{providerId}/delete", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/email-providers");

        var html = await (await client.GetAsync(response.RedirectPath())).Content.ReadAsStringAsync();
        ErrorBannerText(html).Should().Contain("checkout",
            "the refusal must name the app blocking the delete, not merely count it");

        Panel.Read(db => db.EmailProviders.Any(p => p.Id == providerId)).Should().BeTrue(
            "the provider must still exist — the delete was refused, not silently applied anyway");
    }

    [Fact]
    public async Task An_unattached_provider_deletes_cleanly()
    {
        var providerId = SeedProvider("unattached-provider");
        Panel.GivenUser(fixture.WorkspaceId, "ep-delete-clean@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.154", "ep-delete-clean@example.com");

        var token = await client.AntiforgeryTokenFrom("/email-providers");
        var response = await client.PostFormAsync($"/email-providers/{providerId}/delete", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.EmailProviders.Any(p => p.Id == providerId)).Should().BeFalse();
    }

    [Fact]
    public async Task A_viewer_cannot_attach_a_provider_to_an_app()
    {
        var appId = SeedApp("viewer-app");
        var providerId = SeedProvider("viewer-provider");
        Panel.GivenUser(fixture.WorkspaceId, "ep-viewer@example.com", SystemRole.Viewer);
        var client = await Panel.SignedInAs("203.0.113.155", "ep-viewer@example.com");

        var token = await client.AntiforgeryTokenFrom("/email-providers");
        var attachResponse = await client.PostFormAsync($"/email-providers/{providerId}/attach", token,
            ("appId", appId.ToString()));
        attachResponse.RedirectPath().Should().Be("/account/denied");
        Panel.Read(db => db.AppEmailProviders.Any(x => x.AppId == appId)).Should().BeFalse();
    }

    [Fact]
    public async Task No_providers_or_attachments_cross_workspaces()
    {
        var otherWorkspaceId = Guid.CreateVersion7();
        var theirProviderId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.Workspaces.Add(new Harbora.Domain.Identity.Workspace { Id = otherWorkspaceId, Name = "Other", Slug = "other-ep-ws" });
            db.EmailProviders.Add(new EmailProvider
            {
                Id = theirProviderId, WorkspaceId = otherWorkspaceId, Name = "not-yours", Host = "smtp.other.example",
                Port = 587, Username = "x", EncryptedPassword = "cipher:other", FromAddress = "x@other.example"
            });
        });
        var appId = SeedApp("tenancy-app");
        Panel.GivenUser(fixture.WorkspaceId, "ep-tenancy@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.156", "ep-tenancy@example.com");

        var html = await (await client.GetAsync("/email-providers")).Content.ReadAsStringAsync();
        html.Should().NotContain("not-yours");

        var token = await client.AntiforgeryTokenFrom("/email-providers");
        var attachAttempt = await client.PostFormAsync($"/email-providers/{theirProviderId}/attach", token,
            ("appId", appId.ToString()));
        attachAttempt.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "another workspace's provider id must not resolve, even for a signed-in owner of a different workspace");

        var deleteAttempt = await client.PostFormAsync($"/email-providers/{theirProviderId}/delete", token);
        deleteAttempt.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Panel.Read(db => db.EmailProviders.Any(p => p.Id == theirProviderId)).Should().BeTrue("the other workspace's row must be untouched");
    }

    [Fact]
    public async Task Creating_a_provider_encrypts_the_password_at_rest()
    {
        Panel.GivenUser(fixture.WorkspaceId, "ep-create@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.157", "ep-create@example.com");

        var token = await client.AntiforgeryTokenFrom("/email-providers");
        var response = await client.PostFormAsync("/email-providers", token,
            ("name", "Postmark"), ("host", "smtp.postmarkapp.com"), ("port", "587"),
            ("username", "pm-token"), ("password", "raw-password-value"),
            ("fromAddress", "noreply@acme.example"), ("useSsl", "true"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var stored = Panel.Read(db => db.EmailProviders.Single(p => p.Name == "Postmark"));
        stored.WorkspaceId.Should().Be(fixture.WorkspaceId);
        stored.EncryptedPassword.Should().NotBe("raw-password-value", "the password must be encrypted, not stored as typed");
    }
}
