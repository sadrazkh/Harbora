using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Services;
using Harbora.Infrastructure.Storage;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 5.1 (per-app and per-service grants, HARBORA-0035): "deleting an app or service that grants
/// reference must clean them up rather than leaving rows pointing at nothing." <c>ProjectGrant</c>
/// carries no foreign key of its own — unlike <c>DatabaseAccessGrant</c>'s FK onto
/// <c>ManagedServiceId</c> — so there is nothing for the database to cascade on its behalf; the two
/// delete paths themselves have to do it, which is what this proves against the real
/// <see cref="AppOperationsService"/>/<see cref="ManagedServiceEngine"/> rather than a fake standing
/// in for either.
/// </summary>
public sealed class AppScopeGrantCleanupTests
{
    private static HarboraDbContext NewDb() => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("grant-cleanup-" + Guid.NewGuid()).Options);

    private static (Workspace Workspace, Harbora.Domain.Servers.Server Server,
        Harbora.Domain.Projects.Project Project, Harbora.Domain.Projects.Environment Environment) SeedPlacement(
        HarboraDbContext db)
    {
        var workspace = new Workspace { Name = "acme", Slug = "acme" };
        var server = new Harbora.Domain.Servers.Server { Name = "local", Hostname = "localhost", IsLocal = true };
        var project = new Harbora.Domain.Projects.Project { WorkspaceId = workspace.Id, Name = "shop", Slug = "shop" };
        var environment = new Harbora.Domain.Projects.Environment
        {
            WorkspaceId = workspace.Id, ProjectId = project.Id, Name = "Production", Slug = "production", IsDefault = true
        };
        db.Workspaces.Add(workspace);
        db.Servers.Add(server);
        db.Projects.Add(project);
        db.Environments.Add(environment);
        return (workspace, server, project, environment);
    }

    [Fact]
    public async Task Deleting_an_app_removes_grants_that_named_it_but_leaves_the_users_other_grants()
    {
        await using var db = NewDb();
        var (workspace, server, project, environment) = SeedPlacement(db);
        var app = new App
        {
            WorkspaceId = workspace.Id, ServerId = server.Id, EnvironmentId = environment.Id,
            Name = "api", Slug = "api", SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/api:1.0"
        };
        db.Apps.Add(app);

        var contractor = Guid.CreateVersion7();
        var appGrant = new ProjectGrant
        {
            WorkspaceId = workspace.Id, UserId = contractor, ProjectId = project.Id, AppId = app.Id,
            Role = SystemRole.Member
        };
        var otherGrant = new ProjectGrant
        {
            WorkspaceId = workspace.Id, UserId = contractor, ProjectId = project.Id,
            Role = SystemRole.Viewer
        };
        db.ProjectGrants.AddRange(appGrant, otherGrant);
        await db.SaveChangesAsync();

        var docker = new FakeDockerEngine();
        var service = new AppOperationsService(
            db,
            new FakeServerEngineFactory(docker),
            new RecordingProxyEngine(() => db.Routes.IgnoreQueryFilters().AsNoTracking().ToList()),
            new BillingGate(db, Options.Create(new BillingOptions())),
            new HostPortAllocator(db, TestIngress.Registry(), NullLogger<HostPortAllocator>.Instance),
            NullLogger<AppOperationsService>.Instance);

        await service.DeleteAsync(app.Id, removeVolumes: false, CancellationToken.None);

        (await db.ProjectGrants.IgnoreQueryFilters().AnyAsync(g => g.Id == appGrant.Id)).Should().BeFalse(
            "a grant naming a deleted app is a permission that grants nothing and a row nobody can ever explain");
        (await db.ProjectGrants.IgnoreQueryFilters().AnyAsync(g => g.Id == otherGrant.Id)).Should().BeTrue(
            "the same contractor's whole-project grant has nothing to do with this app and must survive");
    }

    /// <summary>
    /// Both tenancy directions in one pass, the CONSTRAINTS.md rule for any cleanup that reads with
    /// <c>IgnoreQueryFilters()</c>: the workspace whose app was deleted loses that grant, and a
    /// second workspace's own grant — pointing at its own, still-alive app — is untouched. The
    /// cleanup query filters by <c>AppId</c> alone, which is already tenant-safe by construction
    /// (two different apps can never share a Guid), but the explicit
    /// <c>WorkspaceId == app.WorkspaceId</c> conjunction is proven here rather than assumed.
    /// </summary>
    [Fact]
    public async Task Deleting_an_app_in_one_workspace_does_not_touch_a_grant_in_another_workspace()
    {
        await using var db = NewDb();
        var (workspaceA, serverA, projectA, environmentA) = SeedPlacement(db);
        var workspaceB = new Workspace { Name = "widgets", Slug = "widgets" };
        var serverB = new Harbora.Domain.Servers.Server { Name = "local-b", Hostname = "b.localhost", IsLocal = true };
        var projectB = new Harbora.Domain.Projects.Project { WorkspaceId = workspaceB.Id, Name = "widgets", Slug = "widgets" };
        var environmentB = new Harbora.Domain.Projects.Environment
        {
            WorkspaceId = workspaceB.Id, ProjectId = projectB.Id, Name = "Production", Slug = "production", IsDefault = true
        };
        db.Workspaces.Add(workspaceB);
        db.Servers.Add(serverB);
        db.Projects.Add(projectB);
        db.Environments.Add(environmentB);

        var appA = new App
        {
            WorkspaceId = workspaceA.Id, ServerId = serverA.Id, EnvironmentId = environmentA.Id,
            Name = "api-a", Slug = "api-a", SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/a:1.0"
        };
        var appB = new App
        {
            WorkspaceId = workspaceB.Id, ServerId = serverB.Id, EnvironmentId = environmentB.Id,
            Name = "api-b", Slug = "api-b", SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/b:1.0"
        };
        db.Apps.AddRange(appA, appB);

        var grantA = new ProjectGrant
        {
            WorkspaceId = workspaceA.Id, UserId = Guid.CreateVersion7(), ProjectId = projectA.Id, AppId = appA.Id,
            Role = SystemRole.Member
        };
        var grantB = new ProjectGrant
        {
            WorkspaceId = workspaceB.Id, UserId = Guid.CreateVersion7(), ProjectId = projectB.Id, AppId = appB.Id,
            Role = SystemRole.Member
        };
        db.ProjectGrants.AddRange(grantA, grantB);
        await db.SaveChangesAsync();

        var docker = new FakeDockerEngine();
        var service = new AppOperationsService(
            db,
            new FakeServerEngineFactory(docker),
            new RecordingProxyEngine(() => db.Routes.IgnoreQueryFilters().AsNoTracking().ToList()),
            new BillingGate(db, Options.Create(new BillingOptions())),
            new HostPortAllocator(db, TestIngress.Registry(), NullLogger<HostPortAllocator>.Instance),
            NullLogger<AppOperationsService>.Instance);

        await service.DeleteAsync(appA.Id, removeVolumes: false, CancellationToken.None);

        (await db.ProjectGrants.IgnoreQueryFilters().AnyAsync(g => g.Id == grantA.Id)).Should().BeFalse(
            "the right tenant: workspace A's own app was deleted, so its own grant must go with it");
        (await db.ProjectGrants.IgnoreQueryFilters().AnyAsync(g => g.Id == grantB.Id)).Should().BeTrue(
            "the wrong tenant: workspace B's app is untouched, so its grant must survive");
        (await db.Apps.IgnoreQueryFilters().AnyAsync(a => a.Id == appB.Id)).Should().BeTrue(
            "workspace B's app itself was never asked about");
    }

    [Fact]
    public async Task Deleting_a_managed_service_removes_grants_that_named_it_but_leaves_the_users_other_grants()
    {
        await using var db = NewDb();
        var (workspace, server, project, environment) = SeedPlacement(db);
        var svc = new ManagedService
        {
            WorkspaceId = workspace.Id, ServerId = server.Id, EnvironmentId = environment.Id,
            Name = "cache", Type = ManagedServiceType.Redis, ContainerName = "harbora-svc-cache",
            VolumeName = "harbora-svc-cache-data"
        };
        db.ManagedServices.Add(svc);

        var contractor = Guid.CreateVersion7();
        var serviceGrant = new ProjectGrant
        {
            WorkspaceId = workspace.Id, UserId = contractor, ProjectId = project.Id, ServiceId = svc.Id,
            Role = SystemRole.Member
        };
        var otherGrant = new ProjectGrant
        {
            WorkspaceId = workspace.Id, UserId = contractor, ProjectId = project.Id,
            Role = SystemRole.Viewer
        };
        db.ProjectGrants.AddRange(serviceGrant, otherGrant);
        await db.SaveChangesAsync();

        var docker = new FakeDockerEngine();
        var engine = new ManagedServiceEngine(
            db, new SingleEngineFactory(docker), new PassthroughProtector(), new NoopJobQueue(),
            new BillingGate(db, Options.Create(new BillingOptions { Enabled = false })),
            Options.Create(new HarboraRuntimeOptions()),
            new FixedClock(DateTimeOffset.UnixEpoch),
            NullLogger<ManagedServiceEngine>.Instance);

        await engine.RemoveAsync(svc.Id, deleteData: false, CancellationToken.None);

        (await db.ProjectGrants.IgnoreQueryFilters().AnyAsync(g => g.Id == serviceGrant.Id)).Should().BeFalse(
            "a grant naming a deleted service is a permission that grants nothing");
        (await db.ProjectGrants.IgnoreQueryFilters().AnyAsync(g => g.Id == otherGrant.Id)).Should().BeTrue(
            "the same contractor's whole-project grant has nothing to do with this service and must survive");
    }
}
