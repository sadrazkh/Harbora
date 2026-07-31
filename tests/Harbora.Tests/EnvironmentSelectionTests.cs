using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Projects;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Environment = Harbora.Domain.Projects.Environment;

namespace Harbora.Tests;

/// <summary>
/// Choosing which environment a new service is created in.
///
/// The environment arrives as an id in a form field, which is the most obvious thing in the page to
/// change by hand. The rule the controller implements, and that these tests pin: an id is honoured
/// only after it is shown to belong to the caller's workspace, and anything else quietly falls back
/// to that workspace's own default rather than failing or, far worse, succeeding somewhere else.
/// </summary>
public class EnvironmentSelectionTests
{
    private static HarboraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("envsel-" + Guid.NewGuid()).Options);

    private static Guid SeedWorkspace(HarboraDbContext db, string name)
    {
        var ws = new Workspace { Name = name, Slug = ProjectService.Slugify(name) };
        db.Workspaces.Add(ws);
        db.SaveChanges();
        return ws.Id;
    }

    /// <summary>
    /// Calls the real resolution the controllers use. An earlier version of this file reimplemented
    /// the rule here, which would have passed happily while the controller did something else — the
    /// exact way a security test becomes decoration.
    /// </summary>
    private static Task<Environment> ResolveAsync(
        HarboraDbContext db, ProjectService projects, Guid workspaceId, Guid? requested)
        => projects.ResolveEnvironmentAsync(workspaceId, requested, default);

    [Fact]
    public async Task An_environment_from_another_workspace_is_refused_and_replaced_with_our_own()
    {
        // The one that matters: a tampered id must never place a service in another customer's project.
        await using var db = NewDb();
        var projects = new ProjectService(db, new FixedClock());
        var mine = SeedWorkspace(db, "Mine");
        var theirs = SeedWorkspace(db, "Theirs");
        var (_, theirEnvironment) = await projects.CreateAsync(theirs, "Their project", null, default);

        var resolved = await ResolveAsync(db, projects, mine, theirEnvironment.Id);

        resolved.Id.Should().NotBe(theirEnvironment.Id);
        resolved.WorkspaceId.Should().Be(mine);
    }

    [Fact]
    public async Task A_chosen_environment_in_our_own_workspace_is_honoured()
    {
        await using var db = NewDb();
        var projects = new ProjectService(db, new FixedClock());
        var workspace = SeedWorkspace(db, "Mine");
        var (project, _) = await projects.CreateAsync(workspace, "Shop", null, default);
        var staging = await projects.AddEnvironmentAsync(workspace, project.Id, "Staging", default);

        var resolved = await ResolveAsync(db, projects, workspace, staging.Id);

        resolved.Id.Should().Be(staging.Id);
    }

    [Fact]
    public async Task No_choice_falls_back_to_the_default_environment()
    {
        // What every existing link, and the CLI, does — it must keep working unchanged.
        await using var db = NewDb();
        var projects = new ProjectService(db, new FixedClock());
        var workspace = SeedWorkspace(db, "Mine");
        var (_, production) = await projects.CreateAsync(workspace, "Shop", null, default);

        var resolved = await ResolveAsync(db, projects, workspace, null);

        resolved.Id.Should().Be(production.Id);
        resolved.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task An_environment_that_does_not_exist_falls_back_rather_than_failing()
    {
        // A stale bookmark should not be an error page.
        await using var db = NewDb();
        var projects = new ProjectService(db, new FixedClock());
        var workspace = SeedWorkspace(db, "Mine");

        var resolved = await ResolveAsync(db, projects, workspace, Guid.CreateVersion7());

        resolved.WorkspaceId.Should().Be(workspace);
    }
}
