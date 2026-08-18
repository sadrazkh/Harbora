using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Environment = Harbora.Domain.Projects.Environment;
using Project = Harbora.Domain.Projects.Project;

namespace Harbora.Tests;

/// <summary>
/// HARBORA-0033's orphan report. Modelled on <c>EnvironmentPlacementReportTests</c>: both of the
/// database-only questions below are enforced by the schema in production (<c>Volume.AppId</c>
/// cascades with its <c>App</c>; <c>App.EnvironmentId</c> is a required, Restrict-on-delete foreign
/// key), so InMemory's lack of FK enforcement is exactly what lets these tests seed the inconsistent
/// state a real Postgres install would refuse to ever hold — proving the query itself finds the row
/// rather than only reciting that the constraint exists.
/// </summary>
public class VolumeOrphanReportTests
{
    private static HarboraDbContext NewDb() => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("volume-orphan-" + Guid.NewGuid()).Options);

    private static Workspace Seed(HarboraDbContext db, string slug = "acme")
    {
        var workspace = new Workspace { Name = slug, Slug = slug };
        db.Workspaces.Add(workspace);
        return workspace;
    }

    private static (Project Project, Environment Environment) SeedPlacement(HarboraDbContext db, Workspace workspace)
    {
        var project = new Project { WorkspaceId = workspace.Id, Name = "blog", Slug = "blog" };
        var environment = new Environment
        { WorkspaceId = workspace.Id, ProjectId = project.Id, Name = "Production", Slug = "production", IsDefault = true };
        db.Projects.Add(project);
        db.Environments.Add(environment);
        return (project, environment);
    }

    // ---- a clean database ----

    [Fact]
    public async Task A_clean_database_reports_zero_on_both_questions_explicitly()
    {
        await using var db = NewDb();
        var workspace = Seed(db);
        var (_, environment) = SeedPlacement(db, workspace);
        var app = new App
        { WorkspaceId = workspace.Id, ServerId = Guid.CreateVersion7(), Name = "web", Slug = "web", EnvironmentId = environment.Id };
        db.Apps.Add(app);
        db.Volumes.Add(new Volume { AppId = app.Id, Name = "web-data", MountPath = "/data" });
        await db.SaveChangesAsync();

        var report = await VolumeOrphanReport.BuildAsync(db);

        report.VolumesWithNoApp.Should().BeEmpty();
        report.VolumesWithNoEnvironment.Should().BeEmpty();
        report.TotalVolumeCount.Should().Be(1);
    }

    [Fact]
    public async Task An_empty_database_reports_zero_rather_than_throwing()
    {
        // The point is not that the lists are empty — an unrun report also looks like that — it is
        // that BuildAsync says so rather than throwing or returning null.
        await using var db = NewDb();

        var report = await VolumeOrphanReport.BuildAsync(db);

        report.VolumesWithNoApp.Should().BeEmpty();
        report.VolumesWithNoEnvironment.Should().BeEmpty();
        report.TotalVolumeCount.Should().Be(0);
    }

    // ---- question 1: a Volume row with no owning App ----

    [Fact]
    public async Task A_volume_pointing_at_an_app_id_that_does_not_exist_is_found()
    {
        // Cannot happen through the platform's own write paths — Volume.AppId cascades with its App —
        // but the report checks the database, not the application code, so it is seeded directly.
        await using var db = NewDb();
        var ghostAppId = Guid.NewGuid();
        db.Volumes.Add(new Volume { AppId = ghostAppId, Name = "ghost-data", MountPath = "/data" });
        await db.SaveChangesAsync();

        var report = await VolumeOrphanReport.BuildAsync(db);

        report.VolumesWithNoApp.Should().ContainSingle(v => v.AppId == ghostAppId && v.MountPath == "/data");
        report.VolumesWithNoEnvironment.Should().BeEmpty();
    }

    [Fact]
    public async Task A_protected_orphaned_volume_is_still_found_and_named_as_protected()
    {
        // The report is read-only and does not act on Protected — it only has to keep naming it, the
        // same way it names the mount path, so an operator triaging this list knows which rows are
        // safe to just delete versus which need the flag turned off first.
        await using var db = NewDb();
        db.Volumes.Add(new Volume
        { AppId = Guid.NewGuid(), Name = "important-data", MountPath = "/data", Protected = true });
        await db.SaveChangesAsync();

        var report = await VolumeOrphanReport.BuildAsync(db);

        report.VolumesWithNoApp.Should().ContainSingle().Which.Protected.Should().BeTrue();
    }

    // ---- question 2: an App exists, but its Environment is gone ----

    [Fact]
    public async Task A_volume_whose_app_points_at_a_missing_environment_is_found()
    {
        await using var db = NewDb();
        var workspace = Seed(db);
        var missingEnvironmentId = Guid.NewGuid(); // never inserted
        var app = new App
        {
            WorkspaceId = workspace.Id, ServerId = Guid.CreateVersion7(), Name = "orphaned-app", Slug = "orphaned-app",
            EnvironmentId = missingEnvironmentId
        };
        db.Apps.Add(app);
        db.Volumes.Add(new Volume { AppId = app.Id, Name = "orphaned-app-data", MountPath = "/data" });
        await db.SaveChangesAsync();

        var report = await VolumeOrphanReport.BuildAsync(db);

        report.VolumesWithNoApp.Should().BeEmpty("the app row itself is still there");
        report.VolumesWithNoEnvironment.Should().ContainSingle(v =>
            v.AppName == "orphaned-app" && v.WorkspaceSlug == "acme" && v.MountPath == "/data");
    }

    // ---- rendering ----

    [Fact]
    public void The_rendered_report_names_an_orphan_rather_than_only_counting_it()
    {
        var orphan = new OrphanedVolume(Guid.NewGuid(), "ghost-data", "/data/uploads", false, Guid.NewGuid(), null, null, null);
        var report = new VolumeOrphanReportResult([orphan], [], TotalVolumeCount: 1, DiskCheckPerformed: false);

        var text = VolumeOrphanReport.Render(report);

        text.Should().Contain("/data/uploads");
        text.Should().Contain(orphan.Id.ToString());
    }

    [Fact]
    public void The_rendered_report_says_zero_plainly_for_a_clean_database()
    {
        var clean = new VolumeOrphanReportResult([], [], TotalVolumeCount: 4, DiskCheckPerformed: false);

        var text = VolumeOrphanReport.Render(clean);

        text.Should().Contain("0 of 4");
        text.Should().NotContain("ghost");
    }

    /// <summary>
    /// The one section this build can never answer honestly as zero — see this class's own remarks
    /// on why "volumes on disk" needs a live Docker connection this report does not have.
    /// </summary>
    [Fact]
    public void Section_three_says_not_checked_rather_than_printing_a_reassuring_zero()
    {
        var clean = new VolumeOrphanReportResult([], [], TotalVolumeCount: 4, DiskCheckPerformed: false);

        var text = VolumeOrphanReport.Render(clean);
        var sectionThree = text[text.IndexOf("3)", StringComparison.Ordinal)..];

        sectionThree.Should().Contain("not checked");
        sectionThree.Should().NotContain(": 0",
            "a count implies somebody counted, and nothing here queried a Docker daemon");
        sectionThree.Should().Contain("live connection",
            "an operator reading this must be told WHY, not just that it says 'not checked'");
    }

    // ---- the rule that defines the whole report: it writes nothing ----

    [Fact]
    public async Task Building_the_report_leaves_every_row_exactly_as_it_was()
    {
        var dbName = "volume-orphan-write-guard-" + Guid.NewGuid();
        HarboraDbContext Open() => new(new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(dbName).Options);

        Guid appId, volumeId;
        await using (var seed = Open())
        {
            var workspace = Seed(seed);
            var (_, environment) = SeedPlacement(seed, workspace);
            var app = new App
            { WorkspaceId = workspace.Id, ServerId = Guid.CreateVersion7(), Name = "web", Slug = "web", EnvironmentId = environment.Id };
            var volume = new Volume { AppId = app.Id, Name = "web-data", MountPath = "/data", Protected = true };
            seed.Apps.Add(app);
            seed.Volumes.Add(volume);
            await seed.SaveChangesAsync();
            appId = app.Id;
            volumeId = volume.Id;
        }

        await using (var runner = Open())
            await VolumeOrphanReport.BuildAsync(runner);

        await using var verify = Open();
        var volumeAfter = await verify.Volumes.IgnoreQueryFilters().SingleAsync(v => v.Id == volumeId);
        volumeAfter.AppId.Should().Be(appId, "the report must not have touched it");
        volumeAfter.Protected.Should().BeTrue("nor changed the flag it only reads");
        (await verify.Volumes.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        (await verify.Apps.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }
}
