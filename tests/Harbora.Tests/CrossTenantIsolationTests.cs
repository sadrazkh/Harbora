using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Networking;
using Harbora.Infrastructure.Deployments;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Cross-tenant isolation (owed from P13). RBAC answers "may this role do X"; it says nothing about
/// "is X theirs". Every lookup that takes an id from the URL must be scoped to the caller's
/// workspace, or a Member of tenant A can operate on tenant B's resources simply by guessing an id.
///
/// These tests pin the scoping predicates the controllers use. Where a query is written without a
/// workspace filter, that is a finding — not a passing test.
/// </summary>
public class CrossTenantIsolationTests
{
    private static HarboraDbContext NewDb() => new(
        new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("tenant-" + Guid.NewGuid()).Options);

    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    private static async Task<(Guid AppA, Guid AppB)> SeedTwoTenantsAsync(HarboraDbContext db)
    {
        var appA = new App { Id = Guid.NewGuid(), WorkspaceId = TenantA, Name = "A", Slug = "a" };
        var appB = new App { Id = Guid.NewGuid(), WorkspaceId = TenantB, Name = "B", Slug = "b" };
        db.Apps.AddRange(appA, appB);
        await db.SaveChangesAsync();
        return (appA.Id, appB.Id);
    }

    // ---- apps ----

    [Fact]
    public async Task An_app_lookup_scoped_to_a_workspace_cannot_reach_another_tenants_app()
    {
        using var db = NewDb();
        var (_, appB) = await SeedTwoTenantsAsync(db);

        // The predicate every AppsController action uses.
        var found = await db.Apps.FirstOrDefaultAsync(a => a.Id == appB && a.WorkspaceId == TenantA);

        found.Should().BeNull("tenant A must not resolve tenant B's app even with the exact id");
    }

    [Fact]
    public async Task Listing_apps_returns_only_the_callers_workspace()
    {
        using var db = NewDb();
        await SeedTwoTenantsAsync(db);

        var visible = await db.Apps.Where(a => a.WorkspaceId == TenantA).ToListAsync();

        visible.Should().ContainSingle().Which.Slug.Should().Be("a");
    }

    // ---- backups: the highest-consequence cross-tenant target ----

    [Fact]
    public async Task A_backup_lookup_cannot_reach_another_tenants_backup()
    {
        using var db = NewDb();
        var backupB = new Backup
        {
            Id = Guid.NewGuid(), WorkspaceId = TenantB, Type = BackupType.Volume,
            Status = BackupStatus.Completed, TargetRef = "b-data"
        };
        db.Backups.Add(backupB);
        await db.SaveChangesAsync();

        // BackupsController.OwnsAsync — the guard in front of download AND restore.
        var owns = await db.Backups.AnyAsync(b => b.Id == backupB.Id && b.WorkspaceId == TenantA);

        owns.Should().BeFalse(
            "restoring another tenant's backup would overwrite their data; downloading it would exfiltrate it");
    }

    // ---- rollback ----

    [Fact]
    public async Task The_rollback_planner_refuses_a_deployment_from_another_app()
    {
        using var h = new PipelineHarness();
        var mine = h.SeedSucceededDeployment(1, "harbora/blog:build-1");

        // Target belongs to this app, but the caller names a different app.
        var plan = await new RollbackPlanner(h.Db, new SingleEngine(h.Docker))
            .PrepareAsync(Guid.NewGuid(), mine.Id, default);

        plan.CanRollback.Should().BeFalse();
    }

    [Fact]
    public async Task A_deployment_lookup_is_scoped_through_its_app()
    {
        using var db = NewDb();
        var (_, appB) = await SeedTwoTenantsAsync(db);
        var deploymentB = new Deployment
        { Id = Guid.NewGuid(), AppId = appB, Number = 1, Status = DeploymentStatus.Succeeded };
        db.Deployments.Add(deploymentB);
        await db.SaveChangesAsync();

        // Deployments have no WorkspaceId of their own — they must be scoped via App.
        var found = await db.Deployments
            .Where(d => d.Id == deploymentB.Id && d.App!.WorkspaceId == TenantA)
            .FirstOrDefaultAsync();

        found.Should().BeNull();
    }

