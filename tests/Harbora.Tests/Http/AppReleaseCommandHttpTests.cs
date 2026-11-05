using System.Net;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The fix a launched app's own check names for an empty database. It could only be set when the app was
/// created, so the check was telling people to do something the panel gave them no way to do.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AppReleaseCommandHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private Guid SeedApp(string slug, string? releaseCommand = null, Guid? workspaceId = null)
    {
        var workspace = workspaceId ?? fixture.WorkspaceId;
        var projectId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();
        var app = new App
        {
            WorkspaceId = workspace,
            ServerId = Guid.CreateVersion7(),
            EnvironmentId = environmentId,
            Name = slug,
            Slug = slug,
            Kind = ServiceKind.Web,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Running,
            ReleaseCommand = releaseCommand
        };
        Panel.Seed(db =>
        {
            db.Projects.Add(new Harbora.Domain.Projects.Project
            {
                Id = projectId, WorkspaceId = workspace, Name = "Shop", Slug = "release-cmd-" + slug
            });
            db.Environments.Add(new Harbora.Domain.Projects.Environment
            {
                Id = environmentId, WorkspaceId = workspace, ProjectId = projectId,
                Name = "Production", Slug = "production", IsDefault = true
            });
            db.Apps.Add(app);
        });
        return app.Id;
    }

    private string? ReleaseCommandOf(Guid appId)
    {
        string? found = null;
        Panel.Seed(db => found = db.Apps.IgnoreQueryFilters().Single(a => a.Id == appId).ReleaseCommand);
        return found;
    }

    [Fact]
    public async Task Setting_it_writes_the_row_and_the_page_shows_it_in_the_field()
    {
        var appId = SeedApp("release-cmd-set");
        Panel.GivenUser(fixture.WorkspaceId, "release-cmd-set@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.221", "release-cmd-set@example.com");

        var response = await client.PostFormAsync($"/apps/{appId}/release-command",
            await client.AntiforgeryTokenFrom($"/apps/details/{appId}"),
            ("releaseCommand", "  npm run migrate  "));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().BeEquivalentTo($"/apps/details/{appId}");
        ReleaseCommandOf(appId).Should().Be("npm run migrate");

        var page = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();
        page.Should().Contain("data-release-command-input").And.Contain("value=\"npm run migrate\"");
    }

    [Fact]
    public async Task A_blank_answer_clears_it()
    {
        var appId = SeedApp("release-cmd-clear", releaseCommand: "npm run migrate");
        Panel.GivenUser(fixture.WorkspaceId, "release-cmd-clear@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.222", "release-cmd-clear@example.com");

        await client.PostFormAsync($"/apps/{appId}/release-command",
            await client.AntiforgeryTokenFrom($"/apps/details/{appId}"),
            ("releaseCommand", "   "));

        ReleaseCommandOf(appId).Should().BeNull();
    }

    [Fact]
    public async Task A_viewer_may_not_set_it()
    {
        var appId = SeedApp("release-cmd-viewer", releaseCommand: "npm run migrate");
        Panel.GivenUser(fixture.WorkspaceId, "release-cmd-viewer@example.com", SystemRole.Viewer);
        var client = await Panel.SignedInAs("203.0.113.223", "release-cmd-viewer@example.com");

        var page = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();
        page.Should().NotContain("data-release-command-input", "a Save that can only be refused is not an offer");
        page.Should().NotContain($"action=\"/apps/{appId}/release-command\"");

        var response = await client.PostFormAsync($"/apps/{appId}/release-command",
            await client.AntiforgeryTokenFrom($"/apps/details/{appId}"),
            ("releaseCommand", "rm -rf /"));

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
        ReleaseCommandOf(appId).Should().Be("npm run migrate");
    }

    [Fact]
    public async Task Without_the_antiforgery_token_nothing_changes()
    {
        var appId = SeedApp("release-cmd-csrf", releaseCommand: "npm run migrate");
        Panel.GivenUser(fixture.WorkspaceId, "release-cmd-csrf@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.224", "release-cmd-csrf@example.com");

        var response = await client.PostAsync($"/apps/{appId}/release-command",
            new FormUrlEncodedContent([new("releaseCommand", "npm run other")]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ReleaseCommandOf(appId).Should().Be("npm run migrate");
    }
}
