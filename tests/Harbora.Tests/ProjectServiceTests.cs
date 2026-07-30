using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Projects;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Creating projects and environments.
///
/// One invariant holds everything else up: a workspace always has a project, and a project always has
/// an environment. The migration made that true for existing data; this keeps it true afterwards, so
/// no screen ever has to render "belongs to nothing" — the state that would otherwise turn up months
/// later, in production, on a customer's dashboard.
/// </summary>
public class ProjectServiceTests
{
    private static HarboraDbContext NewDb(string? name = null) =>
        new(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase(name ?? "projects-" + Guid.NewGuid()).Options);

    private static ProjectService ServiceOn(HarboraDbContext db) => new(db, new FixedClock());

    private static Guid SeedWorkspace(HarboraDbContext db, string name = "Acme")
    {
        var ws = new Workspace { Name = name, Slug = ProjectService.Slugify(name) };
        db.Workspaces.Add(ws);
        db.SaveChanges();
        return ws.Id;
    }

    [Fact]
    public async Task Creating_a_project_creates_an_environment_with_it()
    {
        // A project with nowhere to deploy is not a state worth being able to represent.
        await using var db = NewDb();
        var workspace = SeedWorkspace(db);

        var (project, environment) = await ServiceOn(db).CreateAsync(workspace, "Shop", null, default);

        project.Slug.Should().Be("shop");
        environment.ProjectId.Should().Be(project.Id);
        environment.Slug.Should().Be(ProjectService.DefaultEnvironmentSlug);
        environment.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task A_workspace_with_no_project_gets_one_on_demand()
    {
        // Workspaces created after the migration would otherwise have nothing, and an empty page no
        // button could fix.
        await using var db = NewDb();
        var workspace = SeedWorkspace(db, "New Customer");

        var environment = await ServiceOn(db).EnsureDefaultEnvironmentAsync(workspace, default);

        environment.WorkspaceId.Should().Be(workspace);
        db.Projects.Should().ContainSingle().Which.Slug.Should().Be(ProjectService.DefaultProjectSlug);
    }

    [Fact]
    public async Task Asking_twice_does_not_create_a_second_project()
    {
        // This runs on page loads and on every create, so it has to be idempotent.
        await using var db = NewDb();
        var workspace = SeedWorkspace(db);
        var service = ServiceOn(db);

        var first = await service.EnsureDefaultEnvironmentAsync(workspace, default);
        var again = await service.EnsureDefaultEnvironmentAsync(workspace, default);

        again.Id.Should().Be(first.Id);
        db.Projects.Should().ContainSingle();
    }

    [Fact]
    public async Task An_existing_project_is_reused_even_if_it_was_not_the_default_one()
    {
        // A workspace whose only project was created by hand still counts as having one.
        await using var db = NewDb();
        var workspace = SeedWorkspace(db);
        var service = ServiceOn(db);
        var (_, mine) = await service.CreateAsync(workspace, "Handmade", null, default);

        var resolved = await service.EnsureDefaultEnvironmentAsync(workspace, default);

        resolved.Id.Should().Be(mine.Id);
        db.Projects.Should().ContainSingle();
    }

    [Fact]
    public async Task Two_projects_with_the_same_name_get_different_slugs()
    {
        // The slug is unique per workspace and ends up in URLs and network names.
        await using var db = NewDb();
        var workspace = SeedWorkspace(db);
        var service = ServiceOn(db);

        var (first, _) = await service.CreateAsync(workspace, "Shop", null, default);
        var (second, _) = await service.CreateAsync(workspace, "Shop", null, default);

        first.Slug.Should().Be("shop");
        second.Slug.Should().Be("shop-2");
    }

    [Fact]
    public async Task Another_workspace_may_reuse_a_slug()
    {
        // Slugs are scoped to a workspace; two customers can both have a project called "shop".
        await using var db = NewDb();
        var mine = SeedWorkspace(db, "Mine");
        var theirs = SeedWorkspace(db, "Theirs");
        var service = ServiceOn(db);

        var (a, _) = await service.CreateAsync(mine, "Shop", null, default);
        var (b, _) = await service.CreateAsync(theirs, "Shop", null, default);

        a.Slug.Should().Be(b.Slug);
        a.WorkspaceId.Should().NotBe(b.WorkspaceId);
    }

    [Fact]
    public async Task A_second_environment_is_never_the_default()
    {
        // Promoting an environment is a deliberate act, not a side effect of adding one.
        await using var db = NewDb();
        var workspace = SeedWorkspace(db);
        var service = ServiceOn(db);
        var (project, production) = await service.CreateAsync(workspace, "Shop", null, default);

        var staging = await service.AddEnvironmentAsync(workspace, project.Id, "Staging", default);

        staging.IsDefault.Should().BeFalse();
        production.IsDefault.Should().BeTrue();
        db.Environments.Count(e => e.ProjectId == project.Id && e.IsDefault).Should().Be(1);
    }

    [Fact]
    public async Task Environment_slugs_are_unique_within_a_project_but_not_across_projects()
    {
        await using var db = NewDb();
        var workspace = SeedWorkspace(db);
        var service = ServiceOn(db);
        var (one, _) = await service.CreateAsync(workspace, "One", null, default);
        var (two, _) = await service.CreateAsync(workspace, "Two", null, default);

        var a = await service.AddEnvironmentAsync(workspace, one.Id, "Staging", default);
        var b = await service.AddEnvironmentAsync(workspace, two.Id, "Staging", default);
        var duplicate = await service.AddEnvironmentAsync(workspace, one.Id, "Staging", default);

        a.Slug.Should().Be("staging");
        b.Slug.Should().Be("staging", "each project has its own namespace");
        duplicate.Slug.Should().Be("staging-2");
    }

    [Theory]
    [InlineData("My Shop!", "my-shop")]
    [InlineData("  Spaces  ", "spaces")]
    [InlineData("سلام", "")]          // non-latin reduces to nothing; the caller supplies a fallback
    [InlineData("A___B", "a-b")]
    public void Slugs_stay_dns_safe(string input, string expected)
    {
        // The slug goes into the private network name and into internal hostnames, so it has to
        // survive DNS, not merely a URL.
        ProjectService.Slugify(input).Should().Be(expected);
    }

    [Fact]
    public async Task A_project_named_only_in_persian_still_gets_a_usable_slug()
    {
        // Slugify strips it to nothing, so the fallback matters — the panel's default language is
        // Persian and this is the ordinary case, not an edge one.
        await using var db = NewDb();
        var workspace = SeedWorkspace(db);

        var (project, _) = await ServiceOn(db).CreateAsync(workspace, "فروشگاه", null, default);

        project.Slug.Should().NotBeEmpty();
        project.Name.Should().Be("فروشگاه", "the display name keeps what was typed");
    }
}
