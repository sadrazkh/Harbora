using System.Net;
using FluentAssertions;
using Harbora.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The switch that turns 5.2's approval gate on (2026-09 market-gaps round two): without a way to
/// mark an environment protected in the panel, the whole gate is unreachable — <c>Environment.IsProtected</c>
/// existed on the model already, with nothing anywhere that ever read or wrote it, before this.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class EnvironmentProtectionHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private (Harbora.Domain.Projects.Project Project, Harbora.Domain.Projects.Environment Environment) SeedProject(
        string slug, bool isProtected = false)
    {
        var project = new Harbora.Domain.Projects.Project
        { Id = Guid.CreateVersion7(), WorkspaceId = fixture.WorkspaceId, Name = slug, Slug = slug };
        var environment = new Harbora.Domain.Projects.Environment
        {
            Id = Guid.CreateVersion7(), WorkspaceId = fixture.WorkspaceId, ProjectId = project.Id,
            Name = "Production", Slug = slug + "-production", IsDefault = true, IsProtected = isProtected
        };
        Panel.Seed(db =>
        {
            db.Projects.Add(project);
            db.Environments.Add(environment);
        });
        return (project, environment);
    }

    private async Task<HttpResponseMessage> SetProtectionAsync(
        HttpClient client, Guid projectId, Guid environmentId, bool isProtected)
    {
        var token = await client.AntiforgeryTokenFrom($"/projects/{projectId}?environmentId={environmentId}");
        return await client.PostFormAsync(
            $"/projects/{projectId}/environments/{environmentId}/protection", token,
            ("isProtected", isProtected.ToString()));
    }

    [Fact]
    public async Task An_owner_can_turn_protection_on_and_it_sticks()
    {
        var (project, environment) = SeedProject("protect-on");
        Panel.GivenUser(fixture.WorkspaceId, "protect-owner1@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.51", "protect-owner1@example.com");

        var response = await SetProtectionAsync(client, project.Id, environment.Id, true);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.Environments.Single(e => e.Id == environment.Id)).IsProtected.Should().BeTrue();
    }

    [Fact]
    public async Task Turning_it_back_off_also_sticks()
    {
        var (project, environment) = SeedProject("protect-off", isProtected: true);
        Panel.GivenUser(fixture.WorkspaceId, "protect-owner2@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.52", "protect-owner2@example.com");

        var response = await SetProtectionAsync(client, project.Id, environment.Id, false);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.Environments.Single(e => e.Id == environment.Id)).IsProtected.Should().BeFalse();
    }

    [Fact]
    public async Task Off_by_default_on_a_freshly_created_environment()
    {
        var (project, environment) = SeedProject("protect-default");

        Panel.Read(db => db.Environments.Single(e => e.Id == environment.Id)).IsProtected.Should().BeFalse();
    }

    [Fact]
    public async Task A_viewer_cannot_toggle_protection()
    {
        var (project, environment) = SeedProject("protect-viewer");
        Panel.GivenUser(fixture.WorkspaceId, "protect-viewer1@example.com", SystemRole.Viewer);
        var client = await Panel.SignedInAs("192.0.2.10", "protect-viewer1@example.com");

        var response = await SetProtectionAsync(client, project.Id, environment.Id, true);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/denied");
        Panel.Read(db => db.Environments.Single(e => e.Id == environment.Id)).IsProtected.Should().BeFalse();
    }

    [Fact]
    public async Task Another_workspaces_environment_is_refused()
    {
        var otherWorkspaceId = Guid.CreateVersion7();
        Panel.Seed(db => db.Workspaces.Add(new Harbora.Domain.Identity.Workspace
        { Id = otherWorkspaceId, Name = "Protect Victim Co", Slug = "protect-victim-" + otherWorkspaceId }));
        var victimsProject = new Harbora.Domain.Projects.Project
        { Id = Guid.CreateVersion7(), WorkspaceId = otherWorkspaceId, Name = "victim", Slug = "protect-victim" };
        var victimsEnvironment = new Harbora.Domain.Projects.Environment
        {
            Id = Guid.CreateVersion7(), WorkspaceId = otherWorkspaceId, ProjectId = victimsProject.Id,
            Name = "Production", Slug = "protect-victim-production", IsDefault = true
        };
        Panel.Seed(db => { db.Projects.Add(victimsProject); db.Environments.Add(victimsEnvironment); });

        Panel.GivenUser(fixture.WorkspaceId, "protect-attacker@example.com", SystemRole.Owner);
        var attacker = await Panel.SignedInAs("192.0.2.11", "protect-attacker@example.com");

        var token = await attacker.AntiforgeryTokenFrom("/projects");
        var response = await attacker.PostFormAsync(
            $"/projects/{victimsProject.Id}/environments/{victimsEnvironment.Id}/protection", token,
            ("isProtected", "true"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Panel.Read(db => db.Environments.Single(e => e.Id == victimsEnvironment.Id)).IsProtected.Should().BeFalse();
    }

    [Fact]
    public async Task The_projects_page_offers_the_toggle_and_shows_the_current_state()
    {
        var (project, environment) = SeedProject("protect-page", isProtected: true);
        Panel.GivenUser(fixture.WorkspaceId, "protect-page1@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("192.0.2.12", "protect-page1@example.com");

        var html = await (await client.GetAsync($"/projects/{project.Id}?environmentId={environment.Id}"))
            .Content.ReadAsStringAsync();

        html.Should().Contain($"action=\"/projects/{project.Id}/environments/{environment.Id}/protection\"");
        html.Should().Contain("data-protected=\"true\"");
    }
}
