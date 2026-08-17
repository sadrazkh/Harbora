using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Identity;
using Harbora.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Environment = Harbora.Domain.Projects.Environment;
using Project = Harbora.Domain.Projects.Project;

namespace Harbora.Postgres.Tests;

/// <summary>
/// P2 (2026-08-17 app-environment-management design) against real PostgreSQL: <c>EnvironmentId</c> is
/// a required foreign key on <c>Apps</c> and <c>ManagedServices</c> now, with
/// <c>DeleteBehavior.Restrict</c> in place of the old <c>SetNull</c>.
///
/// <para>
/// Neither half is provable over EF's InMemory provider, which is why this file exists rather than
/// living beside the rest of the schema's tests in <c>Harbora.Tests</c>. InMemory does not validate
/// that a scalar foreign key resolves to a real row — <c>ProjectModelTests</c> used to hold an
/// in-memory version of the first fact here and was deleted for exactly that reason — and it has no
/// concept of a database-level delete restriction at all: <c>DeleteBehavior.Restrict</c> only ever
/// throws when a real foreign-key constraint refuses a <c>DELETE</c>.
/// </para>
/// </summary>
[Collection(PostgresLane.Collection)]
public sealed class EnvironmentColumnPostgresTests(PostgresLane lane)
{
    [PostgresFact]
    public async Task An_app_pointed_at_an_environment_that_does_not_exist_is_refused()
    {
        var connectionString = await lane.FreshlyMigratedAsync("environment-column-fk-app");
        await using var db = PostgresLane.Open(connectionString);

        var workspace = new Workspace { Name = "acme", Slug = "acme" };
        db.Workspaces.Add(workspace);
        db.Apps.Add(new App
        {
            WorkspaceId = workspace.Id, ServerId = Guid.CreateVersion7(),
            Name = "orphan", Slug = "orphan", EnvironmentId = Guid.CreateVersion7()
        });

        await db.Invoking(x => x.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>(
            "EnvironmentId is a required foreign key now, and this one names an environment that " +
            "does not exist");
    }

    [PostgresFact]
    public async Task A_managed_service_pointed_at_an_environment_that_does_not_exist_is_refused()
    {
        var connectionString = await lane.FreshlyMigratedAsync("environment-column-fk-service");
        await using var db = PostgresLane.Open(connectionString);

        var workspace = new Workspace { Name = "acme", Slug = "acme" };
        db.Workspaces.Add(workspace);
        db.ManagedServices.Add(new ManagedService
        {
            WorkspaceId = workspace.Id, ServerId = Guid.CreateVersion7(),
            Name = "orphan-db", ContainerName = "harbora-svc-orphan-db",
            EnvironmentId = Guid.CreateVersion7()
        });

        await db.Invoking(x => x.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
    }

    [PostgresFact]
    public async Task Deleting_an_environment_that_still_holds_an_app_is_refused()
    {
        var connectionString = await lane.FreshlyMigratedAsync("environment-column-restrict-app");
        Guid environmentId;
        await using (var seed = PostgresLane.Open(connectionString))
        {
            var (_, environment) = await SeedPlacementAsync(seed);
            seed.Apps.Add(new App
            {
                WorkspaceId = environment.WorkspaceId, ServerId = Guid.CreateVersion7(),
                Name = "web", Slug = "web", EnvironmentId = environment.Id
            });
            await seed.SaveChangesAsync();
            environmentId = environment.Id;
        }

        await using var db = PostgresLane.Open(connectionString);
        var toDelete = await db.Environments.SingleAsync(e => e.Id == environmentId);
        db.Environments.Remove(toDelete);

        // Restrict, not SetNull and not Cascade: the app must survive, still pointed at this
        // environment, exactly as it was before the delete was attempted.
        await db.Invoking(x => x.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>(
            "the environment still holds an app, and Restrict must refuse rather than delete it or " +
            "null out the app's own required column");

        await using var verify = PostgresLane.Open(connectionString);
        (await verify.Environments.CountAsync(e => e.Id == environmentId)).Should().Be(1);
        var appAfter = await verify.Apps.SingleAsync();
        appAfter.EnvironmentId.Should().Be(environmentId);
    }

    [PostgresFact]
    public async Task Deleting_an_environment_that_still_holds_a_database_is_refused()
    {
        var connectionString = await lane.FreshlyMigratedAsync("environment-column-restrict-db");
        Guid environmentId;
        await using (var seed = PostgresLane.Open(connectionString))
        {
            var (_, environment) = await SeedPlacementAsync(seed);
            seed.ManagedServices.Add(new ManagedService
            {
                WorkspaceId = environment.WorkspaceId, ServerId = Guid.CreateVersion7(),
                Name = "orders", ContainerName = "harbora-svc-orders", EnvironmentId = environment.Id
            });
            await seed.SaveChangesAsync();
            environmentId = environment.Id;
        }

        await using var db = PostgresLane.Open(connectionString);
        var toDelete = await db.Environments.SingleAsync(e => e.Id == environmentId);
        db.Environments.Remove(toDelete);

        await db.Invoking(x => x.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();

        await using var verify = PostgresLane.Open(connectionString);
        (await verify.Environments.CountAsync(e => e.Id == environmentId)).Should().Be(1);
        var serviceAfter = await verify.ManagedServices.SingleAsync();
        serviceAfter.EnvironmentId.Should().Be(environmentId);
    }

    [PostgresFact]
    public async Task An_environment_with_nothing_in_it_still_deletes()
    {
        var connectionString = await lane.FreshlyMigratedAsync("environment-column-empty-delete");
        Guid environmentId;
        await using (var seed = PostgresLane.Open(connectionString))
        {
            var (_, environment) = await SeedPlacementAsync(seed);
            environmentId = environment.Id;
        }

        await using (var db = PostgresLane.Open(connectionString))
        {
            var toDelete = await db.Environments.SingleAsync(e => e.Id == environmentId);
            db.Environments.Remove(toDelete);
            await db.Invoking(x => x.SaveChangesAsync()).Should().NotThrowAsync(
                "nothing is placed in this environment, so Restrict has nothing to refuse");
        }

        await using var verify = PostgresLane.Open(connectionString);
        (await verify.Environments.CountAsync(e => e.Id == environmentId)).Should().Be(0);
    }

    private static async Task<(Project Project, Environment Environment)> SeedPlacementAsync(Harbora.Data.HarboraDbContext db)
    {
        var workspace = new Workspace { Name = "acme", Slug = "acme" };
        var project = new Project { WorkspaceId = workspace.Id, Name = "shop", Slug = "shop" };
        var environment = new Environment
        {
            WorkspaceId = workspace.Id, ProjectId = project.Id,
            Name = "production", Slug = "production", IsDefault = true
        };
        db.Workspaces.Add(workspace);
        db.Projects.Add(project);
        db.Environments.Add(environment);
        await db.SaveChangesAsync();
        return (project, environment);
    }
}
