using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Environment = Harbora.Domain.Projects.Environment;
using Project = Harbora.Domain.Projects.Project;

namespace Harbora.Tests;

/// <summary>
/// The project/environment layer added on top of the existing model.
///
/// The constraint that shaped it: the deploy engine is the one part of Harbora proven to work end to
/// end, so <c>Apps</c> keeps its name, its columns and its behaviour. Everything here is additive.
/// EnvironmentId itself became required in P2 (2026-08-17 app-environment-management design), once
/// the 2026-07-30 backfill had placed every row that predated it.
/// </summary>
public class ProjectModelTests
{
    private static readonly Guid Workspace = Guid.CreateVersion7();
    private static readonly Guid OtherWorkspace = Guid.CreateVersion7();

    private sealed class Scope(Guid workspaceId, bool unscoped = false) : IWorkspaceScope
    {
        public bool IsUnscoped => unscoped;
        public Guid WorkspaceId => workspaceId;
    }

    private static HarboraDbContext NewDb(string name, IWorkspaceScope? scope = null)
    {
        var options = new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(name).Options;
        return scope is null ? new HarboraDbContext(options) : new HarboraDbContext(options, scope);
    }

    private static (Project Project, Environment Environment) Seed(HarboraDbContext db, Guid workspaceId)
    {
        var project = new Project { WorkspaceId = workspaceId, Name = "Shop", Slug = "shop" };
        var environment = new Environment
        {
            WorkspaceId = workspaceId, Project = project,
            Name = "Production", Slug = "production", IsDefault = true
        };
        db.Projects.Add(project);
        db.Environments.Add(environment);
        db.SaveChanges();
        return (project, environment);
    }

    [Fact]
    public void An_app_can_belong_to_an_environment()
    {
        using var db = NewDb("proj-" + Guid.NewGuid());
        var (_, environment) = Seed(db, Workspace);

        db.Apps.Add(new App
        {
            WorkspaceId = Workspace, ServerId = Guid.CreateVersion7(),
            Name = "api", Slug = "api", EnvironmentId = environment.Id
        });
        db.SaveChanges();

        db.Apps.Single().EnvironmentId.Should().Be(environment.Id);
    }

    // The test that used to live here, An_app_without_an_environment_is_still_valid, asserted the
    // opposite of what P2 (2026-08-17 app-environment-management design) makes true: EnvironmentId is
    // a required Guid now, and there is no longer a C# value that expresses "no environment" for a
    // test in this file to construct. Deleted rather than inverted in place, because the invariant it
    // would need to prove — a dangling EnvironmentId is refused — is a real foreign-key constraint
    // that only a real database enforces; EF's InMemory provider does not validate that a scalar FK
    // resolves to an existing row, so an inverted version here would assert something InMemory cannot
    // actually demonstrate. EnvironmentColumnPostgresTests in the Postgres lane proves it for real.

    [Fact]
    public void An_existing_app_defaults_to_a_web_service()
    {
        // The column backfills to the behaviour every current app already has, so adding it changes
        // nothing about how anything deploys.
        new App().Kind.Should().Be(ServiceKind.Web);
    }

    [Fact]
    public void Projects_are_scoped_to_their_workspace()
    {
        var name = "proj-tenant-" + Guid.NewGuid();
        using (var seed = NewDb(name))
        {
            Seed(seed, Workspace);
            Seed(seed, OtherWorkspace);
        }

        using var mine = NewDb(name, new Scope(Workspace));

        mine.Projects.Should().ContainSingle().Which.WorkspaceId.Should().Be(Workspace);
        mine.Environments.Should().ContainSingle().Which.WorkspaceId.Should().Be(Workspace);
    }

    [Fact]
    public void A_background_job_still_sees_every_workspace()
    {
        // Schedulers, reconcilers and the metering service legitimately span tenants; the unscoped
        // context is what they run under.
        var name = "proj-system-" + Guid.NewGuid();
        using (var seed = NewDb(name))
        {
            Seed(seed, Workspace);
            Seed(seed, OtherWorkspace);
        }

        using var system = NewDb(name, new Scope(Guid.Empty, unscoped: true));

        system.Projects.Should().HaveCount(2);
    }

    [Fact]
    public void An_environment_carries_its_own_workspace_id()
    {
        // Denormalised on purpose: filtering through the parent project turns the query into a join,
        // which hides rows whose parent is momentarily missing — the same trap deployments already
        // avoid for the crash reconciler's sake.
        using var db = NewDb("proj-" + Guid.NewGuid());
        var (project, environment) = Seed(db, Workspace);

        environment.WorkspaceId.Should().Be(project.WorkspaceId);
    }

    [Fact]
    public void A_project_can_hold_several_environments()
    {
        using var db = NewDb("proj-" + Guid.NewGuid());
        var (project, _) = Seed(db, Workspace);

        db.Environments.Add(new Environment
        {
            WorkspaceId = Workspace, ProjectId = project.Id, Name = "Staging", Slug = "staging"
        });
        db.SaveChanges();

        db.Environments.Where(e => e.ProjectId == project.Id).Should().HaveCount(2);
        db.Environments.Count(e => e.IsDefault).Should().Be(1, "exactly one environment is the default");
    }

    [Fact]
    public void A_managed_database_belongs_to_an_environment_too()
    {
        using var db = NewDb("proj-" + Guid.NewGuid());
        var (_, environment) = Seed(db, Workspace);

        db.ManagedServices.Add(new Harbora.Domain.Services.ManagedService
        {
            WorkspaceId = Workspace, ServerId = Guid.CreateVersion7(), Name = "db",
            ContainerName = "harbora-svc-db", VolumeName = "harbora-svc-db-data",
            EnvironmentId = environment.Id
        });
        db.SaveChanges();

        db.ManagedServices.Single().EnvironmentId.Should().Be(environment.Id);
    }
}
