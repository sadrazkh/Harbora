using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Common;
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
/// A-brief (2026-09 r4 round): the report that says what a cleanup would reclaim before anyone
/// presses the button. Every figure it names is asked of the same rules
/// <see cref="DiskCleanupService"/> already acts on — <see cref="CleanupPlan.OrphanedBuildImages"/>
/// and <see cref="DeploymentPlanning.ImagesToPrune"/> — never a second guess at them, so this suite
/// spends most of its weight proving the two agree rather than hand-writing what "should" prune.
/// </summary>
public sealed class DiskUsageReportTests : IDisposable
{
    private readonly HarboraDbContext _db = new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("disk-usage-report-" + Guid.NewGuid()).Options);

    private readonly FakeDockerEngine _panel = new();
    private readonly FakeServerEngineFactory _engines;

    public DiskUsageReportTests() => _engines = new FakeServerEngineFactory(_panel);

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

    /// <summary>An app with <paramref name="releases"/> successful deployments, images
    /// <c>harbora/{slug}:build-1..N</c> tagged in order, none seeded on any engine by this helper.</summary>
    private async Task<(Guid AppId, List<Harbora.Domain.Deployments.Deployment> History)> AddAppAsync(
        Guid serverId, string slug, int releases, Guid? activeReleaseOverride = null)
    {
        var appId = Guid.NewGuid();
        var history = new List<Harbora.Domain.Deployments.Deployment>();
        for (var n = 1; n <= releases; n++)
        {
            var deployment = new Harbora.Domain.Deployments.Deployment
            {
                Id = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), AppId = appId, Number = n,
                ImageTag = $"harbora/{slug}:build-{n}", Status = DeploymentStatus.Succeeded
            };
            history.Add(deployment);
            _db.Deployments.Add(deployment);
        }

        var activeId = activeReleaseOverride ?? history.LastOrDefault()?.Id;
        _db.Apps.Add(new Harbora.Domain.Apps.App
        {
            Id = appId, WorkspaceId = Guid.NewGuid(), ServerId = serverId, Name = slug, Slug = slug,
            ActiveDeploymentId = activeId
        });
        await _db.SaveChangesAsync();
        return (appId, history);
    }

    public void Dispose() => _db.Dispose();

    // ---- the core reuse guarantee: this report's "prunable now" IS ImagesToPrune's answer ----

    [Fact]
    public async Task Prunable_now_tags_for_an_app_are_exactly_what_ImagesToPrune_would_prune()
    {
        var server = await AddServerAsync("panel", local: true);
        var (_, history) = await AddAppAsync(server.Id, "shop", releases: 4);
        _panel.SeedImage(
            "harbora/shop:build-1", "harbora/shop:build-2", "harbora/shop:build-3", "harbora/shop:build-4");

        var options = new HarboraRuntimeOptions { ImageRetentionCount = 2 };
        var report = await Report(options).BuildAsync(default);

        var appUsage = report.Servers.Single().Apps.Should().ContainSingle(a => a.AppSlug == "shop").Subject;

        // Independently ask the very same production rule the report is supposed to be calling, with
        // identical inputs — not a hand-written list of expected tags.
        var onHost = await _panel.ListImagesAsync("harbora/", default);
        var active = history.Last().Id;
        var expected = DeploymentPlanning.ImagesToPrune(onHost, history, active, "harbora", "shop", 2);

        appUsage.PrunableNowTags.Should().BeEquivalentTo(expected,
            "a second notion of what is prunable would drift from the cleaner that actually deletes");
    }

    [Fact]
    public async Task Retention_disabled_means_nothing_is_reported_as_prunable_now()
    {
        var server = await AddServerAsync("panel", local: true);
        await AddAppAsync(server.Id, "shop", releases: 3);
        _panel.SeedImage("harbora/shop:build-1", "harbora/shop:build-2", "harbora/shop:build-3");

        var report = await Report(new HarboraRuntimeOptions { ImageRetentionCount = 0 }).BuildAsync(default);

        var appUsage = report.Servers.Single().Apps.Single();
        appUsage.PrunableNowTags.Should().BeEmpty(
            "DiskCleanupService.SweepAsync itself never runs retention when it is disabled");
    }

    // ---- orphans are named as orphans, never folded into a living app's own figures ----

    [Fact]
    public async Task An_image_of_a_deleted_app_is_reported_as_orphaned_not_as_any_living_apps()
    {
        var server = await AddServerAsync("panel", local: true);
        await AddAppAsync(server.Id, "blog", releases: 1);
        _panel.SeedImage("harbora/blog:build-1", "harbora/shop-was-deleted:build-7");

        var report = await Report().BuildAsync(default);

        var result = report.Servers.Single();
        result.Orphaned.Tags.Should().ContainSingle().Which.Should().Be("harbora/shop-was-deleted:build-7");
        result.Apps.Should().ContainSingle().Which.AppSlug.Should().Be("blog");
        result.Apps.Single().TotalBytes.Should().BeGreaterThan(0);
        // The orphan's bytes must never be attributed to "blog" — the living app on this server.
        result.Apps.Single().PrunableNowTags.Should().NotContain("harbora/shop-was-deleted:build-7");
    }

    [Fact]
    public async Task Orphaned_bytes_and_count_come_straight_from_CleanupPlan()
    {
        var server = await AddServerAsync("panel", local: true);
        _panel.SeedImage("harbora/gone-app:build-1", "harbora/gone-app:build-2");

        var report = await Report().BuildAsync(default);

        var result = report.Servers.Single();
        var onHost = await _panel.ListImagesAsync("harbora/", default);
        var expectedTags = CleanupPlan.OrphanedBuildImages(onHost, "harbora", Enumerable.Empty<string>());

        result.Orphaned.Tags.Should().BeEquivalentTo(expectedTags);
        result.Orphaned.Count.Should().Be(2);
        result.Orphaned.TotalBytes.Should().BeGreaterThan(0);
    }

    // ---- the active deployment's image is never something a cleanup would reclaim ----

    [Fact]
    public async Task The_active_deployments_image_is_never_listed_as_prunable_now_or_counted_reclaimable()
    {
        var server = await AddServerAsync("panel", local: true);
        var (_, history) = await AddAppAsync(server.Id, "shop", releases: 5);
        _panel.SeedImage(
            "harbora/shop:build-1", "harbora/shop:build-2", "harbora/shop:build-3",
            "harbora/shop:build-4", "harbora/shop:build-5");

        // Retention window of 1: only the active tag itself would be protected by keep=1 unless the
        // active deployment's own tag is explicitly excluded — this is the fact under test.
        var options = new HarboraRuntimeOptions { ImageRetentionCount = 1 };
        var report = await Report(options).BuildAsync(default);

        var appUsage = report.Servers.Single().Apps.Single();
        var activeTag = history.Last().ImageTag;

        appUsage.ActiveTag.Should().Be(activeTag);
        appUsage.PrunableNowTags.Should().NotContain(activeTag);
        report.Servers.Single().ReclaimableBytes.Should().BeLessThan(appUsage.TotalBytes,
            "the active image's bytes must be excluded from what is reclaimable");
    }

    // ---- a server the factory refuses is not examined, and does not read as clean ----

    [Fact]
    public async Task A_server_the_factory_refuses_is_named_not_examined_with_its_reason()
    {
        await AddServerAsync("panel", local: true);
        var stranded = await AddServerAsync("web-04");
        _engines.Unreachable(stranded.Id, "no agent endpoint and no node is enrolled on it");

        var report = await Report().BuildAsync(default);

        var result = report.Servers.Should().ContainSingle(s => s.ServerName == "web-04").Subject;
        result.NotExamined.Should().Contain("no agent endpoint");
        result.Apps.Should().BeEmpty();
        result.Orphaned.Count.Should().Be(0);
        result.ReclaimableBytes.Should().Be(0);
        // Zero here must never be confused with "clean" — the report-level total excludes it entirely.
        report.ReclaimableBytes.Should().Be(0);
        report.AnyNotExamined.Should().BeTrue();
    }

    // ---- a v1 node is refused for the same reason DiskCleanupService gives ----

    [Fact]
    public async Task A_server_behind_a_v1_node_is_not_examined_for_the_same_reason_DiskCleanupService_gives()
    {
        await AddServerAsync("panel", local: true);
        var node = await AddServerAsync("web-03");
        _engines.On(node.Id, new NodeWorkloadEngine("web-03-node", null!, null!, null!, NullLogger.Instance));

        var report = await Report().BuildAsync(default);

        var result = report.Servers.Should().ContainSingle(s => s.ServerName == "web-03").Subject;
        result.NotExamined.Should().NotBeNull();
        result.NotExamined.Should().Contain("web-03-node");
        result.NotExamined.Should().Contain("manages its own images",
            "the same wording DiskCleanupService.SweepAsync uses for the identical refusal");
        result.Apps.Should().BeEmpty();
    }

    // ---- every server is named, whatever happened to it ----

    [Fact]
    public async Task Every_server_appears_once_examined_or_not()
    {
        var panel = await AddServerAsync("panel", local: true);
        var node = await AddServerAsync("web-03");
        var stranded = await AddServerAsync("web-04");
        _engines.On(node.Id, new NodeWorkloadEngine("web-03-node", null!, null!, null!, NullLogger.Instance));
        _engines.Unreachable(stranded.Id, "no agent endpoint");

        var report = await Report().BuildAsync(default);

        report.Servers.Select(s => s.ServerName).Should().BeEquivalentTo("panel", "web-03", "web-04");
        report.AnyExamined.Should().BeTrue();
        report.AnyNotExamined.Should().BeTrue();
    }

    // ---- reclaimable total: orphans plus prunable, nothing else ----

    [Fact]
    public async Task Reclaimable_total_is_orphan_bytes_plus_prunable_bytes_across_apps()
    {
        var server = await AddServerAsync("panel", local: true);
        await AddAppAsync(server.Id, "shop", releases: 3);
        _panel.SeedImage("harbora/shop:build-1", "harbora/shop:build-2", "harbora/shop:build-3");
        _panel.SeedImage("harbora/gone:build-1");

        var report = await Report(new HarboraRuntimeOptions { ImageRetentionCount = 1 }).BuildAsync(default);

        var result = report.Servers.Single();
        var expected = result.Orphaned.TotalBytes + result.Apps.Sum(a => a.PrunableNowBytes);

        result.ReclaimableBytes.Should().Be(expected);
        result.ReclaimableBytes.Should().BeGreaterThan(0);
    }

    // ---- known volumes carry the database's own measured size, unmeasured reads as unmeasured ----

    [Fact]
    public async Task A_volume_never_measured_reports_a_null_size_not_zero()
    {
        var server = await AddServerAsync("panel", local: true);
        var app = new Harbora.Domain.Apps.App
        { Id = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), ServerId = server.Id, Name = "blog", Slug = "blog" };
        _db.Apps.Add(app);
        _db.Volumes.Add(new Harbora.Domain.Apps.Volume
        { AppId = app.Id, Name = "harbora-vol-blog-data", MountPath = "/data" });
        await _db.SaveChangesAsync();

        var report = await Report().BuildAsync(default);

        var volume = report.Servers.Single().Volumes.Should().ContainSingle().Subject;
        volume.Name.Should().Be("harbora-vol-blog-data");
        volume.SizeBytes.Should().BeNull("StorageMeasurer has not walked this volume yet");
    }

    [Fact]
    public async Task A_measured_volume_reports_the_stored_size()
    {
        var server = await AddServerAsync("panel", local: true);
        var app = new Harbora.Domain.Apps.App
        { Id = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), ServerId = server.Id, Name = "blog", Slug = "blog" };
        _db.Apps.Add(app);
        _db.Volumes.Add(new Harbora.Domain.Apps.Volume
        {
            AppId = app.Id, Name = "harbora-vol-blog-data", MountPath = "/data",
            StorageBytes = 12345, StorageMeasuredAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        var report = await Report().BuildAsync(default);

        report.Servers.Single().Volumes.Single().SizeBytes.Should().Be(12345);
    }
}