    // ---- routes + domains ----

    [Fact]
    public async Task A_route_lookup_cannot_reach_another_tenants_route()
    {
        using var db = NewDb();
        var (_, appB) = await SeedTwoTenantsAsync(db);
        var routeB = new Route { Id = Guid.NewGuid(), WorkspaceId = TenantB, AppId = appB, Host = "b.example.com" };
        db.Routes.Add(routeB);
        await db.SaveChangesAsync();

        var found = await db.Routes.FirstOrDefaultAsync(r => r.Id == routeB.Id && r.WorkspaceId == TenantA);

        found.Should().BeNull();
    }

    [Fact]
    public async Task Proxy_config_is_built_from_one_workspaces_routes_only()
    {
        // The pipeline re-applies the whole workspace's routes on every deploy. If that query were
        // unscoped, one tenant's deploy would rewrite the routing table for everyone.
        using var db = NewDb();
        var (appA, appB) = await SeedTwoTenantsAsync(db);
        db.Routes.AddRange(
            new Route { Id = Guid.NewGuid(), WorkspaceId = TenantA, AppId = appA, Host = "a.example.com", IsEnabled = true },
            new Route { Id = Guid.NewGuid(), WorkspaceId = TenantB, AppId = appB, Host = "b.example.com", IsEnabled = true });
        await db.SaveChangesAsync();

        var applied = await db.Routes.Where(r => r.WorkspaceId == TenantA && r.IsEnabled).ToListAsync();

        applied.Should().ContainSingle().Which.Host.Should().Be("a.example.com");
    }

    // ---- the deploy path itself ----

    [Fact]
    public async Task A_deploy_only_ever_touches_its_own_apps_containers()
    {
        // Container selection is by the harbora.app label. If it matched loosely, a deploy of "blog"
        // could retire containers belonging to "blog-staging" — potentially another tenant's app.
        using var h = new PipelineHarness();
        h.WithPreviousDeployment(number: 1);
        h.Docker.SeedContainer("harbora-other-1", "other-tenant-app");

        await h.RunAsync(h.QueueDeployment(number: 2));

        h.Docker.LiveContainerNames.Should().Contain("harbora-other-1",
            "another app's container must survive an unrelated deploy");
    }

    /// <summary>
    /// The defect 2026-08-15-unique-app-names-design fixes, and the sharper case
    /// <see cref="A_deploy_only_ever_touches_its_own_apps_containers"/> above does not cover: that test
    /// uses two different slugs, which the pre-fix code already got right by accident (the label
    /// existed but its value never matched). The real defect only showed when two workspaces picked
    /// the <em>same</em> slug — "api", "web", "app" — because <c>ContainersToRetire</c> matched the
    /// <c>harbora.app</c> label's value against the slug alone, and that label was only ever unique
    /// per workspace. A second, unrelated workspace with its own "api" shares this harness's database
    /// and docker host, exactly as two tenants share one node and one database in production.
    /// </summary>
    private static (App App, string ContainerName) AddStrangerWorkspaceApp(
        PipelineHarness harness, string slug, int number = 1)
    {
        var workspace = new Harbora.Domain.Identity.Workspace
        { Id = Guid.NewGuid(), Name = "Stranger", Slug = "stranger-" + Guid.NewGuid().ToString("N")[..8] };
        var project = new Harbora.Domain.Projects.Project
        { Id = Guid.NewGuid(), WorkspaceId = workspace.Id, Name = "Stranger", Slug = "stranger" };
        var environment = new Harbora.Domain.Projects.Environment
        {
            Id = Guid.NewGuid(), WorkspaceId = workspace.Id, ProjectId = project.Id,
            Name = "Production", Slug = "production", IsDefault = true
        };
        var app = new App
        {
            Id = Guid.NewGuid(), WorkspaceId = workspace.Id, ServerId = harness.Server.Id,
            EnvironmentId = environment.Id, Name = slug, Slug = slug,
            SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "nginx:1.27",
            ContainerPort = 8080, Status = AppStatus.Running
        };

        harness.Db.Workspaces.Add(workspace);
        harness.Db.Projects.Add(project);
        harness.Db.Environments.Add(environment);
        harness.Db.Apps.Add(app);
        harness.Db.SaveChanges();

        var containerName = DeploymentPlanning.ContainerName(workspace.Id, slug, number);
        harness.Docker.SeedContainer(containerName, slug, workspaceId: workspace.Id);
        return (app, containerName);
    }

