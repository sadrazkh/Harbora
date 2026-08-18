using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Storage;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// HARBORA-0033's brake, exercised against the real <see cref="AppOperationsService"/> rather than a
/// hand-written fake — this is the single choke point every volume-destroying delete goes through
/// (the panel's own app delete, <c>PreviewEnvironmentService.RemoveAsync</c>'s teardown, and
/// <c>ProjectDeletionService</c>'s cascade all call <see cref="IAppOperationsService.DeleteAsync"/>
/// rather than talking to Docker themselves — see that class's own remarks), so proving the guard
/// here proves it for all three at once.
/// </summary>
public sealed class AppOperationsServiceVolumeProtectionTests
{
    private static HarboraDbContext NewDb() => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("app-ops-volume-protection-" + Guid.NewGuid()).Options);

    private static AppOperationsService NewService(HarboraDbContext db, FakeDockerEngine docker) => new(
        db,
        new FakeServerEngineFactory(docker),
        new RecordingProxyEngine(() => db.Routes.IgnoreQueryFilters().AsNoTracking().ToList()),
        new BillingGate(db, Options.Create(new BillingOptions())),
        new HostPortAllocator(db, TestIngress.Registry(), NullLogger<HostPortAllocator>.Instance),
        NullLogger<AppOperationsService>.Instance);

    private static (Workspace Workspace, Harbora.Domain.Servers.Server Server,
        Harbora.Domain.Projects.Project Project, Harbora.Domain.Projects.Environment Environment) SeedPlacement(
        HarboraDbContext db)
    {
        var workspace = new Workspace { Name = "acme", Slug = "acme" };
        var server = new Harbora.Domain.Servers.Server
        { Name = "local", Hostname = "localhost", IsLocal = true };
        var project = new Harbora.Domain.Projects.Project { WorkspaceId = workspace.Id, Name = "blog", Slug = "blog" };
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
    public async Task Deleting_an_app_with_a_protected_volume_and_removeVolumes_is_refused_before_anything_is_touched()
    {
        await using var db = NewDb();
        var (workspace, server, _, environment) = SeedPlacement(db);
        var app = new App
        {
            WorkspaceId = workspace.Id, ServerId = server.Id, EnvironmentId = environment.Id,
            Name = "api", Slug = "api", SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/api:1.0"
        };
        var volume = new Volume { AppId = app.Id, Name = "api-data", MountPath = "/data", Protected = true };
        db.Apps.Add(app);
        db.Volumes.Add(volume);
        await db.SaveChangesAsync();

        var docker = new FakeDockerEngine();
        docker.SeedContainer("harbora-api-1", "api", workspaceId: workspace.Id, appId: app.Id);
        var service = NewService(db, docker);

        var act = () => service.DeleteAsync(app.Id, removeVolumes: true, CancellationToken.None);

        (await act.Should().ThrowAsync<VolumeProtectedException>())
            .Which.Message.Should().Contain("/data").And.Contain("Protected");

        docker.Calls.Should().BeEmpty("a refusal must happen before the container is even resolved, not after it is removed");
        (await db.Apps.IgnoreQueryFilters().AnyAsync(a => a.Id == app.Id)).Should().BeTrue(
            "the app must still be there — the delete was refused, not half-applied");
        (await db.Volumes.IgnoreQueryFilters().AnyAsync(v => v.Id == volume.Id)).Should().BeTrue(
            "the protected volume's row must survive along with the app that owns it");
    }

    [Fact]
    public async Task Deleting_an_app_whose_volume_is_not_protected_removes_it_as_before()
    {
        await using var db = NewDb();
        var (workspace, server, _, environment) = SeedPlacement(db);
        var app = new App
        {
            WorkspaceId = workspace.Id, ServerId = server.Id, EnvironmentId = environment.Id,
            Name = "worker", Slug = "worker", SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/worker:1.0"
        };
        var volume = new Volume { AppId = app.Id, Name = "worker-data", MountPath = "/data" };
        db.Apps.Add(app);
        db.Volumes.Add(volume);
        await db.SaveChangesAsync();

        var docker = new FakeDockerEngine();
        var service = NewService(db, docker);

        await service.DeleteAsync(app.Id, removeVolumes: true, CancellationToken.None);

        docker.Calls.Should().Contain(c => c.Operation == "RemoveVolumeAsync" && c.Target == "worker-data",
            "an unprotected volume's data must still be destroyed when the caller asked for that — the guard must not become a blanket refusal");
        (await db.Apps.IgnoreQueryFilters().AnyAsync(a => a.Id == app.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_an_app_with_a_protected_volume_but_removeVolumes_false_still_succeeds()
    {
        // Protected is about DESTROYING data, not about detaching it. removeVolumes: false leaves the
        // bytes on the server exactly as it would for an unprotected volume — see Volume.Protected's
        // own remarks on why that line is drawn there.
        await using var db = NewDb();
        var (workspace, server, _, environment) = SeedPlacement(db);
        var app = new App
        {
            WorkspaceId = workspace.Id, ServerId = server.Id, EnvironmentId = environment.Id,
            Name = "keep-data", Slug = "keep-data", SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/keep:1.0"
        };
        db.Apps.Add(app);
        db.Volumes.Add(new Volume { AppId = app.Id, Name = "keep-data-vol", MountPath = "/data", Protected = true });
        await db.SaveChangesAsync();

        var docker = new FakeDockerEngine();
        var service = NewService(db, docker);

        await service.DeleteAsync(app.Id, removeVolumes: false, CancellationToken.None);

        docker.Calls.Should().NotContain(c => c.Operation == "RemoveVolumeAsync");
        (await db.Apps.IgnoreQueryFilters().AnyAsync(a => a.Id == app.Id)).Should().BeFalse(
            "the app itself is gone — only the Docker volume's data was never at risk here");
    }

    /// <summary>
    /// Covers "preview-environment teardown" and "environment deletion" together: both are the same
    /// code path in <see cref="AppOperationsService.DeleteAsync"/> — a preview's environment is only
    /// ever dropped once its app row is actually gone (see that method's own remarks) — so a refusal
    /// that leaves the app row in place must, by construction, leave the environment in place too.
    /// </summary>
    [Fact]
    public async Task A_protected_volume_blocks_a_preview_teardown_and_its_environment_survives_with_it()
    {
        await using var db = NewDb();
        var (workspace, server, project, _) = SeedPlacement(db);
        var parent = new App
        {
            WorkspaceId = workspace.Id, ServerId = server.Id,
            EnvironmentId = db.Environments.Local.First().Id,
            Name = "shop", Slug = "shop", SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/shop:1.0"
        };
        var previewEnvironment = new Harbora.Domain.Projects.Environment
        {
            WorkspaceId = workspace.Id, ProjectId = project.Id, Name = "preview/feature-x", Slug = "preview-feature-x"
        };
        var preview = new App
        {
            WorkspaceId = workspace.Id, ServerId = server.Id, EnvironmentId = previewEnvironment.Id,
            Name = "shop · feature-x", Slug = "shop-feature-x", SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/shop:1.0", PreviewOfAppId = parent.Id, PreviewBranch = "feature-x"
        };
        db.Apps.Add(parent);
        db.Environments.Add(previewEnvironment);
        db.Apps.Add(preview);
        db.Volumes.Add(new Volume { AppId = preview.Id, Name = "preview-data", MountPath = "/data", Protected = true });
        await db.SaveChangesAsync();

        var docker = new FakeDockerEngine();
        var service = NewService(db, docker);

        var act = () => service.DeleteAsync(preview.Id, removeVolumes: true, CancellationToken.None);

        await act.Should().ThrowAsync<VolumeProtectedException>();

        (await db.Apps.IgnoreQueryFilters().AnyAsync(a => a.Id == preview.Id)).Should().BeTrue(
            "the preview app must still be there — a preview teardown is the exact same DeleteAsync call an app delete makes");
        (await db.Environments.IgnoreQueryFilters().AnyAsync(e => e.Id == previewEnvironment.Id)).Should().BeTrue(
            "the environment is only ever dropped once its app is actually gone, so it must survive too — this is 'environment deletion' refused by the same guard");
    }
}
