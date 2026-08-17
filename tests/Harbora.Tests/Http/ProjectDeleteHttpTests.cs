using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// P2's delete path (2026-08-17 app-environment-management design): <c>DeleteBehavior.Restrict</c>
/// replaced <c>SetNull</c> once <c>EnvironmentId</c> became required, so a project whose environment
/// still holds a workload can no longer be deleted by cascading it away silently — and the guard that
/// already refused this at the application level had to start naming what it refuses, not just
/// counting it. "Cannot delete" with no way to discover what is blocking it is a dead end, not a
/// guard, the same rule <c>ConfirmRemove</c> and the reserved-host refusals both already follow.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class ProjectDeleteHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private (Harbora.Domain.Projects.Project Project, Harbora.Domain.Projects.Environment Environment) SeedProject(
        string slug)
    {
        var project = new Harbora.Domain.Projects.Project
        { Id = Guid.CreateVersion7(), WorkspaceId = fixture.WorkspaceId, Name = slug, Slug = slug };
        var environment = new Harbora.Domain.Projects.Environment
        {
            Id = Guid.CreateVersion7(), WorkspaceId = fixture.WorkspaceId, ProjectId = project.Id,
            Name = "Production", Slug = "production", IsDefault = true
        };
        Panel.Seed(db =>
        {
            db.Projects.Add(project);
            db.Environments.Add(environment);
        });
        return (project, environment);
    }

    private async Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid projectId)
    {
        var token = await client.AntiforgeryTokenFrom($"/projects/{projectId}");
        return await client.PostFormAsync($"/projects/{projectId}/delete", token);
    }

    private static readonly Regex ErrorBanner = new(
        """<div class="mb-4 rounded-lg bg-danger-soft[^>]*>(?<text>.*?)</div>""",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// The refusal banner's own text — not the whole page, which lists this project's apps and
    /// databases as ordinary content regardless of whether the delete was refused. Asserting on the
    /// full page would pass even if the banner said only "2 apps and 1 database", because the name
    /// would still be sitting right there in the project's own list underneath it.
    /// </summary>
    private static string ErrorBannerText(string html)
    {
        var match = ErrorBanner.Match(html);
        match.Success.Should().BeTrue("a refused delete must render the danger banner Details.cshtml draws from TempData[\"Error\"]");
        return match.Groups["text"].Value;
    }

    [Fact]
    public async Task Deleting_a_project_whose_environment_still_holds_an_app_is_refused_and_names_the_app()
    {
        var (project, environment) = SeedProject("delete-refusal-app");
        Panel.Seed(db => db.Apps.Add(new App
        {
            WorkspaceId = fixture.WorkspaceId, EnvironmentId = environment.Id, ServerId = Guid.CreateVersion7(),
            Name = "checkout-api", Slug = "checkout-api-" + project.Id.ToString("N")[..8],
            SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/checkout:1.0"
        }));
        Panel.GivenUser(fixture.WorkspaceId, "delete-refusal-app@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.240", "delete-refusal-app@example.com");

        var response = await DeleteAsync(client, project.Id);

        response.StatusCode.Should().Be(HttpStatusCode.Found, "a refused delete redirects back to Details");
        response.RedirectPath().Should().Be($"/projects/{project.Id}");

        var html = await (await client.GetAsync(response.RedirectPath())).Content.ReadAsStringAsync();
        ErrorBannerText(html).Should().Contain("checkout-api",
            "the refusal banner itself must name the app blocking the delete, not just say how many " +
            "there are — the app's name appearing anywhere else on the page (its own row in the " +
            "project's list, for instance) does not prove the refusal named it");

        Panel.Read(db => db.Projects.Any(p => p.Id == project.Id)).Should().BeTrue(
            "the project must still be there — the delete was refused, not silently applied anyway");
    }

    [Fact]
    public async Task Deleting_a_project_whose_environment_still_holds_a_database_is_refused_and_names_it()
    {
        var (project, environment) = SeedProject("delete-refusal-db");
        Panel.Seed(db => db.ManagedServices.Add(new ManagedService
        {
            WorkspaceId = fixture.WorkspaceId, EnvironmentId = environment.Id, ServerId = Guid.CreateVersion7(),
            Name = "orders-db", ContainerName = "harbora-svc-orders-db-" + project.Id.ToString("N")[..8],
            Type = ManagedServiceType.PostgreSql, DatabaseName = "orders",
            VolumeName = "orders-db-data"
        }));
        Panel.GivenUser(fixture.WorkspaceId, "delete-refusal-db@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.241", "delete-refusal-db@example.com");

        var response = await DeleteAsync(client, project.Id);

        response.RedirectPath().Should().Be($"/projects/{project.Id}");
        var html = await (await client.GetAsync(response.RedirectPath())).Content.ReadAsStringAsync();
        ErrorBannerText(html).Should().Contain("orders-db",
            "the refusal banner itself must name the database blocking the delete, not just say how " +
            "many there are");

        Panel.Read(db => db.Projects.Any(p => p.Id == project.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task A_project_whose_environment_holds_nothing_still_deletes()
    {
        var (project, _) = SeedProject("delete-empty");
        Panel.GivenUser(fixture.WorkspaceId, "delete-empty@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.242", "delete-empty@example.com");

        var response = await DeleteAsync(client, project.Id);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/projects",
            "a successful delete lands on the project list, not back on the (now gone) project's own page");

        Panel.Read(db => db.Projects.Any(p => p.Id == project.Id)).Should().BeFalse(
            "nothing was placed in this project's environment, so Restrict had nothing to refuse");
    }
}
