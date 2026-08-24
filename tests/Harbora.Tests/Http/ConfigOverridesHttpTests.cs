using System.Net;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Configuration;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// C2 (2026-08-22 config-delivery plan) end to end through real routes, a real cookie, real Razor —
/// the same shape <see cref="ConfigGroupsHttpTests"/> already proved for config groups. Parsing and
/// diagnostics are covered per-format by <c>*ConfigFileEditorTests</c>; deploy-time application is
/// covered at the container-write seam by <c>ConfigOverridePipelineTests</c>. This class proves the
/// rules a person actually creates reach the database correctly, a secret never renders in plaintext,
/// and permissions/tenancy hold — the same three concerns <c>ConfigGroupsHttpTests</c> covers.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class ConfigOverridesHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private Guid SeedApp(string slug, Guid? workspaceId = null)
    {
        var ws = workspaceId ?? fixture.WorkspaceId;
        var projectId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();
        var app = new App
        {
            WorkspaceId = ws, ServerId = Guid.CreateVersion7(), EnvironmentId = environmentId,
            Name = slug, Slug = slug, SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0", Status = AppStatus.Running
        };
        Panel.Seed(db =>
        {
            db.Projects.Add(new Harbora.Domain.Projects.Project
            {
                Id = projectId, WorkspaceId = ws, Name = "Shop", Slug = "co-" + slug
            });
            db.Environments.Add(new Harbora.Domain.Projects.Environment
            {
                Id = environmentId, WorkspaceId = ws, ProjectId = projectId,
                Name = "Production", Slug = "production", IsDefault = true
            });
            db.Apps.Add(app);
        });
        return app.Id;
    }

    [Fact]
    public async Task Adding_a_literal_rule_persists_it_and_shows_it_on_the_page()
    {
        var appId = SeedApp("literal-app");
        Panel.GivenUser(fixture.WorkspaceId, "co-literal@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.240", "co-literal@example.com");

        var token = await client.AntiforgeryTokenFrom($"/apps/{appId}/config-overrides");
        var created = await client.PostFormAsync($"/apps/{appId}/config-overrides", token,
            ("filePath", "appsettings.json"), ("keyPath", "ConnectionStrings:Default"),
            ("valueKind", "literal"), ("literalValue", "Host=db;Database=app"));
        created.StatusCode.Should().Be(HttpStatusCode.Found);

        var stored = Panel.Read(db => db.ConfigOverrideRules.Single(r => r.AppId == appId));
        stored.FilePath.Should().Be("appsettings.json");
        stored.KeyPath.Should().Be("ConnectionStrings:Default");
        stored.ValueKind.Should().Be(ConfigOverrideValueKind.Literal);
        stored.LiteralValue.Should().Be("Host=db;Database=app");
        stored.HasUnpublishedChanges.Should().BeTrue("nothing has deployed with it yet");

        var html = await (await client.GetAsync($"/apps/{appId}/config-overrides")).Content.ReadAsStringAsync();
        html.Should().Contain("appsettings.json");
        html.Should().Contain("ConnectionStrings:Default");
        html.Should().Contain("Host=db;Database=app");
    }

    [Fact]
    public async Task A_secret_rules_value_is_encrypted_at_rest_and_never_rendered_in_plaintext()
    {
        var appId = SeedApp("secret-app");
        Panel.GivenUser(fixture.WorkspaceId, "co-secret@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.241", "co-secret@example.com");

        var token = await client.AntiforgeryTokenFrom($"/apps/{appId}/config-overrides");
        await client.PostFormAsync($"/apps/{appId}/config-overrides", token,
            ("filePath", "appsettings.json"), ("keyPath", "ConnectionStrings:Default"),
            ("valueKind", "secret"), ("secretValue", "super-secret-password"));

        var stored = Panel.Read(db => db.ConfigOverrideRules.Single(r => r.AppId == appId));
        stored.ValueKind.Should().Be(ConfigOverrideValueKind.Secret);
        stored.EncryptedSecretValue.Should().NotBe("super-secret-password", "it must be stored encrypted, not plaintext");

        var html = await (await client.GetAsync($"/apps/{appId}/config-overrides")).Content.ReadAsStringAsync();
        html.Should().NotContain("super-secret-password");
    }

    [Fact]
    public async Task An_unrecognised_extension_with_no_explicit_format_is_refused_with_a_clear_reason()
    {
        var appId = SeedApp("unknown-ext-app");
        Panel.GivenUser(fixture.WorkspaceId, "co-unknown@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.242", "co-unknown@example.com");

        var token = await client.AntiforgeryTokenFrom($"/apps/{appId}/config-overrides");
        var response = await client.PostFormAsync($"/apps/{appId}/config-overrides", token,
            ("filePath", "config/settings"), ("keyPath", "x"), ("valueKind", "literal"), ("literalValue", "y"));
        response.StatusCode.Should().Be(HttpStatusCode.Found);

        Panel.Read(db => db.ConfigOverrideRules.Any(r => r.AppId == appId)).Should().BeFalse(
            "a rule whose format cannot be determined must not be silently saved");
    }

    [Fact]
    public async Task Deleting_a_rule_removes_it()
    {
        var appId = SeedApp("delete-app");
        var rule = new ConfigOverrideRule
        {
            AppId = appId, FilePath = "appsettings.json", KeyPath = "A:B",
            ValueKind = ConfigOverrideValueKind.Literal, LiteralValue = "x"
        };
        Panel.Seed(db => db.ConfigOverrideRules.Add(rule));
        Panel.GivenUser(fixture.WorkspaceId, "co-delete@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.243", "co-delete@example.com");

        var token = await client.AntiforgeryTokenFrom($"/apps/{appId}/config-overrides");
        var response = await client.PostFormAsync($"/apps/{appId}/config-overrides/{rule.Id}/delete", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.ConfigOverrideRules.Any(r => r.Id == rule.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task A_viewer_cannot_add_or_delete_a_rule()
    {
        var appId = SeedApp("viewer-co-app");
        var rule = new ConfigOverrideRule
        {
            AppId = appId, FilePath = "appsettings.json", KeyPath = "A:B",
            ValueKind = ConfigOverrideValueKind.Literal, LiteralValue = "x"
        };
        Panel.Seed(db => db.ConfigOverrideRules.Add(rule));
        Panel.GivenUser(fixture.WorkspaceId, "co-viewer@example.com", SystemRole.Viewer);
        var client = await Panel.SignedInAs("203.0.113.244", "co-viewer@example.com");

        // A viewer's own GET on this page is refused (Capabilities.AppsEnv), so the token comes
        // from the app's main page instead — the same workaround ConfigGroupsHttpTests uses for
        // exactly this reason.
        var token = await client.AntiforgeryTokenFrom($"/apps/details/{appId}");
        var createResponse = await client.PostFormAsync($"/apps/{appId}/config-overrides", token,
            ("filePath", "appsettings.json"), ("keyPath", "A:C"), ("valueKind", "literal"), ("literalValue", "z"));
        createResponse.RedirectPath().Should().Be("/account/denied");

        var deleteResponse = await client.PostFormAsync($"/apps/{appId}/config-overrides/{rule.Id}/delete", token);
        deleteResponse.RedirectPath().Should().Be("/account/denied");

        Panel.Read(db => db.ConfigOverrideRules.Count(r => r.AppId == appId)).Should().Be(1);
    }

    [Fact]
    public async Task No_rules_or_pages_cross_workspaces()
    {
        var otherWorkspaceId = Guid.CreateVersion7();
        Panel.Seed(db => db.Workspaces.Add(new Harbora.Domain.Identity.Workspace
        {
            Id = otherWorkspaceId, Name = "Other", Slug = "other-co-ws"
        }));
        var theirAppId = SeedApp("their-app", otherWorkspaceId);
        var theirRule = new ConfigOverrideRule
        {
            AppId = theirAppId, FilePath = "appsettings.json", KeyPath = "Secret:Path",
            ValueKind = ConfigOverrideValueKind.Literal, LiteralValue = "not-yours"
        };
        Panel.Seed(db => db.ConfigOverrideRules.Add(theirRule));
        Panel.GivenUser(fixture.WorkspaceId, "co-tenancy@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.245", "co-tenancy@example.com");

        var pageResponse = await client.GetAsync($"/apps/{theirAppId}/config-overrides");
        pageResponse.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "another workspace's app id must not resolve, even for a signed-in owner of a different workspace");

        Panel.Read(db => db.ConfigOverrideRules.Any(r => r.Id == theirRule.Id)).Should().BeTrue(
            "the other workspace's rule must be untouched");
    }
}
