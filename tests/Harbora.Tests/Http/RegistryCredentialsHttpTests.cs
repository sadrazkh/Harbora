using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Registries;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Private-registry pull credentials end to end (1.3, 2026-09 market-gaps round two) — the real
/// pipeline routes, a real cookie, real Razor. Mirrors <see cref="EmailProvidersHttpTests"/>: a secret
/// is never sent back in readable form except on an explicit reveal, a blank secret on update leaves
/// the stored one untouched, and deleting a credential still used by an app is refused and names it.
/// Unlike email providers there is no attach/detach action — matching is automatic by registry host,
/// proven at the pull seam by <see cref="DeploymentPipelineRegistryCredentialTests"/>; this class
/// proves the CRUD surface and the delete refusal a person actually reads.
///
/// <para>
/// Every test uses a registry host nobody else in this class uses. All tests in
/// <see cref="HarboraHttpFixture"/> share one workspace (<c>fixture.WorkspaceId</c>), and
/// <c>RegistryCredential</c> enforces at most one row per (workspace, host) — so two tests reusing the
/// same host would collide with each other depending on execution order, exactly the same reason
/// <see cref="EmailProvidersHttpTests"/> gives every seeded provider its own name.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class RegistryCredentialsHttpTests(HarboraHttpFixture fixture)
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

    private Guid SeedApp(string slug, string prebuiltImage)
    {
        var projectId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId, ServerId = Guid.CreateVersion7(), EnvironmentId = environmentId,
            Name = slug, Slug = slug, SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = prebuiltImage, Status = AppStatus.Running
        };
        Panel.Seed(db =>
        {
            db.Projects.Add(new Harbora.Domain.Projects.Project
            {
                Id = projectId, WorkspaceId = fixture.WorkspaceId, Name = "Shop", Slug = "rc-" + slug
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

    /// <summary>Encrypts through the real, DI-resolved protector — not a fake "cipher:" prefix — so a
    /// test that actually reveals the secret exercises the genuine round trip.</summary>
    private Guid SeedCredential(string host, string username = "bot", string secret = "s3cr3t")
    {
        var protector = Panel.Resolve<ISecretProtector>();
        var credential = new RegistryCredential
        {
            WorkspaceId = fixture.WorkspaceId, RegistryHost = host,
            Username = username, EncryptedSecret = protector.Protect(secret)
        };
        Panel.Seed(db => db.RegistryCredentials.Add(credential));
        return credential.Id;
    }

    [Fact]
    public async Task Creating_a_credential_encrypts_the_secret_at_rest()
    {
        Panel.GivenUser(fixture.WorkspaceId, "rc-create@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.160", "rc-create@example.com");

        var token = await client.AntiforgeryTokenFrom("/registry-credentials");
        var response = await client.PostFormAsync("/registry-credentials", token,
            ("registryHost", "create-test.ghcr.example"), ("username", "acme-bot"), ("secret", "raw-token-value"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var stored = Panel.Read(db => db.RegistryCredentials.Single(c => c.RegistryHost == "create-test.ghcr.example"));
        stored.WorkspaceId.Should().Be(fixture.WorkspaceId);
        stored.EncryptedSecret.Should().NotBe("raw-token-value", "the secret must be encrypted, not stored as typed");
    }

    [Fact]
    public async Task The_registry_host_is_normalized_to_lower_case_and_trimmed()
    {
        Panel.GivenUser(fixture.WorkspaceId, "rc-normalize@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.161", "rc-normalize@example.com");

        var token = await client.AntiforgeryTokenFrom("/registry-credentials");
        await client.PostFormAsync("/registry-credentials", token,
            ("registryHost", "  Normalize-Test.Example.COM/ "), ("username", "bot"), ("secret", "tok"));

        Panel.Read(db => db.RegistryCredentials.Any(c => c.RegistryHost == "normalize-test.example.com")).Should().BeTrue();
    }

    [Fact]
    public async Task A_second_credential_for_a_host_that_already_has_one_is_refused()
    {
        SeedCredential("dup-test.example.com");
        Panel.GivenUser(fixture.WorkspaceId, "rc-dup@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.162", "rc-dup@example.com");

        var token = await client.AntiforgeryTokenFrom("/registry-credentials");
        var response = await client.PostFormAsync("/registry-credentials", token,
            ("registryHost", "dup-test.example.com"), ("username", "someone-else"), ("secret", "another-token"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var html = await (await client.GetAsync(response.RedirectPath())).Content.ReadAsStringAsync();
        ErrorBannerText(html).Should().Contain("dup-test.example.com");
        Panel.Read(db => db.RegistryCredentials.Count(c => c.WorkspaceId == fixture.WorkspaceId && c.RegistryHost == "dup-test.example.com"))
            .Should().Be(1, "a workspace may have at most one credential per registry host — this is what makes matching deterministic");
    }

    [Fact]
    public async Task The_secret_stays_masked_on_the_list_page_and_reveals_only_on_request()
    {
        var id = SeedCredential("mask-test.example.com", secret: "topsecrettoken");
        Panel.GivenUser(fixture.WorkspaceId, "rc-mask@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.163", "rc-mask@example.com");

        var maskedHtml = await (await client.GetAsync("/registry-credentials")).Content.ReadAsStringAsync();
        maskedHtml.Should().NotContain("topsecrettoken", "the ciphertext must never decrypt onto the page by default");
        maskedHtml.Should().Contain("mask-test.example.com");

        var revealedHtml = await (await client.GetAsync($"/registry-credentials?reveal={id}")).Content.ReadAsStringAsync();
        revealedHtml.Should().Contain("topsecrettoken", "an explicit reveal click must show the real secret");
    }

    [Fact]
    public async Task Updating_with_a_blank_secret_leaves_the_stored_one_unchanged()
    {
        var id = SeedCredential("blank-update.example.com:5000", username: "old-user", secret: "original-secret");
        Panel.GivenUser(fixture.WorkspaceId, "rc-update-blank@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.164", "rc-update-blank@example.com");

        var before = Panel.Read(db => db.RegistryCredentials.Single(c => c.Id == id).EncryptedSecret);

        var token = await client.AntiforgeryTokenFrom("/registry-credentials");
        var response = await client.PostFormAsync($"/registry-credentials/{id}/update", token,
            ("username", "new-user"), ("secret", ""));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var after = Panel.Read(db => db.RegistryCredentials.Single(c => c.Id == id));
        after.Username.Should().Be("new-user");
        after.EncryptedSecret.Should().Be(before, "a blank secret field must leave the stored ciphertext untouched");
    }

    [Fact]
    public async Task Updating_with_a_new_secret_rotates_it_and_nothing_keeps_the_old_ciphertext()
    {
        var id = SeedCredential("rotate-test.example.com:5000", secret: "original-secret");
        Panel.GivenUser(fixture.WorkspaceId, "rc-rotate@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.165", "rc-rotate@example.com");

        var before = Panel.Read(db => db.RegistryCredentials.Single(c => c.Id == id).EncryptedSecret);

        var token = await client.AntiforgeryTokenFrom("/registry-credentials");
        await client.PostFormAsync($"/registry-credentials/{id}/update", token,
            ("username", "bot"), ("secret", "brand-new-secret"));

        // Exactly one row for this credential — rotation overwrites in place rather than leaving a
        // second, stale row a pull could still be matched to.
        Panel.Read(db => db.RegistryCredentials.Count(c => c.Id == id)).Should().Be(1);
        var after = Panel.Read(db => db.RegistryCredentials.Single(c => c.Id == id));
        after.EncryptedSecret.Should().NotBe(before);
        after.EncryptedSecret.Should().NotBe("brand-new-secret", "the new secret must be encrypted too, not stored as typed");
    }

    [Fact]
    public async Task Deleting_a_credential_still_pulling_for_an_app_is_refused_and_names_the_app()
    {
        var id = SeedCredential("delete-refused.example.com");
        SeedApp("checkout", "delete-refused.example.com/acme/checkout:1.0");
        Panel.GivenUser(fixture.WorkspaceId, "rc-delete-refused@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.166", "rc-delete-refused@example.com");

        var token = await client.AntiforgeryTokenFrom("/registry-credentials");
        var response = await client.PostFormAsync($"/registry-credentials/{id}/delete", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var html = await (await client.GetAsync(response.RedirectPath())).Content.ReadAsStringAsync();
        ErrorBannerText(html).Should().Contain("checkout",
            "the refusal must name the app blocking the delete, not merely count it");

        Panel.Read(db => db.RegistryCredentials.Any(c => c.Id == id)).Should().BeTrue(
            "the credential must still exist — the delete was refused, not silently applied anyway");
    }

    [Fact]
    public async Task Deleting_a_credential_for_a_different_registry_than_the_apps_own_is_not_blocked_by_it()
    {
        var id = SeedCredential("delete-unrelated.example.com");
        // An app pulls from a different registry — this credential is not what it uses, so its
        // existence must not block the delete.
        SeedApp("uses-elsewhere", "elsewhere.example.com/acme/uses-elsewhere:1.0");
        Panel.GivenUser(fixture.WorkspaceId, "rc-delete-unrelated@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.167", "rc-delete-unrelated@example.com");

        var token = await client.AntiforgeryTokenFrom("/registry-credentials");
        var response = await client.PostFormAsync($"/registry-credentials/{id}/delete", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.RegistryCredentials.Any(c => c.Id == id)).Should().BeFalse();
    }

    [Fact]
    public async Task An_unused_credential_deletes_cleanly()
    {
        var id = SeedCredential("unused.example.com");
        Panel.GivenUser(fixture.WorkspaceId, "rc-delete-clean@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.168", "rc-delete-clean@example.com");

        var token = await client.AntiforgeryTokenFrom("/registry-credentials");
        var response = await client.PostFormAsync($"/registry-credentials/{id}/delete", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.RegistryCredentials.Any(c => c.Id == id)).Should().BeFalse();
    }

    [Fact]
    public async Task A_viewer_cannot_create_a_credential()
    {
        Panel.GivenUser(fixture.WorkspaceId, "rc-viewer@example.com", SystemRole.Viewer);
        var client = await Panel.SignedInAs("203.0.113.169", "rc-viewer@example.com");

        var token = await client.AntiforgeryTokenFrom("/registry-credentials");
        var response = await client.PostFormAsync("/registry-credentials", token,
            ("registryHost", "viewer-test.example.com"), ("username", "bot"), ("secret", "tok"));

        response.RedirectPath().Should().Be("/account/denied");
        Panel.Read(db => db.RegistryCredentials.Any(c => c.RegistryHost == "viewer-test.example.com")).Should().BeFalse();
    }

    [Fact]
    public async Task No_credentials_cross_workspaces()
    {
        var otherWorkspaceId = Guid.CreateVersion7();
        var theirCredentialId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.Workspaces.Add(new Harbora.Domain.Identity.Workspace { Id = otherWorkspaceId, Name = "Other", Slug = "other-rc-ws" });
            db.RegistryCredentials.Add(new RegistryCredential
            {
                Id = theirCredentialId, WorkspaceId = otherWorkspaceId, RegistryHost = "not-yours.example.com",
                Username = "x", EncryptedSecret = "cipher:other"
            });
        });
        Panel.GivenUser(fixture.WorkspaceId, "rc-tenancy@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.170", "rc-tenancy@example.com");

        var html = await (await client.GetAsync("/registry-credentials")).Content.ReadAsStringAsync();
        html.Should().NotContain("not-yours.example.com");

        var token = await client.AntiforgeryTokenFrom("/registry-credentials");
        var deleteAttempt = await client.PostFormAsync($"/registry-credentials/{theirCredentialId}/delete", token);
        deleteAttempt.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Panel.Read(db => db.RegistryCredentials.Any(c => c.Id == theirCredentialId)).Should().BeTrue(
            "the other workspace's row must be untouched");
    }
}
