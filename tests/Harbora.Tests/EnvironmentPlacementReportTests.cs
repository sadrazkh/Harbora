using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Identity;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Projects;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Environment = Harbora.Domain.Projects.Environment;
using Project = Harbora.Domain.Projects.Project;

namespace Harbora.Tests;

/// <summary>
/// P1 — the report nobody has run. Four questions against the live database, and the rule that
/// defines it: it writes nothing.
///
/// <para>
/// Q1 (null EnvironmentId) and Q3 (dual-attach) both settled to "always zero" once later
/// sub-projects landed: P2 (2026-08-17 app-environment-management design) made EnvironmentId a
/// required foreign key, so a null EnvironmentId can no longer exist in the schema at all, and P3
/// retired the dual attach that made Q3 answerable as anything but zero. Both fields stay in the
/// report rather than being deleted — a section that silently disappeared would read as "not
/// checked" to an operator who remembers it being here.
/// </para>
/// </summary>
public class EnvironmentPlacementReportTests
{
    private static HarboraDbContext NewDb() => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("environment-placement-" + Guid.NewGuid()).Options);

    private static Workspace Seed(HarboraDbContext db, string slug = "acme")
    {
        var workspace = new Workspace { Name = slug, Slug = slug };
        db.Workspaces.Add(workspace);
        return workspace;
    }

    private static (Project Project, Environment Environment) SeedPlacement(
        HarboraDbContext db, Workspace workspace, string projectSlug = "blog", string envSlug = "production")
    {
        var project = new Project { WorkspaceId = workspace.Id, Name = projectSlug, Slug = projectSlug };
        var environment = new Environment
        {
            WorkspaceId = workspace.Id, ProjectId = project.Id, Name = envSlug, Slug = envSlug, IsDefault = true
        };
        db.Projects.Add(project);
        db.Environments.Add(environment);
        return (project, environment);
    }

    // ---- question 1: null EnvironmentId ----

    /// <summary>
    /// Inverted by P2 (2026-08-17 app-environment-management design). EnvironmentId is a required
    /// foreign key now, so a null EnvironmentId cannot exist in the schema at all — there is no
    /// longer a C# value that expresses "unplaced". Q1 used to find and name these rows; the only
    /// correct answer now is that there are none to find, always, whatever else is seeded.
    /// </summary>
    [Fact]
    public async Task No_workload_can_be_unplaced_any_more_so_the_report_finds_none()
    {
        await using var db = NewDb();
        var workspace = Seed(db);
        var (_, environment) = SeedPlacement(db, workspace);
        db.Apps.Add(new App
        {
            WorkspaceId = workspace.Id, ServerId = Guid.CreateVersion7(),
            Name = "web", Slug = "web", EnvironmentId = environment.Id
        });
        db.ManagedServices.Add(new ManagedService
        {
            WorkspaceId = workspace.Id, ServerId = Guid.CreateVersion7(),
            Name = "pg", ContainerName = "pg", EnvironmentId = environment.Id
        });
        await db.SaveChangesAsync();

        var report = await EnvironmentPlacementReport.BuildAsync(db);

        report.UnplacedApps.Should().BeEmpty();
        report.UnplacedManagedServices.Should().BeEmpty();
        report.UnplacedWorkloadCount.Should().Be(0);
    }

    [Fact]
    public async Task A_clean_database_reports_zero_unplaced_workloads_explicitly()
    {
        // Nothing seeded at all. The point is not that the lists are empty — an unrun report also
        // looks like that — it is that BuildAsync says so rather than throwing or returning null.
        await using var db = NewDb();

        var report = await EnvironmentPlacementReport.BuildAsync(db);

        report.UnplacedApps.Should().BeEmpty();
        report.UnplacedManagedServices.Should().BeEmpty();
        report.UnplacedWorkloadCount.Should().Be(0);
    }

    // ---- question 2: environments with no workloads ----

    [Fact]
    public async Task An_environment_holding_no_app_or_service_is_reported_as_empty()
    {
        await using var db = NewDb();
        var workspace = Seed(db);
        var (_, environment) = SeedPlacement(db, workspace);
        await db.SaveChangesAsync();

        var report = await EnvironmentPlacementReport.BuildAsync(db);

        report.EmptyEnvironments.Should().ContainSingle(e => e.Id == environment.Id);
    }

    [Fact]
    public async Task An_environment_holding_an_app_is_not_reported_as_empty()
    {
        await using var db = NewDb();
        var workspace = Seed(db);
        var (_, environment) = SeedPlacement(db, workspace);
        db.Apps.Add(new App
        {
            WorkspaceId = workspace.Id, ServerId = Guid.CreateVersion7(),
            Name = "web", Slug = "web", EnvironmentId = environment.Id
        });
        await db.SaveChangesAsync();

        var report = await EnvironmentPlacementReport.BuildAsync(db);

        report.EmptyEnvironments.Should().BeEmpty();
    }

    [Fact]
    public async Task An_environment_holding_only_a_managed_service_is_not_reported_as_empty()
    {
        await using var db = NewDb();
        var workspace = Seed(db);
        var (_, environment) = SeedPlacement(db, workspace);
        db.ManagedServices.Add(new ManagedService
        {
            WorkspaceId = workspace.Id, ServerId = Guid.CreateVersion7(),
            Name = "pg", ContainerName = "pg", EnvironmentId = environment.Id
        });
        await db.SaveChangesAsync();

        var report = await EnvironmentPlacementReport.BuildAsync(db);

        report.EmptyEnvironments.Should().BeEmpty();
    }

    // ---- question 3: workloads that would attach to more than one network today ----

    /// <summary>
    /// Inverted by P3 (2026-08-17 app-environment-management design): a placed workload used to
    /// dual-attach unconditionally, because both production call sites hardcoded
    /// <c>keepWorkspaceNetwork: true</c>. P3 moved every one-off that reached a database on the
    /// workspace network onto the workload's own environment network and then deleted that
    /// parameter, so <c>NetworkPlan.For</c> now returns exactly one name — a placed workload no
    /// longer dual-attaches, and this count is zero regardless of placement.
    /// </summary>
    [Fact]
    public async Task A_workload_placed_in_an_environment_no_longer_counts_toward_the_dual_attach_total()
    {
        await using var db = NewDb();
        var workspace = Seed(db);
        var (_, environment) = SeedPlacement(db, workspace);
        db.Apps.Add(new App
        {
            WorkspaceId = workspace.Id, ServerId = Guid.CreateVersion7(),
            Name = "web", Slug = "web", EnvironmentId = environment.Id
        });
        await db.SaveChangesAsync();

        var report = await EnvironmentPlacementReport.BuildAsync(db);

        report.DualAttachWorkloadCount.Should().Be(0);
        report.TotalWorkloadCount.Should().Be(1);
    }

    // ---- question 4: a workspace with workloads but no project ----

    [Fact]
    public async Task A_workspace_with_an_app_but_no_project_is_flagged()
    {
        await using var db = NewDb();
        var workspace = Seed(db);
        db.Apps.Add(new App
        {
            WorkspaceId = workspace.Id, ServerId = Guid.CreateVersion7(),
            Name = "legacy", Slug = "legacy"
        });
        await db.SaveChangesAsync();

        var report = await EnvironmentPlacementReport.BuildAsync(db);

        report.WorkspacesWithWorkloadsButNoProject.Should().ContainSingle(w => w.WorkspaceId == workspace.Id);
    }

    [Fact]
    public async Task A_workspace_with_a_project_is_not_flagged_even_though_it_has_workloads()
    {
        await using var db = NewDb();
        var workspace = Seed(db);
        SeedPlacement(db, workspace);
        db.Apps.Add(new App
        {
            WorkspaceId = workspace.Id, ServerId = Guid.CreateVersion7(),
            Name = "legacy", Slug = "legacy"
        });
        await db.SaveChangesAsync();

        var report = await EnvironmentPlacementReport.BuildAsync(db);

        report.WorkspacesWithWorkloadsButNoProject.Should().BeEmpty();
    }

    [Fact]
    public async Task A_workspace_with_a_project_and_no_workloads_at_all_is_not_flagged()
    {
        // SetupController's own shape: the first workspace has neither a project nor a workload yet.
        // That is legal and must not be mistaken for the defect this question is looking for.
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var report = await EnvironmentPlacementReport.BuildAsync(db);

        report.WorkspacesWithWorkloadsButNoProject.Should().BeEmpty();
    }

    // ---- the rule that defines the whole sub-project: it writes nothing ----

    [Fact]
    public async Task Building_the_report_leaves_every_row_exactly_as_it_was()
    {
        var dbName = "environment-placement-write-guard-" + Guid.NewGuid();
        HarboraDbContext Open() => new(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase(dbName).Options);

        Guid appId, serviceId, environmentId;
        await using (var seed = Open())
        {
            var workspace = Seed(seed);
            var (_, environment) = SeedPlacement(seed, workspace);
            var app = new App
            {
                WorkspaceId = workspace.Id, ServerId = Guid.CreateVersion7(),
                Name = "web", Slug = "web", EnvironmentId = environment.Id
            };
            var service = new ManagedService
            {
                WorkspaceId = workspace.Id, ServerId = Guid.CreateVersion7(),
                Name = "pg", ContainerName = "pg", EnvironmentId = environment.Id
            };
            seed.Apps.Add(app);
            seed.ManagedServices.Add(service);
            await seed.SaveChangesAsync();
            appId = app.Id;
            serviceId = service.Id;
            environmentId = environment.Id;
        }

        // Proof by re-reading, not by reading the code: a fresh context over the same store, before
        // and after, must see byte-for-byte the same rows.
        await using (var runner = Open())
            await EnvironmentPlacementReport.BuildAsync(runner);

        await using var verify = Open();
        var appAfter = await verify.Apps.IgnoreQueryFilters().SingleAsync(a => a.Id == appId);
        var serviceAfter = await verify.ManagedServices.IgnoreQueryFilters().SingleAsync(s => s.Id == serviceId);

        appAfter.EnvironmentId.Should().Be(environmentId, "the report must not have touched it");
        serviceAfter.EnvironmentId.Should().Be(environmentId, "the report must not have touched it");
        (await verify.Apps.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        (await verify.ManagedServices.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        (await verify.Environments.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    // ---- rendering: a person has to be able to read it ----

    [Fact]
    public void The_rendered_report_names_the_unplaced_workload_rather_than_only_counting_it()
    {
        var workspaceId = Guid.NewGuid();
        var report = new EnvironmentPlacementReportResult(
            UnplacedApps: [new UnplacedWorkload(Guid.NewGuid(), "App", "orphaned-worker", workspaceId, "acme")],
            UnplacedManagedServices: [],
            EmptyEnvironments: [],
            TotalWorkloadCount: 1,
            DualAttachWorkloadCount: 0,
            WorkspacesWithWorkloadsButNoProject: []);

        var text = EnvironmentPlacementReport.Render(report);

        text.Should().Contain("orphaned-worker");
        text.Should().Contain("acme");
    }

    [Fact]
    public void The_rendered_report_says_zero_plainly_for_a_clean_database()
    {
        var clean = new EnvironmentPlacementReportResult(
            UnplacedApps: [], UnplacedManagedServices: [], EmptyEnvironments: [],
            TotalWorkloadCount: 3, DualAttachWorkloadCount: 3, WorkspacesWithWorkloadsButNoProject: []);

        var text = EnvironmentPlacementReport.Render(clean);

        // Not merely an empty section — the point of P1 is that a zero anyone reads is a zero that
        // ran, not a report that silently produced nothing.
        text.Should().Contain("0");
        text.Should().NotContain("orphaned");
    }

    /// <summary>
    /// Section 1 must not print a zero, because since P2 nothing asks the database that question —
    /// the column is a required foreign key, so the query was removed rather than answered.
    ///
    /// <para>
    /// A "0" there would be a claim nobody made. And the operator this report exists for runs it
    /// <em>before</em> a risky migration, quite possibly on a server where that migration has not
    /// applied — so a reassuring zero from a check that never ran is the precise failure the report
    /// was built to prevent, reappearing inside the report itself.
    /// </para>
    /// </summary>
    [Fact]
    public void Section_one_says_the_schema_enforces_it_rather_than_printing_a_reassuring_zero()
    {
        var clean = new EnvironmentPlacementReportResult(
            UnplacedApps: [], UnplacedManagedServices: [], EmptyEnvironments: [],
            TotalWorkloadCount: 3, DualAttachWorkloadCount: 3, WorkspacesWithWorkloadsButNoProject: []);

        var text = EnvironmentPlacementReport.Render(clean);

        var sectionOne = text[text.IndexOf("1)", StringComparison.Ordinal)..];
        sectionOne = sectionOne[..sectionOne.IndexOf("2)", StringComparison.Ordinal)];

        sectionOne.Should().Contain("enforced by the schema");
        sectionOne.Should().NotContain(": 0",
            "a count implies somebody counted, and since P2 nobody does");
        sectionOne.Should().Contain("not an answer",
            "the operator must be told this line is silent, not clean, if the migration has not applied");
    }
}
