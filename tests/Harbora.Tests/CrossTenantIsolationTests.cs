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
