using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Projects;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Environment = Harbora.Domain.Projects.Environment;
using Project = Harbora.Domain.Projects.Project;

namespace Harbora.Postgres.Tests;

/// <summary>
/// P1's report run against real PostgreSQL rather than EF InMemory — including its own first check of
/// the 2026-07-30 backfill migration's effect, which no test in this lane had exercised before.
///
/// <para>
/// <see cref="EnvironmentPlacementReportPostgresTests.A_freshly_migrated_empty_database_reports_zero_across_every_question"/>
/// runs the report against a database that carries every migration up to head, including the one that
/// backfills <c>EnvironmentId</c> (<c>20260730220251_ProjectsAndEnvironments.cs</c>). An empty
/// database proves nothing about the backfill's SQL directly — there is nothing for it to have
/// missed — but it does prove the report's own queries translate and run against the schema that SQL
/// produced, which InMemory cannot check: <c>IgnoreQueryFilters()</c> composing correctly, the
/// required <c>Environment → Project</c> join, and the dictionary/hash-set post-processing over rows
/// that came back from real Npgsql projections rather than LINQ-to-Objects throughout.
/// </para>
/// </summary>
[Collection(PostgresLane.Collection)]
public sealed class EnvironmentPlacementReportPostgresTests(PostgresLane lane)
{
    [PostgresFact]
    public async Task A_freshly_migrated_empty_database_reports_zero_across_every_question()
    {
        // Shared and read-only for the whole assembly — safe to reuse because this fact writes
        // nothing, which is also half of what it is checking.
        var connectionString = await lane.HeadSchemaAsync();
        await using var db = PostgresLane.Open(connectionString);

        var report = await EnvironmentPlacementReport.BuildAsync(db);

        report.UnplacedWorkloadCount.Should().Be(0);
        report.EmptyEnvironments.Should().BeEmpty();
        report.WorkspacesWithWorkloadsButNoProject.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task A_workload_detached_after_the_backfill_is_named_by_the_report_over_real_postgres()
    {
        // Stands in for the shape the spec says is the only way a NULL exists in production today: a
        // row placed by the 2026-07-30 backfill and then detached by DeleteBehavior.SetNull on an
        // environment delete. Written directly rather than by deleting an environment, because the
        // point of this fact is the report's read, not the delete path that produces the row.
        var connectionString = await lane.FreshlyMigratedAsync("environment-report-unplaced");

        Guid workspaceId;
        await using (var seed = PostgresLane.Open(connectionString))
        {
            var workspace = new Workspace { Name = "acme", Slug = "acme" };
            seed.Workspaces.Add(workspace);
            seed.Apps.Add(new App
            {
                WorkspaceId = workspace.Id, ServerId = Guid.CreateVersion7(),
                Name = "orphaned-worker", Slug = "orphaned-worker", EnvironmentId = null
            });
            await seed.SaveChangesAsync();
            workspaceId = workspace.Id;
        }

        await using var db = PostgresLane.Open(connectionString);
        var report = await EnvironmentPlacementReport.BuildAsync(db);

        report.UnplacedApps.Should()
            .ContainSingle(a => a.Name == "orphaned-worker" && a.WorkspaceId == workspaceId);
    }

    [PostgresFact]
    public async Task Building_the_report_changes_no_row_over_real_postgres()
    {
        var connectionString = await lane.FreshlyMigratedAsync("environment-report-write-guard");

        Guid appId, environmentId;
        await using (var seed = PostgresLane.Open(connectionString))
        {
            var workspace = new Workspace { Name = "acme", Slug = "acme" };
            var project = new Project { WorkspaceId = workspace.Id, Name = "blog", Slug = "blog" };
            var environment = new Environment
            {
                WorkspaceId = workspace.Id, ProjectId = project.Id,
                Name = "production", Slug = "production", IsDefault = true
            };
            var app = new App
            {
                WorkspaceId = workspace.Id, ServerId = Guid.CreateVersion7(),
                Name = "web", Slug = "web", EnvironmentId = environment.Id
            };
            seed.Workspaces.Add(workspace);
            seed.Projects.Add(project);
            seed.Environments.Add(environment);
            seed.Apps.Add(app);
            await seed.SaveChangesAsync();
            appId = app.Id;
            environmentId = environment.Id;
        }

        await using (var runner = PostgresLane.Open(connectionString))
            await EnvironmentPlacementReport.BuildAsync(runner);

        await using var verify = PostgresLane.Open(connectionString);
        var appAfter = await verify.Apps.IgnoreQueryFilters().SingleAsync(a => a.Id == appId);
        appAfter.EnvironmentId.Should().Be(environmentId, "the report must not have touched it");
    }
}
