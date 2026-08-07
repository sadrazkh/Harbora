using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Servers;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Maintenance;
using Harbora.Infrastructure.Nodes;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Which machines a cleanup run actually sweeps.
///
/// <para>
/// The sweep read every application on the platform and then removed images on one daemon: the
/// panel's own. A build image left behind on another server was never a candidate, and the run
/// reported a number that only ever described one machine — so an operator watching a full disk on a
/// second server pressed the button, read "0 images removed", and concluded there was nothing there.
/// </para>
/// </summary>
public sealed class DiskCleanupTests : IDisposable
{
    private readonly HarboraDbContext _db = new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("disk-cleanup-" + Guid.NewGuid()).Options);

    private readonly FakeDockerEngine _panel = new();
    private readonly FakeServerEngineFactory _engines;

    public DiskCleanupTests() => _engines = new FakeServerEngineFactory(_panel);

    private DiskCleanupService Service(HarboraRuntimeOptions? options = null) => new(
        _db, _engines, Options.Create(options ?? new HarboraRuntimeOptions()),
        NullLogger<DiskCleanupService>.Instance);

    private async Task<Server> AddServerAsync(string name, bool local = false)
    {
        var server = new Server { Id = Guid.NewGuid(), Name = name, IsLocal = local };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync();
        return server;
    }

    /// <summary>A living app with one successful release, so its own image is a rollback target.</summary>
    private async Task AddAppAsync(Guid serverId, string slug)
    {
        var appId = Guid.NewGuid();
        _db.Apps.Add(new Harbora.Domain.Apps.App
        { Id = appId, WorkspaceId = Guid.NewGuid(), ServerId = serverId, Name = slug, Slug = slug });
        _db.Deployments.Add(new Harbora.Domain.Deployments.Deployment
        {
            Id = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), AppId = appId, Number = 1,
            ImageTag = $"harbora/{slug}:build-1", Status = Harbora.Domain.Common.DeploymentStatus.Succeeded
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task Each_servers_own_daemon_is_swept_with_its_own_leftovers()
    {
        var panelServer = await AddServerAsync("panel", local: true);
        var second = await AddServerAsync("web-02");
        var secondDocker = new FakeDockerEngine();
        _engines.On(second.Id, secondDocker);

        await AddAppAsync(panelServer.Id, "blog");
        _panel.SeedImage("harbora/blog:build-1", "harbora/gone:build-1");
        secondDocker.SeedImage("harbora/shop-was-deleted:build-7");

        var result = await Service().RunAsync(default);

        _panel.OperationsOn("harbora/gone:build-1").Should().Contain("RemoveImageAsync");
        secondDocker.OperationsOn("harbora/shop-was-deleted:build-7").Should().Contain("RemoveImageAsync");
        _panel.StoredImageTags.Should().Contain("harbora/blog:build-1", "a living app's image is not an orphan");
        result.OrphanRemoved.Should().Be(2);
    }

    /// <summary>
    /// The protection list is every application on the platform, not the ones on the machine being
    /// swept. An app moved to another server would otherwise have its rollback images deleted from
    /// the machine it just left.
    /// </summary>
    [Fact]
    public async Task An_image_belonging_to_an_app_on_another_server_is_left_alone()
    {
        var panelServer = await AddServerAsync("panel", local: true);
        var second = await AddServerAsync("web-02");
        _engines.On(second.Id, new FakeDockerEngine());

        await AddAppAsync(second.Id, "blog");
        _panel.SeedImage("harbora/blog:build-1");

        var result = await Service().RunAsync(default);

        _panel.StoredImageTags.Should().Contain("harbora/blog:build-1");
        result.OrphanRemoved.Should().Be(0);
    }

    [Fact]
    public async Task Every_server_is_visited_exactly_once_and_reported_on_by_name()
    {
        var panelServer = await AddServerAsync("panel", local: true);
        var second = await AddServerAsync("web-02");
        _engines.On(second.Id, new FakeDockerEngine());

        var result = await Service().RunAsync(default);

        _engines.Resolved.Should().BeEquivalentTo(new[] { panelServer.Id, second.Id });
        result.Servers.Select(s => s.ServerName).Should().BeEquivalentTo("panel", "web-02");
    }

    /// <summary>
    /// A v1 node lists no images and removes none — both by design, and both silent. Counting that
    /// as a clean machine is the reading an operator will act on, so it says what happened instead.
    /// </summary>
    [Fact]
    public async Task A_node_that_manages_its_own_images_contributes_nothing_and_says_so()
    {
        await AddServerAsync("panel", local: true);
        var node = await AddServerAsync("web-03");
        _engines.On(node.Id, new NodeWorkloadEngine("web-03-node", null!, null!, null!, NullLogger.Instance));

        var result = await Service().RunAsync(default);

        var reported = result.Servers.Should().ContainSingle(s => s.ServerName == "web-03").Subject;
        reported.Skipped.Should().NotBeNull().And.Subject.As<string>().Should().Contain("web-03-node");
        reported.OrphanRemoved.Should().Be(0);
        reported.FreedBytes.Should().BeNull("unmeasured is not zero");
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_is_reported_rather_than_swallowed()
    {
        await AddServerAsync("panel", local: true);
        var stranded = await AddServerAsync("web-04");
        _engines.Unreachable(stranded.Id, "no agent endpoint and no node is enrolled on it");

        var result = await Service().RunAsync(default);

        result.Servers.Should().Contain(s =>
            s.ServerName == "web-04" && s.Skipped!.Contains("no agent endpoint"));
    }

    /// <summary>
    /// An app created before the platform had servers carries <c>Guid.Empty</c>. The factory answers
    /// for an unknown server the way it always has — this machine — and grouping by server must not
    /// quietly drop such an app out of the sweep entirely.
    /// </summary>
    [Fact]
    public async Task An_app_carrying_no_real_server_is_still_swept_on_this_machine()
    {
        await AddServerAsync("panel", local: true);
        await AddAppAsync(Guid.Empty, "legacy");
        _panel.SeedImage("harbora/legacy:build-1", "harbora/legacy:build-2");

        var result = await Service(new HarboraRuntimeOptions { ImageRetentionCount = 1 }).RunAsync(default);

        result.RetentionRemoved.Should().Be(1);
        _panel.StoredImageTags.Should().BeEquivalentTo("harbora/legacy:build-1");
    }

    /// <summary>
    /// Retention is per application, so it must run against the daemon that app deploys to — running
    /// it against the panel would remove nothing and report a number that describes nothing.
    /// </summary>
    [Fact]
    public async Task Retention_for_an_app_runs_on_the_server_that_app_deploys_to()
    {
        await AddServerAsync("panel", local: true);
        var second = await AddServerAsync("web-02");
        var secondDocker = new FakeDockerEngine();
        _engines.On(second.Id, secondDocker);

        var appId = Guid.NewGuid();
        _db.Apps.Add(new Harbora.Domain.Apps.App
        { Id = appId, WorkspaceId = Guid.NewGuid(), ServerId = second.Id, Name = "shop", Slug = "shop" });
        for (var n = 1; n <= 4; n++)
            _db.Deployments.Add(new Harbora.Domain.Deployments.Deployment
            {
                Id = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), AppId = appId, Number = n,
                ImageTag = $"harbora/shop:build-{n}",
                Status = Harbora.Domain.Common.DeploymentStatus.Succeeded
            });
        await _db.SaveChangesAsync();

        secondDocker.SeedImage(
            "harbora/shop:build-1", "harbora/shop:build-2", "harbora/shop:build-3", "harbora/shop:build-4");

        var result = await Service(new HarboraRuntimeOptions { ImageRetentionCount = 2 }).RunAsync(default);

        result.RetentionRemoved.Should().BeGreaterThan(0);
        secondDocker.StoredImageTags.Should().NotContain("harbora/shop:build-1");
        _panel.Calls.Should().NotContain(c => c.Operation == "RemoveImageAsync");
    }

    public void Dispose() => _db.Dispose();
}