    [Fact]
    public async Task Deploying_one_workspaces_app_leaves_another_workspaces_identically_slugged_container_running()
    {
        using var harness = new PipelineHarness();
        // The common name that makes this reachable in production — see the design doc.
        harness.App.Slug = "api";
        await harness.Db.SaveChangesAsync();

        var (strangerApp, strangerContainer) = AddStrangerWorkspaceApp(harness, "api");

        var deployment = harness.QueueDeployment();
        var result = await harness.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        harness.Docker.LiveContainerNames.Should().Contain(strangerContainer,
            "a deploy must never remove a container it does not own — this is the whole point of the " +
            "fix, in the design doc's own words");
        harness.Docker.OperationsOn(strangerContainer).Should().NotContain(nameof(FakeDockerEngine.RemoveContainerAsync));

        // And the stranger's app row is undisturbed too — this was never workspace A's to touch.
        var untouched = await harness.Db.Apps.FindAsync(strangerApp.Id);
        untouched!.WorkspaceId.Should().NotBe(harness.App.WorkspaceId);
    }

    /// <summary>
    /// The counterpart the isolation guarantee must not cost: this workspace's OWN previous "api"
    /// container is still retired on cutover, identically-slugged stranger notwithstanding. A fix that
    /// stopped all retirement near a slug collision would trade the cross-tenant leak for a same-tenant
    /// one — every redeploy leaking its own previous container.
    ///
    /// The first deployment runs through the real pipeline, rather than a raw seeded container, so its
    /// container carries the <c>harbora.workspace</c> label the fixed pipeline actually stamps —
    /// exactly what lets retirement tell it apart from the stranger's identically-slugged, identically
    /// unlabelled-to-A container even though neither container's slug is unique on this host.
    /// </summary>
    [Fact]
    public async Task A_workspaces_own_previous_container_is_still_retired_despite_a_strangers_same_slug()
    {
        using var harness = new PipelineHarness();
        harness.App.Slug = "api";
        await harness.Db.SaveChangesAsync();

        var first = harness.QueueDeployment(number: 1);
        await harness.RunAsync(first);
        var firstContainer = harness.ContainerFor(1);
        harness.Docker.LiveContainerNames.Should().Contain(firstContainer, "the first deploy must have started it");

        var (_, strangerContainer) = AddStrangerWorkspaceApp(harness, "api");

        var second = harness.QueueDeployment(number: 2);
        var result = await harness.RunAsync(second);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        harness.Docker.LiveContainerNames.Should().NotContain(firstContainer,
            "existing containers must still be retired — a fix that strands every container deployed " +
            "before it shipped trades one leak for another");
        harness.Docker.LiveContainerNames.Should().Contain(strangerContainer,
            "and the stranger's container, sitting right next to it under the same slug, must survive");
    }

    [Fact]
    public async Task Image_retention_never_prunes_another_apps_images()
    {
        using var h = new PipelineHarness().WithGitSource().WithDockerfile();
        h.Options.ImageRetentionCount = 1;
        h.Docker.SeedImage("harbora/blog:build-1", "harbora/other:build-1", "harbora/other:build-2");
        h.SeedSucceededDeployment(1, "harbora/blog:build-1");
        h.WithPreviousDeployment(number: 2, image: "harbora/blog:build-2");

        await h.RunAsync(h.QueueDeployment(number: 3));

        h.Docker.StoredImageTags.Should().Contain(["harbora/other:build-1", "harbora/other:build-2"]);
    }

    private sealed class SingleEngine(FakeDockerEngine engine) : Harbora.Application.Abstractions.IServerEngineFactory
    {
        public Harbora.Application.Abstractions.IDockerEngine Local => engine;
        public Task<Harbora.Application.Abstractions.IDockerEngine> ResolveAsync(Guid serverId, CancellationToken ct)
            => Task.FromResult<Harbora.Application.Abstractions.IDockerEngine>(engine);
    }
}
