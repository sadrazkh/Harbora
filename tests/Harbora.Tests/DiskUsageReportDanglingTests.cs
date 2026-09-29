using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Servers;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Docker;
using Harbora.Infrastructure.Maintenance;
using Harbora.Infrastructure.Nodes;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The disk report's dangling-images line: the page stops being blind to the thing that actually filled
/// a production disk to 96% (165 untagged images, 70 GB, against about 10 GB of tagged build images).
///
/// <para>
/// Every figure the report already showed is derived from TAGGED images under the build prefix, so on
/// that machine it would have read "about 10 GB, little reclaimable" while the disk was nearly full.
/// The count and size here come from the same daemon filter the prune uses, per server, and a server
/// that could not be asked is named as such — never rendered as "0".
/// </para>
/// </summary>
public sealed class DiskUsageReportDanglingTests : IDisposable
{
    private readonly HarboraDbContext _db = new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("disk-usage-dangling-" + Guid.NewGuid()).Options);

    private readonly FakeDockerEngine _panel = new();
    private readonly FakeServerEngineFactory _engines;

    public DiskUsageReportDanglingTests() => _engines = new FakeServerEngineFactory(_panel);

    private DiskUsageReport Report(HarboraRuntimeOptions? options = null) => new(
        _db, _engines, Options.Create(options ?? new HarboraRuntimeOptions()),
        NullLogger<DiskUsageReport>.Instance);

    private async Task<Server> AddServerAsync(string name, bool local = false)
    {
        var server = new Server { Id = Guid.NewGuid(), Name = name, IsLocal = local };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync();
        return server;
    }

    [Fact]
    public async Task The_report_shows_dangling_count_and_size_per_server()
    {
        await AddServerAsync("panel", local: true);
        var second = await AddServerAsync("web-02");
        var secondDocker = new FakeDockerEngine();
        _engines.On(second.Id, secondDocker);

        _panel.SeedDangling(count: 165, bytesEach: 1000);
        secondDocker.SeedDangling(count: 3, bytesEach: 40);

        var report = await Report().BuildAsync(default);

        var here = report.Servers.Single(s => s.ServerName == "panel").Dangling;
        here.Count.Should().Be(165);
        here.TotalBytes.Should().Be(165_000);
        here.NotExamined.Should().BeNull();

        var there = report.Servers.Single(s => s.ServerName == "web-02").Dangling;
        there.Count.Should().Be(3);
        there.TotalBytes.Should().Be(120);
    }

    [Fact]
    public async Task A_disk_full_of_dangling_images_no_longer_reports_little_reclaimable()
    {
        // The exact blindness the incident exposed: a tagged build image or two, and everything else
        // untagged. The old report said "about the size of two build images"; the reclaimable figure
        // must now include the untagged mass a cleanup will actually prune.
        var panel = await AddServerAsync("panel", local: true);

        // One living app whose only build image is the rollback target — nothing tagged is reclaimable.
        var appId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();
        _db.Apps.Add(new Harbora.Domain.Apps.App
        {
            Id = appId, WorkspaceId = Guid.NewGuid(), ServerId = panel.Id, Name = "live", Slug = "live",
            ActiveDeploymentId = deploymentId
        });
        _db.Deployments.Add(new Harbora.Domain.Deployments.Deployment
        {
            Id = deploymentId, WorkspaceId = Guid.NewGuid(), AppId = appId, Number = 1,
            ImageTag = "harbora/live:build-1", Status = Harbora.Domain.Common.DeploymentStatus.Succeeded
        });
        await _db.SaveChangesAsync();
        _panel.SeedImage("harbora/live:build-1");
        _panel.SeedDangling(count: 165, bytesEach: 1L << 20);

        var report = await Report().BuildAsync(default);

        var server = report.Servers.Single();
        server.Orphaned.Count.Should().Be(0);
        server.Apps.Single().PrunableNowTags.Should().BeEmpty();
        server.ReclaimableBytes.Should().Be(server.Dangling.TotalBytes,
            "no orphan and no superseded tag here, so the reclaimable figure is the untagged mass");
        report.ReclaimableBytes.Should().Be(165L << 20);
    }

    [Fact]
    public async Task Reclaimable_is_orphans_plus_prunable_plus_dangling_when_all_three_exist()
    {
        await AddServerAsync("panel", local: true);
        _panel.SeedImage("harbora/gone:build-1");
        _panel.SeedDangling(count: 2, bytesEach: 500);

        var report = await Report().BuildAsync(default);

        var server = report.Servers.Single();
        server.Orphaned.TotalBytes.Should().BeGreaterThan(0);
        server.ReclaimableBytes.Should().Be(server.Orphaned.TotalBytes + 1000);
    }

    [Fact]
    public async Task A_server_with_no_dangling_images_reads_as_none_found_not_as_not_examined()
    {
        await AddServerAsync("panel", local: true);

        var report = await Report().BuildAsync(default);

        var dangling = report.Servers.Single().Dangling;
        dangling.NotExamined.Should().BeNull("the daemon was asked and answered");
        dangling.Count.Should().Be(0);
    }

    // ---- the two machines that cannot be asked ----

    [Fact]
    public async Task A_v1_node_is_not_examined_for_dangling_images_and_says_why()
    {
        await AddServerAsync("panel", local: true);
        var node = await AddServerAsync("web-03");
        _engines.On(node.Id, new NodeWorkloadEngine("web-03-node", null!, null!, null!, NullLogger.Instance));

        var report = await Report().BuildAsync(default);

        var result = report.Servers.Single(s => s.ServerName == "web-03");
        result.NotExamined.Should().Be(NodeWorkloadEngine.ImagesManagedByNodeReason("web-03-node"));
        result.Dangling.NotExamined.Should().Be(result.NotExamined,
            "the same reason as the rest of the machine's row, not a second wording");
    }

    [Fact]
    public async Task An_agent_that_answers_404_is_examined_for_tagged_images_but_not_for_dangling_ones()
    {
        await AddServerAsync("panel", local: true);
        var remote = await AddServerAsync("web-02");
        var agent = StubAgentHandler.OldAgent("[]");
        _engines.On(remote.Id, new RemoteDockerEngine(
            new StubAgentHandler.Factory(agent), "http://web-02.example.com", "token"));

        var report = await Report().BuildAsync(default);

        var result = report.Servers.Single(s => s.ServerName == "web-02");
        result.NotExamined.Should().BeNull("an old agent lists tagged images, so the machine IS examined");
        result.Dangling.NotExamined.Should().Be(RemoteDockerEngine.AgentTooOldReason);
        result.Dangling.Count.Should().Be(0);
        report.ReclaimableBytes.Should().Be(0);
    }

    [Fact]
    public async Task A_total_that_leaves_out_unread_dangling_images_says_so()
    {
        await AddServerAsync("panel", local: true);
        var remote = await AddServerAsync("web-02");
        _engines.On(remote.Id, new RemoteDockerEngine(
            new StubAgentHandler.Factory(StubAgentHandler.OldAgent("[]")), "http://web-02.example.com", "token"));

        var report = await Report().BuildAsync(default);

        report.AnyNotExamined.Should().BeFalse("both servers were examined for tagged images");
        report.AnyDanglingNotExamined.Should().BeTrue(
            "but one could not be read for dangling images, so the total silently excludes them");
    }

    [Fact]
    public async Task A_report_where_every_server_was_read_for_dangling_images_makes_no_such_claim()
    {
        await AddServerAsync("panel", local: true);

        var report = await Report().BuildAsync(default);

        report.AnyDanglingNotExamined.Should().BeFalse();
    }

    [Fact]
    public async Task A_daemon_that_fails_the_dangling_read_does_not_fail_the_report()
    {
        await AddServerAsync("panel", local: true);
        _panel.SeedImage("harbora/gone:build-1");
        _panel.DanglingThrows = new InvalidOperationException("daemon fell over");

        var report = await Report().BuildAsync(default);

        var server = report.Servers.Single();
        server.NotExamined.Should().BeNull("the tagged-image half of the report still answered");
        server.Orphaned.Count.Should().Be(1);
        server.Dangling.NotExamined.Should().Contain("daemon fell over").And.Contain("dangling images");
    }

    [Fact]
    public async Task The_dangling_size_stays_an_upper_bound_only_when_it_was_read()
    {
        // An unread server contributes nothing to the total — a false zero and a real zero must not
        // be summed as if they were the same thing.
        await AddServerAsync("panel", local: true);
        var node = await AddServerAsync("web-03");
        _engines.On(node.Id, new NodeWorkloadEngine("web-03-node", null!, null!, null!, NullLogger.Instance));
        _panel.SeedDangling(2, 700);

        var report = await Report().BuildAsync(default);

        report.ReclaimableBytes.Should().Be(1400);
        report.AnyNotExamined.Should().BeTrue();
    }

    public void Dispose() => _db.Dispose();
}
