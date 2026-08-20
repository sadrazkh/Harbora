using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Shared environment-variable groups end to end (Sub-project 9, 2026-08-20 platform-options plan) —
/// the real pipeline routes, a real cookie, real Razor. Covers the three things the plan singles out:
/// provenance is never hidden on the app's own env page, secrets stay masked, and deleting an
/// attached group refuses with the named list (the <c>ProjectsController.Delete</c> idiom) rather
/// than a raw constraint failure. Precedence itself is proven at the assembly seam by
/// <c>ConfigGroupMergeTests</c>/<c>ConfigGroupPipelineTests</c>; this class proves the same facts
/// reach the pages a person actually reads.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class ConfigGroupsHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private static readonly Regex ErrorBanner = new(
        """<div class="alert-warning[^>]*>(?<text>.*?)</div>""", RegexOptions.Singleline | RegexOptions.Compiled);

    private static string ErrorBannerText(string html)
    {
        var match = ErrorBanner.Match(html);
        match.Success.Should().BeTrue("a refused delete must render the TempData[\"Error\"] banner");
        return match.Groups["text"].Value;
    }

    /// <summary>EnvironmentId is required (P2, 2026-08-17 app-environment-management design); a
    /// project and environment of the app's own keeps this app inside a scope the signed-in owner
    /// actually has capability grants over — the same shape <c>AppReplicasHttpTests</c> seeds.</summary>
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
                Id = projectId, WorkspaceId = fixture.WorkspaceId, Name = "Shop", Slug = "cg-" + slug
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

    private Guid SeedGroup(string name, params (string Key, string Value, bool Secret)[] entries)
    {
        var group = new ConfigGroup { WorkspaceId = fixture.WorkspaceId, Name = name };
        Panel.Seed(db =>
        {
            db.ConfigGroups.Add(group);
            foreach (var (key, value, secret) in entries)
                db.ConfigGroupEntries.Add(new ConfigGroupEntry
                {
                    ConfigGroupId = group.Id, Key = key,
                    Value = secret ? "cipher:" + value : value, IsSecret = secret
                });
        });
        return group.Id;
    }

    [Fact]
    public async Task The_groups_page_lists_a_group_its_entry_count_and_masks_a_secret_value()
    {
        var groupId = SeedGroup("shared-defaults", ("API_BASE", "https://api.example.com", false), ("DB_PASSWORD", "s3cret", true));
        Panel.GivenUser(fixture.WorkspaceId, "cg-list@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.230", "cg-list@example.com");

        var html = await (await client.GetAsync("/config-groups")).Content.ReadAsStringAsync();

        html.Should().Contain($"data-config-group-id=\"{groupId}\"");
        html.Should().Contain("data-config-group-name=\"shared-defaults\"");
        html.Should().Contain("https://api.example.com", "a non-secret value renders in plain text");
        html.Should().NotContain("s3cret", "a secret entry's plaintext must never reach the page");
        html.Should().Contain("&#x2022;&#x2022;&#x2022;&#x2022;&#x2022;&#x2022;",
            "a secret entry masks with the same bullet EnvironmentVariable uses (Razor HTML-encodes it)");
    }

    [Fact]
    public async Task Creating_a_group_then_adding_an_entry_persists_both()
    {
        Panel.GivenUser(fixture.WorkspaceId, "cg-create@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.231", "cg-create@example.com");

        var token = await client.AntiforgeryTokenFrom("/config-groups");
        var created = await client.PostFormAsync("/config-groups", token, ("name", "flags"));
        created.StatusCode.Should().Be(HttpStatusCode.Found);

        var group = Panel.Read(db => db.ConfigGroups.Single(g => g.Name == "flags"));

        var entryToken = await client.AntiforgeryTokenFrom("/config-groups");
        await client.PostFormAsync($"/config-groups/{group.Id}/entries", entryToken,
            ("key", "FEATURE_X"), ("value", "on"), ("isSecret", "false"));

        var stored = Panel.Read(db => db.ConfigGroupEntries.Single(e => e.ConfigGroupId == group.Id));
        stored.Key.Should().Be("FEATURE_X");
        stored.Value.Should().Be("on");
        stored.IsSecret.Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_a_group_still_attached_to_an_app_is_refused_and_names_the_app()
    {
        var appId = SeedApp("checkout");
        var groupId = SeedGroup("attached-group", ("KEY", "value", false));
        Panel.Seed(db => db.AppConfigGroups.Add(new AppConfigGroup
        {
            AppId = appId, ConfigGroupId = groupId, AttachOrder = 1, HasUnpublishedChanges = true
        }));
        Panel.GivenUser(fixture.WorkspaceId, "cg-delete-refused@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.232", "cg-delete-refused@example.com");

        var token = await client.AntiforgeryTokenFrom("/config-groups");
        var response = await client.PostFormAsync($"/config-groups/{groupId}/delete", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/config-groups");

        var html = await (await client.GetAsync(response.RedirectPath())).Content.ReadAsStringAsync();
        ErrorBannerText(html).Should().Contain("checkout",
            "the refusal must name the app blocking the delete, not merely count it");

        Panel.Read(db => db.ConfigGroups.Any(g => g.Id == groupId)).Should().BeTrue(
            "the group must still exist — the delete was refused, not silently applied anyway");
    }

    [Fact]
    public async Task A_group_with_no_attached_apps_deletes_cleanly()
    {
        var groupId = SeedGroup("unattached-group", ("KEY", "value", false));
        Panel.GivenUser(fixture.WorkspaceId, "cg-delete-clean@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.233", "cg-delete-clean@example.com");

        var token = await client.AntiforgeryTokenFrom("/config-groups");
        var response = await client.PostFormAsync($"/config-groups/{groupId}/delete", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.ConfigGroups.Any(g => g.Id == groupId)).Should().BeFalse();
    }

    [Fact]
    public async Task Attaching_a_group_makes_its_key_show_on_the_apps_env_page_with_provenance()
    {
        var appId = SeedApp("api");
        var groupId = SeedGroup("shared", ("API_BASE", "https://api.example.com", false));
        Panel.GivenUser(fixture.WorkspaceId, "cg-attach@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.234", "cg-attach@example.com");

        var token = await client.AntiforgeryTokenFrom($"/apps/details/{appId}");
        var attach = await client.PostFormAsync($"/apps/{appId}/config-groups", token, ("configGroupId", groupId.ToString()));
        attach.StatusCode.Should().Be(HttpStatusCode.Found);

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();
        html.Should().Contain("API_BASE");
        html.Should().Contain("https://api.example.com");
        html.Should().Contain("data-env-source=\"group\"", "a group-provided row must say it came from a group, not the app itself");
        html.Should().Contain("shared", "the row must name the specific group it came from");
        html.Should().Contain("data-attached-config-group=\"shared\"");
    }

    [Fact]
    public async Task The_apps_own_variable_visibly_wins_over_a_group_defining_the_same_key()
    {
        var appId = SeedApp("api-own-wins");
        var groupId = SeedGroup("shared", ("PORT", "8080", false));
        Panel.Seed(db => db.EnvironmentVariables.Add(new EnvironmentVariable { AppId = appId, Key = "PORT", Value = "9000" }));
        Panel.Seed(db => db.AppConfigGroups.Add(new AppConfigGroup { AppId = appId, ConfigGroupId = groupId, AttachOrder = 1 }));
        Panel.GivenUser(fixture.WorkspaceId, "cg-own-wins@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.235", "cg-own-wins@example.com");

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-env-key=\"PORT\" data-env-source=\"app\"",
            "the effective row for PORT must be sourced from the app, not the group, even though the group also defines it");
        html.Should().NotContain("8080", "the group's shadowed value for PORT must not render anywhere as PORT's value");
    }

    [Fact]
    public async Task Editing_a_groups_entry_marks_the_attached_app_stale()
    {
        var appId = SeedApp("worker");
        var groupId = SeedGroup("shared", ("KEY", "v1", false));
        var join = new AppConfigGroup { AppId = appId, ConfigGroupId = groupId, AttachOrder = 1, HasUnpublishedChanges = false };
        Panel.Seed(db => db.AppConfigGroups.Add(join));
        Panel.GivenUser(fixture.WorkspaceId, "cg-stale@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.236", "cg-stale@example.com");

        Panel.Read(db => db.AppConfigGroups.Single(x => x.Id == join.Id).HasUnpublishedChanges).Should().BeFalse();

        var token = await client.AntiforgeryTokenFrom("/config-groups");
        await client.PostFormAsync($"/config-groups/{groupId}/entries", token, ("key", "KEY"), ("value", "v2"), ("isSecret", "false"));

        Panel.Read(db => db.AppConfigGroups.Single(x => x.Id == join.Id).HasUnpublishedChanges).Should().BeTrue(
            "editing a group must flip attached apps to the applies-on-next-deploy state — it must never restart or redeploy them itself");

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();
        html.Should().Contain("data-config-group-state=\"unpublished\"");
    }

    [Fact]
    public async Task Detaching_a_group_removes_its_keys_from_the_apps_effective_env_page()
    {
        var appId = SeedApp("detach-me");
        var groupId = SeedGroup("shared", ("ONLY_FROM_GROUP", "value", false));
        Panel.Seed(db => db.AppConfigGroups.Add(new AppConfigGroup { AppId = appId, ConfigGroupId = groupId, AttachOrder = 1 }));
        Panel.GivenUser(fixture.WorkspaceId, "cg-detach@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.237", "cg-detach@example.com");

        (await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync())
            .Should().Contain("ONLY_FROM_GROUP");

        var token = await client.AntiforgeryTokenFrom($"/apps/details/{appId}");
        var detach = await client.PostFormAsync($"/apps/{appId}/config-groups/{groupId}/detach", token);
        detach.StatusCode.Should().Be(HttpStatusCode.Found);

        Panel.Read(db => db.AppConfigGroups.Any(x => x.AppId == appId && x.ConfigGroupId == groupId)).Should().BeFalse();
        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();
        html.Should().NotContain("ONLY_FROM_GROUP");
    }

    [Fact]
    public async Task A_viewer_cannot_create_a_group_or_attach_one_to_an_app()
    {
        var appId = SeedApp("viewer-app");
        var groupId = SeedGroup("shared", ("KEY", "value", false));
        Panel.GivenUser(fixture.WorkspaceId, "cg-viewer@example.com", SystemRole.Viewer);
        var client = await Panel.SignedInAs("203.0.113.238", "cg-viewer@example.com");

        var groupsToken = await client.AntiforgeryTokenFrom("/config-groups");
        var createResponse = await client.PostFormAsync("/config-groups", groupsToken, ("name", "denied-group"));
        createResponse.RedirectPath().Should().Be("/account/denied");
        Panel.Read(db => db.ConfigGroups.Any(g => g.Name == "denied-group")).Should().BeFalse();

        var appToken = await client.AntiforgeryTokenFrom($"/apps/details/{appId}");
        var attachResponse = await client.PostFormAsync($"/apps/{appId}/config-groups", appToken, ("configGroupId", groupId.ToString()));
        attachResponse.RedirectPath().Should().Be("/account/denied");
        Panel.Read(db => db.AppConfigGroups.Any(x => x.AppId == appId)).Should().BeFalse();
    }

    [Fact]
    public async Task No_groups_or_entries_cross_workspaces()
    {
        var otherWorkspaceId = Guid.CreateVersion7();
        var theirGroupId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.Workspaces.Add(new Harbora.Domain.Identity.Workspace { Id = otherWorkspaceId, Name = "Other", Slug = "other-cg-ws" });
            db.ConfigGroups.Add(new ConfigGroup { Id = theirGroupId, WorkspaceId = otherWorkspaceId, Name = "not-yours" });
            db.ConfigGroupEntries.Add(new ConfigGroupEntry { ConfigGroupId = theirGroupId, Key = "SECRET_TO_THEM", Value = "x" });
        });
        Panel.GivenUser(fixture.WorkspaceId, "cg-tenancy@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.239", "cg-tenancy@example.com");

        var html = await (await client.GetAsync("/config-groups")).Content.ReadAsStringAsync();
        html.Should().NotContain("not-yours");
        html.Should().NotContain("SECRET_TO_THEM");

        var token = await client.AntiforgeryTokenFrom("/config-groups");
        var deleteAttempt = await client.PostFormAsync($"/config-groups/{theirGroupId}/delete", token);
        deleteAttempt.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "another workspace's group id must not resolve, even for a signed-in owner of a different workspace");

        Panel.Read(db => db.ConfigGroups.Any(g => g.Id == theirGroupId)).Should().BeTrue("the other workspace's row must be untouched");
    }
}
