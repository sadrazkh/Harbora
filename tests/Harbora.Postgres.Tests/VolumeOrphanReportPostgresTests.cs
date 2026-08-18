using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Environment = Harbora.Domain.Projects.Environment;
using Project = Harbora.Domain.Projects.Project;

namespace Harbora.Postgres.Tests;

/// <summary>
/// HARBORA-0033's orphan report run against real PostgreSQL rather than EF InMemory. Only the
/// "nothing found" shape is exercisable here, unlike <c>VolumeOrphanReportTests</c>: real Postgres
/// enforces the same foreign keys the report's own remarks describe (<c>Volume.AppId</c> cascades,
/// <c>App.EnvironmentId</c> is Restrict), so the inconsistent rows those InMemory tests seed directly
/// cannot exist here at all. What this proves instead is what InMemory cannot: that the query itself —
/// <c>IgnoreQueryFilters()</c>, the dictionary/hash-set post-processing over real Npgsql projections —
/// runs against the schema the migrations actually produce.
/// </summary>
[Collection(PostgresLane.Collection)]
public sealed class VolumeOrphanReportPostgresTests(PostgresLane lane)
{
    [PostgresFact]
    public async Task A_freshly_migrated_empty_database_reports_zero_on_both_questions()
    {
        var connectionString = await lane.HeadSchemaAsync();
        await using var db = PostgresLane.Open(connectionString);

        var report = await VolumeOrphanReport.BuildAsync(db);

        report.VolumesWithNoApp.Should().BeEmpty();
        report.VolumesWithNoEnvironment.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task A_volume_whose_app_and_environment_both_exist_is_not_reported_over_real_postgres()
    {
        var connectionString = await lane.FreshlyMigratedAsync("volume-orphan-placed");

        await using (var seed = PostgresLane.Open(connectionString))
        {
            var workspace = new Workspace { Name = "acme", Slug = "acme" };
            var project = new Project { WorkspaceId = workspace.Id, Name = "blog", Slug = "blog" };
            var environment = new Environment
            { WorkspaceId = workspace.Id, ProjectId = project.Id, Name = "production", Slug = "production", IsDefault = true };
            var app = new App
            { WorkspaceId = workspace.Id, ServerId = Guid.CreateVersion7(), Name = "web", Slug = "web", EnvironmentId = environment.Id };
            seed.Workspaces.Add(workspace);
            seed.Projects.Add(project);
            seed.Environments.Add(environment);
            seed.Apps.Add(app);
            seed.Volumes.Add(new Volume { AppId = app.Id, Name = "web-data", MountPath = "/data" });
            await seed.SaveChangesAsync();
        }

        await using var db = PostgresLane.Open(connectionString);
        var report = await VolumeOrphanReport.BuildAsync(db);

        report.VolumesWithNoApp.Should().BeEmpty();
        report.VolumesWithNoEnvironment.Should().BeEmpty();
        report.TotalVolumeCount.Should().Be(1);
    }

    [PostgresFact]
    public async Task Building_the_report_changes_no_row_over_real_postgres()
    {
        var connectionString = await lane.FreshlyMigratedAsync("volume-orphan-write-guard");

        Guid volumeId;
        await using (var seed = PostgresLane.Open(connectionString))
        {
            var workspace = new Workspace { Name = "acme", Slug = "acme" };
            var project = new Project { WorkspaceId = workspace.Id, Name = "blog", Slug = "blog" };
            var environment = new Environment
            { WorkspaceId = workspace.Id, ProjectId = project.Id, Name = "production", Slug = "production", IsDefault = true };
            var app = new App
            { WorkspaceId = workspace.Id, ServerId = Guid.CreateVersion7(), Name = "web", Slug = "web", EnvironmentId = environment.Id };
            var volume = new Volume { AppId = app.Id, Name = "web-data", MountPath = "/data", Protected = true };
            seed.Workspaces.Add(workspace);
            seed.Projects.Add(project);
            seed.Environments.Add(environment);
            seed.Apps.Add(app);
            seed.Volumes.Add(volume);
            await seed.SaveChangesAsync();
            volumeId = volume.Id;
        }

        await using (var runner = PostgresLane.Open(connectionString))
            await VolumeOrphanReport.BuildAsync(runner);

        await using var verify = PostgresLane.Open(connectionString);
        var volumeAfter = await verify.Volumes.IgnoreQueryFilters().SingleAsync(v => v.Id == volumeId);
        volumeAfter.Protected.Should().BeTrue("the report must not have touched it");
    }
}
