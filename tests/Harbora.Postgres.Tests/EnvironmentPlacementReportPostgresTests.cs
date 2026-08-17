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
/// backfills <c>EnvironmentId</c> (<c>20260730220251_ProjectsAndEnvironments.cs</c>) and the one that
/// makes it required (P2, 2026-08-17 app-environment-management design). An empty database proves
/// nothing about the backfill's SQL directly — there is nothing for it to have missed — but it does
/// prove the report's own queries translate and run against the schema that SQL produced, which
/// InMemory cannot check: <c>IgnoreQueryFilters()</c> composing correctly, the required
/// <c>Environment → Project</c> join, and the dictionary/hash-set post-processing over rows that came
/// back from real Npgsql projections rather than LINQ-to-Objects throughout.
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
    public async Task A_placed_workload_is_not_reported_as_unplaced_over_real_postgres()
    {
        // The test that used to live here wrote a row with EnvironmentId = null to stand in for the
        // shape the spec said was the only way a NULL could exist in production: a row placed by the
        // 2026-07-30 backfill and then detached by DeleteBehavior.SetNull on an environment delete.
        // P2 (2026-08-17 app-environment-management design) made EnvironmentId a required, RESTRICT
        // foreign key, so that row can no longer exist — EnvironmentColumnPostgresTests proves the
        // constraint that makes it impossible. What is left to prove here is that Q1 reports zero
        // for a database that actually has a workload in it, not only for an empty one.
        var connectionString = await lane.FreshlyMigratedAsync("environment-report-placed");

        Guid workspaceId;
        await using (var seed = PostgresLane.Open(connectionString))
        {
            var workspace = new Workspace { Name = "acme", Slug = "acme" };
            var project = new Project { WorkspaceId = workspace.Id, Name = "blog", Slug = "blog" };
            var environment = new Environment
            {
                WorkspaceId = workspace.Id, ProjectId = project.Id,
                Name = "production", Slug = "production", IsDefault = true
            };
            seed.Workspaces.Add(workspace);
            seed.Projects.Add(project);
            seed.Environments.Add(environment);
            seed.Apps.Add(new App
            {
                WorkspaceId = workspace.Id, ServerId = Guid.CreateVersion7(), EnvironmentId = environment.Id,
                Name = "web", Slug = "web"
            });
            await seed.SaveChangesAsync();
            workspaceId = workspace.Id;
        }

        await using var db = PostgresLane.Open(connectionString);
        var report = await EnvironmentPlacementReport.BuildAsync(db);

        report.UnplacedApps.Should().BeEmpty();
        report.UnplacedWorkloadCount.Should().Be(0);
        report.WorkspacesWithWorkloadsButNoProject.Where(w => w.WorkspaceId == workspaceId).Should().BeEmpty();
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
