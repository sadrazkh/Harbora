using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Services;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>The confirmed shape of the same POST — a typed name alongside the antiforgery token,
    /// exactly what the confirm page's own form submits.</summary>
    private async Task<HttpResponseMessage> ConfirmedDeleteAsync(HttpClient client, Guid projectId, string confirmName)
    {
        var token = await client.AntiforgeryTokenFrom($"/projects/{projectId}");
        return await client.PostFormAsync($"/projects/{projectId}/delete", token, ("confirmName", confirmName));
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

    /// <summary>
    /// The other half of P2's own guard: refusing forever with no way to discover a way through it is
    /// as much of a dead end as refusing silently. These four tests are the confirm-and-cascade path
    /// the three refusal tests above were always missing — the actual fix for "you cannot even delete
    /// a project".
    /// </summary>
    [Fact]
    public async Task The_confirm_page_names_every_app_and_database_the_delete_would_destroy()
    {
        var (project, environment) = SeedProject("confirm-page-lists");
        Panel.Seed(db =>
        {
            db.Apps.Add(new App
            {
                WorkspaceId = fixture.WorkspaceId, EnvironmentId = environment.Id, ServerId = Guid.CreateVersion7(),
                Name = "checkout-api", Slug = "checkout-api-" + project.Id.ToString("N")[..8],
                SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/checkout:1.0"
            });
            db.ManagedServices.Add(new ManagedService
            {
                WorkspaceId = fixture.WorkspaceId, EnvironmentId = environment.Id, ServerId = Guid.CreateVersion7(),
                Name = "orders-db", ContainerName = "harbora-svc-orders-db-" + project.Id.ToString("N")[..8],
                Type = ManagedServiceType.PostgreSql, DatabaseName = "orders", VolumeName = "orders-db-data"
            });
        });
        Panel.GivenUser(fixture.WorkspaceId, "confirm-page-lists@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.243", "confirm-page-lists@example.com");

        var response = await client.GetAsync($"/projects/{project.Id}/delete");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();

        html.Should().Contain("checkout-api").And.Contain("orders-db",
            "the confirm screen has to name exactly what the delete would destroy, not merely count it");
        html.Should().Contain(project.Name,
            "the typed-confirmation label has to show the project's own name for someone to type back");
    }

    [Fact]
    public async Task Typing_the_wrong_name_on_the_confirm_page_still_refuses_the_delete()
    {
        var (project, environment) = SeedProject("wrong-confirm-name");
        Panel.Seed(db => db.Apps.Add(new App
        {
            WorkspaceId = fixture.WorkspaceId, EnvironmentId = environment.Id, ServerId = Guid.CreateVersion7(),
            Name = "checkout-api", Slug = "checkout-api-" + project.Id.ToString("N")[..8],
            SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/checkout:1.0"
        }));
        Panel.GivenUser(fixture.WorkspaceId, "wrong-confirm-name@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.244", "wrong-confirm-name@example.com");

        var response = await ConfirmedDeleteAsync(client, project.Id, "not-the-project-name");

        response.RedirectPath().Should().Be($"/projects/{project.Id}");
        Panel.Read(db => db.Projects.Any(p => p.Id == project.Id)).Should().BeTrue(
            "a typo in the confirmation must not be treated as consent to destroy the project");
        Panel.Read(db => db.Apps.IgnoreQueryFilters().Any(a => a.EnvironmentId == environment.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Typing_the_projects_own_name_deletes_it_and_everything_inside_it()
    {
        var (project, environment) = SeedProject("full-cascade");
        Panel.Seed(db =>
        {
            db.Apps.Add(new App
            {
                WorkspaceId = fixture.WorkspaceId, EnvironmentId = environment.Id, ServerId = Guid.CreateVersion7(),
                Name = "checkout-api", Slug = "checkout-api-" + project.Id.ToString("N")[..8],
                SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/checkout:1.0"
            });
            db.ManagedServices.Add(new ManagedService
            {
                WorkspaceId = fixture.WorkspaceId, EnvironmentId = environment.Id, ServerId = Guid.CreateVersion7(),
                Name = "orders-db", ContainerName = "harbora-svc-orders-db-" + project.Id.ToString("N")[..8],
                Type = ManagedServiceType.PostgreSql, DatabaseName = "orders", VolumeName = "orders-db-data"
            });
        });
        Panel.GivenUser(fixture.WorkspaceId, "full-cascade@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.245", "full-cascade@example.com");

        var response = await ConfirmedDeleteAsync(client, project.Id, project.Name);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/projects",
            "a fully completed delete lands on the project list, the same place an empty project's delete does");

        Panel.Read(db => db.Projects.Any(p => p.Id == project.Id)).Should().BeFalse();
        Panel.Read(db => db.Environments.IgnoreQueryFilters().Any(e => e.ProjectId == project.Id)).Should().BeFalse();
        Panel.Read(db => db.Apps.IgnoreQueryFilters().Any(a => a.EnvironmentId == environment.Id)).Should().BeFalse();
        Panel.Read(db => db.ManagedServices.IgnoreQueryFilters().Any(s => s.EnvironmentId == environment.Id)).Should().BeFalse();
    }

    /// <summary>
    /// The must-not-lie guarantee: a container that resists removal must be named as still there, not
    /// papered over with "Deleted". Uses <c>FakeDockerEngine.UnremovableContainers</c> — the same fake
    /// every other delete-path test in this suite would reach for — to make one specific app's
    /// container removal throw, the way a real daemon does when a container is wedged.
    /// </summary>
    [Fact]
    public async Task A_container_that_will_not_stop_leaves_only_that_app_behind_and_says_so()
    {
        var (project, environment) = SeedProject("container-wont-stop");
        var stuckSlug = "stuck-api-" + project.Id.ToString("N")[..8];
        var fineSlug = "fine-worker-" + project.Id.ToString("N")[..8];
        Panel.Seed(db =>
        {
            db.Apps.Add(new App
            {
                WorkspaceId = fixture.WorkspaceId, EnvironmentId = environment.Id, ServerId = Guid.CreateVersion7(),
                Name = "stuck-api", Slug = stuckSlug,
                SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/stuck:1.0"
            });
            db.Apps.Add(new App
            {
                WorkspaceId = fixture.WorkspaceId, EnvironmentId = environment.Id, ServerId = Guid.CreateVersion7(),
                Name = "fine-worker", Slug = fineSlug,
                SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/fine:1.0"
            });
        });

        const string containerName = "harbora-stuck-1";
        Panel.Docker.SeedContainer(containerName, stuckSlug, workspaceId: fixture.WorkspaceId);
        Panel.Docker.UnremovableContainers.Add(containerName);

        Panel.GivenUser(fixture.WorkspaceId, "container-wont-stop@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.246", "container-wont-stop@example.com");

        var response = await ConfirmedDeleteAsync(client, project.Id, project.Name);

        response.RedirectPath().Should().Be($"/projects/{project.Id}",
            "a delete that did not finish must land back on the project, not on the (falsely) empty list");

        var html = await (await client.GetAsync(response.RedirectPath())).Content.ReadAsStringAsync();
        ErrorBannerText(html).Should().Contain("stuck-api",
            "the result must name exactly which app could not be removed, not just say the delete failed");

        Panel.Read(db => db.Projects.Any(p => p.Id == project.Id)).Should().BeTrue(
            "the project must not be reported deleted while one of its apps is still there");
        Panel.Read(db => db.Apps.IgnoreQueryFilters().Any(a => a.Slug == stuckSlug)).Should().BeTrue(
            "the app whose container would not stop must keep its row — removing it anyway would orphan the container");
        Panel.Read(db => db.Apps.IgnoreQueryFilters().Any(a => a.Slug == fineSlug)).Should().BeFalse(
            "the sibling app with no container problem must still be gone — one stuck item must not block the rest");
    }
}
