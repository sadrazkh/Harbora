using System.Net;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Status;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <c>/status-page</c> end to end (P7, 2026-08-20 platform-options plan) — the authenticated
/// workspace-settings half. Mirrors <c>EventSubscriptionsHttpTests</c>' idiom: read follows the base
/// authenticated policy, mutation requires <c>Capabilities.AlertsManage</c>.
///
/// <para>Every test seeds its own workspace, for the same reason <c>StatusPageHttpTests</c> does:
/// <c>StatusPage.WorkspaceId</c> is unique, and this collection's panel is shared across every test.</para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class StatusPageSettingsHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private (Guid WorkspaceId, Guid EnvironmentId) GivenWorkspace(string slug)
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
        });
        return (workspaceId, environmentId);
    }

    private Guid GivenApp(Guid workspaceId, Guid environmentId, string slug)
    {
        var appId = Guid.CreateVersion7();
        Panel.Seed(db => db.Apps.Add(new App
        {
            Id = appId, WorkspaceId = workspaceId, EnvironmentId = environmentId,
            Name = slug, Slug = slug, Kind = ServiceKind.Web,
            SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/" + slug + ":1.0",
            Status = AppStatus.Running
        }));
        return appId;
    }

    [Fact]
    public async Task Opening_the_settings_page_creates_a_disabled_row_lazily()
    {
        var (workspaceId, _) = GivenWorkspace("settings-lazy-create");
        Panel.GivenUser(workspaceId, "status-lazy@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.20", "status-lazy@example.com");

        var response = await client.GetAsync("/status-page");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = Panel.Read(db => db.StatusPages.Single(p => p.WorkspaceId == workspaceId));
        page.IsEnabled.Should().BeFalse("opening the settings screen must never itself publish anything");
    }

    [Fact]
    public async Task Enabling_and_disabling_flip_the_row_through_the_real_routes()
    {
        var (workspaceId, _) = GivenWorkspace("settings-toggle");
        Panel.GivenUser(workspaceId, "status-toggle@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.21", "status-toggle@example.com");
        await client.GetAsync("/status-page"); // lazily creates the row

        var enableToken = await client.AntiforgeryTokenFrom("/status-page");
        var enabled = await client.PostFormAsync("/status-page/enable", enableToken);
        enabled.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.StatusPages.Single(p => p.WorkspaceId == workspaceId).IsEnabled).Should().BeTrue();

        var disableToken = await client.AntiforgeryTokenFrom("/status-page");
        var disabled = await client.PostFormAsync("/status-page/disable", disableToken);
        disabled.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.StatusPages.Single(p => p.WorkspaceId == workspaceId).IsEnabled).Should().BeFalse();
    }

    [Fact]
    public async Task Adding_a_component_stores_the_typed_display_name_not_the_apps_own_slug()
    {
        var (workspaceId, environmentId) = GivenWorkspace("settings-add-component");
        var appId = GivenApp(workspaceId, environmentId, "internal-api-slug");
        Panel.GivenUser(workspaceId, "status-add@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.22", "status-add@example.com");
        await client.GetAsync("/status-page");

        var token = await client.AntiforgeryTokenFrom("/status-page");
        var response = await client.PostFormAsync("/status-page/components", token,
            ("appId", appId.ToString()), ("displayName", "Customer-Facing API"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var component = Panel.Read(db => db.StatusPageComponents.Single(c => c.AppId == appId));
        component.DisplayName.Should().Be("Customer-Facing API");
        component.WorkspaceId.Should().Be(workspaceId);
    }

    [Fact]
    public async Task Adding_a_component_with_no_display_name_is_refused()
    {
        var (workspaceId, environmentId) = GivenWorkspace("settings-empty-name");
        var appId = GivenApp(workspaceId, environmentId, "some-app");
        Panel.GivenUser(workspaceId, "status-empty-name@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.23", "status-empty-name@example.com");
        await client.GetAsync("/status-page");

        var token = await client.AntiforgeryTokenFrom("/status-page");
        await client.PostFormAsync("/status-page/components", token, ("appId", appId.ToString()), ("displayName", ""));

        Panel.Read(db => db.StatusPageComponents.Any(c => c.AppId == appId)).Should().BeFalse();
    }

    [Fact]
    public async Task Removing_a_component_deletes_the_row()
    {
        var (workspaceId, environmentId) = GivenWorkspace("settings-remove-component");
        var appId = GivenApp(workspaceId, environmentId, "removable-app");
        var pageId = Guid.CreateVersion7();
        var componentId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.StatusPages.Add(new StatusPage { Id = pageId, WorkspaceId = workspaceId, IsEnabled = true });
            db.StatusPageComponents.Add(new StatusPageComponent
            {
                Id = componentId, WorkspaceId = workspaceId, StatusPageId = pageId, AppId = appId,
                DisplayName = "Removable", SortOrder = 0
            });
        });
        Panel.GivenUser(workspaceId, "status-remove@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.24", "status-remove@example.com");

        var token = await client.AntiforgeryTokenFrom("/status-page");
        var response = await client.PostFormAsync($"/status-page/components/{componentId}/remove", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.StatusPageComponents.Any(c => c.Id == componentId)).Should().BeFalse();
    }

    [Fact]
    public async Task Posting_an_incident_requires_both_language_titles()
    {
        var (workspaceId, _) = GivenWorkspace("settings-incident-both-langs");
        Panel.GivenUser(workspaceId, "status-incident-lang@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.25", "status-incident-lang@example.com");
        await client.GetAsync("/status-page");

        var token = await client.AntiforgeryTokenFrom("/status-page");
        await client.PostFormAsync("/status-page/incidents", token, ("titleEn", "Only English"), ("titleFa", ""));

        Panel.Read(db => db.StatusIncidents.Any(i => i.TitleEn == "Only English")).Should().BeFalse(
            "the plan requires both languages the same way Announcement (sub-project 4) does");
    }

    [Fact]
    public async Task Posting_and_resolving_an_incident_works_through_the_real_routes()
    {
        var (workspaceId, _) = GivenWorkspace("settings-incident-lifecycle");
        Panel.GivenUser(workspaceId, "status-incident-life@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.26", "status-incident-life@example.com");
        await client.GetAsync("/status-page");

        var postToken = await client.AntiforgeryTokenFrom("/status-page");
        await client.PostFormAsync("/status-page/incidents", postToken,
            ("titleEn", "Elevated errors"), ("titleFa", "افزایش خطاها"));

        var incident = Panel.Read(db => db.StatusIncidents.Single(i => i.TitleEn == "Elevated errors"));
        incident.ResolvedAt.Should().BeNull();

        var resolveToken = await client.AntiforgeryTokenFrom("/status-page");
        var resolved = await client.PostFormAsync($"/status-page/incidents/{incident.Id}/resolve", resolveToken);

        resolved.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.StatusIncidents.Single(i => i.Id == incident.Id).ResolvedAt).Should().NotBeNull();
    }

    [Fact]
    public async Task A_member_without_alerts_manage_can_view_but_not_mutate()
    {
        var (workspaceId, environmentId) = GivenWorkspace("settings-member-denied");
        var appId = GivenApp(workspaceId, environmentId, "member-app");
        Panel.GivenUser(workspaceId, "status-member@example.com", SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.27", "status-member@example.com");

        var page = await client.GetAsync("/status-page");
        page.StatusCode.Should().Be(HttpStatusCode.OK);

        var token = await client.AntiforgeryTokenFrom("/status-page");
        var response = await client.PostFormAsync("/status-page/enable", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found, "alerts.manage is not a Member's");
        response.RedirectPath().Should().Be("/account/denied");
        Panel.Read(db => db.StatusPages.Any(p => p.WorkspaceId == workspaceId && p.IsEnabled)).Should().BeFalse();
    }

    [Fact]
    public async Task Adding_a_component_for_an_app_in_another_workspace_404s()
    {
        var (workspaceId, _) = GivenWorkspace("settings-tenancy-owner");
        var (otherWorkspaceId, otherEnvironmentId) = GivenWorkspace("settings-tenancy-other");
        var theirAppId = GivenApp(otherWorkspaceId, otherEnvironmentId, "not-yours-app");

        Panel.GivenUser(workspaceId, "status-tenancy@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.28", "status-tenancy@example.com");
        await client.GetAsync("/status-page");

        var token = await client.AntiforgeryTokenFrom("/status-page");
        var response = await client.PostFormAsync("/status-page/components", token,
            ("appId", theirAppId.ToString()), ("displayName", "Sneaky"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "an app id from another workspace must not resolve, even as a raw form post");
        Panel.Read(db => db.StatusPageComponents.Any(c => c.AppId == theirAppId)).Should().BeFalse();
    }

    [Fact]
    public async Task Removing_another_workspaces_component_by_id_404s_and_leaves_it_untouched()
    {
        var (workspaceId, _) = GivenWorkspace("settings-tenancy-remover");
        var (otherWorkspaceId, otherEnvironmentId) = GivenWorkspace("settings-tenancy-victim");
        var theirAppId = GivenApp(otherWorkspaceId, otherEnvironmentId, "victim-app");
        var theirPageId = Guid.CreateVersion7();
        var theirComponentId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.StatusPages.Add(new StatusPage { Id = theirPageId, WorkspaceId = otherWorkspaceId, IsEnabled = true });
            db.StatusPageComponents.Add(new StatusPageComponent
            {
                Id = theirComponentId, WorkspaceId = otherWorkspaceId, StatusPageId = theirPageId,
                AppId = theirAppId, DisplayName = "Victim's component", SortOrder = 0
            });
        });

        Panel.GivenUser(workspaceId, "status-remover@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.29", "status-remover@example.com");
        await client.GetAsync("/status-page");

        var token = await client.AntiforgeryTokenFrom("/status-page");
        var response = await client.PostFormAsync($"/status-page/components/{theirComponentId}/remove", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Panel.Read(db => db.StatusPageComponents.Any(c => c.Id == theirComponentId)).Should().BeTrue(
            "the other workspace's component must be untouched");
    }
}
