using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Projects;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Round six: creating a project or an environment now writes its own audit entry, inside
/// <see cref="ProjectService"/>, so the panel's forms and the in-panel agent leave the same trace and
/// no caller has to remember to. Before this neither was audited anywhere.
/// </summary>
public class ProjectServiceAuditTests
{
    private static HarboraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("project-audit-" + Guid.NewGuid()).Options);

    private static Guid SeedWorkspace(HarboraDbContext db)
    {
        var ws = new Workspace { Name = "Acme", Slug = "acme" };
        db.Workspaces.Add(ws);
        db.SaveChanges();
        return ws.Id;
    }

    [Fact]
    public async Task Creating_a_project_audits_it_against_its_workspace()
    {
        await using var db = NewDb();
        var workspace = SeedWorkspace(db);
        var audit = new DatabaseServiceHarness.RecordingAudit();

        var (project, environment) = await new ProjectService(db, new FixedClock(), audit: audit)
            .CreateAsync(workspace, "Shop", null, default);

        var entry = audit.Entries.Should().ContainSingle().Which;
        entry.Action.Should().Be("project.created");
        entry.TargetType.Should().Be("project");
        entry.TargetId.Should().Be(project.Id.ToString());
        entry.WorkspaceId.Should().Be(workspace);
        entry.MetadataJson.Should().Contain("\"slug\":\"shop\"").And.Contain(environment.Id.ToString());
    }

    [Fact]
    public async Task Adding_an_environment_audits_it_against_its_workspace()
    {
        await using var db = NewDb();
        var workspace = SeedWorkspace(db);
        var audit = new DatabaseServiceHarness.RecordingAudit();
        var service = new ProjectService(db, new FixedClock(), audit: audit);
        var (project, _) = await service.CreateAsync(workspace, "Shop", null, default);
        audit.Entries.Clear();

        var staging = await service.AddEnvironmentAsync(workspace, project.Id, "Staging", default);

        var entry = audit.Entries.Should().ContainSingle().Which;
        entry.Action.Should().Be("environment.created");
        entry.TargetType.Should().Be("environment");
        entry.TargetId.Should().Be(staging.Id.ToString());
        entry.WorkspaceId.Should().Be(workspace);
        entry.MetadataJson.Should().Contain(project.Id.ToString());
    }

    [Fact]
    public async Task Preparing_a_project_for_someone_elses_unit_of_work_audits_nothing()
    {
        // PrepareAsync saves nothing; an entry written now would outlive a stack deployment that rolls
        // back. Its caller owns the commit, and with it whatever it chooses to record.
        await using var db = NewDb();
        var workspace = SeedWorkspace(db);
        var audit = new DatabaseServiceHarness.RecordingAudit();

        await new ProjectService(db, new FixedClock(), audit: audit).PrepareAsync(workspace, "Stack", null, default);

        audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task The_default_project_made_on_demand_is_audited_once_not_on_every_ask()
    {
        await using var db = NewDb();
        var workspace = SeedWorkspace(db);
        var audit = new DatabaseServiceHarness.RecordingAudit();
        var service = new ProjectService(db, new FixedClock(), audit: audit);

        await service.EnsureDefaultEnvironmentAsync(workspace, default);
        await service.EnsureDefaultEnvironmentAsync(workspace, default);

        audit.Entries.Should().ContainSingle().Which.Action.Should().Be("project.created");
    }
}
