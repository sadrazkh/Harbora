using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Servers;
using Harbora.Infrastructure.Nodes;
using Harbora.Infrastructure.Storage;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// HARBORA-0033's disk-side half: <see cref="VolumeOrphanReport"/> could only ever say "not checked"
/// for a volume that exists on a server's disk with no <see cref="Volume"/> row at all — this is what
/// actually checks, per server, through the new
/// <see cref="Harbora.Application.Abstractions.IDockerEngine.ListVolumesAsync"/>.
///
/// <para>
/// Modelled on <c>DiskCleanupTests</c>: every server is named, whether it answered, refused, or could
/// not be reached at all — never folded silently into one total, for the identical reason that suite
/// gives for image cleanup.
/// </para>
/// </summary>
public sealed class DiskVolumeOrphanReportTests : IDisposable
{
    private readonly HarboraDbContext _db = new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("disk-volume-orphan-" + Guid.NewGuid()).Options);

    private readonly FakeDockerEngine _panel = new();
    private readonly FakeServerEngineFactory _engines;

    public DiskVolumeOrphanReportTests() => _engines = new FakeServerEngineFactory(_panel);

    private DiskVolumeOrphanReport Report() => new(_db, _engines, NullLogger<DiskVolumeOrphanReport>.Instance);

    private async Task<Server> AddServerAsync(string name, bool local = false)
    {
        var server = new Server { Id = Guid.NewGuid(), Name = name, IsLocal = local };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync();
        return server;
    }

    /// <summary>An app with one attached volume — the row this report treats as "known".</summary>
    private async Task<string> AddAppWithVolumeAsync(Guid serverId, string slug, string mountPath = "/data")
    {
        var app = new App
        { Id = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), ServerId = serverId, Name = slug, Slug = slug };
        var volumeName = Harbora.Infrastructure.Storage.MountPath.VolumeNameFor(slug, mountPath);
        _db.Apps.Add(app);
        _db.Volumes.Add(new Volume { AppId = app.Id, Name = volumeName, MountPath = mountPath });
        await _db.SaveChangesAsync();
        return volumeName;
    }

    public void Dispose() => _db.Dispose();

    // ---- the core question: a disk volume with no database row ----

    [Fact]
    public async Task A_harbora_volume_on_disk_with_no_database_row_is_named_as_an_orphan()
    {
        var server = await AddServerAsync("panel", local: true);
        await AddAppWithVolumeAsync(server.Id, "blog");
        // Left behind by an unmount or an app-delete that kept its data — no App, no Volume row.
        _panel.SeedVolume("harbora-vol-gone-app-data");

        var report = await Report().BuildAsync(default);

        var reached = report.Servers.Should().ContainSingle().Subject;
        reached.Outcome.Should().Be(ServerVolumeCheckOutcome.Reached);
        reached.Orphans.Should().ContainSingle(o => o.Name == "harbora-vol-gone-app-data");
    }

    [Fact]
    public async Task A_volume_the_platform_itself_provisioned_is_not_an_orphan()
    {
        var server = await AddServerAsync("panel", local: true);
        var volumeName = await AddAppWithVolumeAsync(server.Id, "blog");
        // EnsureVolumeAsync is how the deployment pipeline actually puts a volume on disk — simulated
        // here rather than SeedVolume, so this proves the real provisioning path is recognised too.
        await _panel.EnsureVolumeAsync(volumeName, default);

        var report = await Report().BuildAsync(default);

        report.Servers.Should().ContainSingle().Which.Orphans.Should().BeEmpty();
    }

    [Fact]
    public async Task A_clean_server_reports_zero_orphans_explicitly()
    {
        var server = await AddServerAsync("panel", local: true);
        await AddAppWithVolumeAsync(server.Id, "blog");

        var report = await Report().BuildAsync(default);

        var reached = report.Servers.Should().ContainSingle().Subject;
        reached.Outcome.Should().Be(ServerVolumeCheckOutcome.Reached);
        reached.Orphans.Should().BeEmpty();
    }

    // ---- the prefix boundary: only harbora-vol-* is ever a candidate ----

    [Fact]
    public async Task A_disk_volume_outside_the_harbora_vol_naming_scheme_is_never_flagged()
    {
        var server = await AddServerAsync("panel", local: true);
        // A managed service's own data volume and a compose stack's volume both use different naming
        // schemes and are never tracked as a Volume row — see MountPath.HarboraVolumePrefix's own
        // remarks. Flagging either here would tell an operator a live database is an orphan.
        _panel.SeedVolume("harbora-svc-mydb-data");
        _panel.SeedVolume("harbora-blog-uploads");
        _panel.SeedVolume("some-unrelated-volume-a-person-made-by-hand");

        var report = await Report().BuildAsync(default);

        report.Servers.Should().ContainSingle().Which.Orphans.Should().BeEmpty(
            "none of these seeded volumes are shaped like an app volume this report could ever have tracked");
    }

    // ---- scoping: an orphan on one server never appears on another's list ----

    [Fact]
    public async Task An_orphan_is_attributed_to_the_server_it_is_actually_on()
    {
        var first = await AddServerAsync("web-01", local: true);
        var second = await AddServerAsync("web-02");
        var secondDocker = new FakeDockerEngine();
        _engines.On(second.Id, secondDocker);

        await AddAppWithVolumeAsync(first.Id, "blog");
        secondDocker.SeedVolume("harbora-vol-shop-data"); // orphan, but only on web-02

        var report = await Report().BuildAsync(default);

        report.Servers.Single(s => s.ServerName == "web-01").Orphans.Should().BeEmpty();
        report.Servers.Single(s => s.ServerName == "web-02").Orphans.Should()
            .ContainSingle(o => o.Name == "harbora-vol-shop-data");
    }

    /// <summary>
    /// A volume genuinely owned on server A must never be treated as an orphan on server B just
    /// because both machines happen to answer with a docker volume of the same name.
    /// </summary>
    [Fact]
    public async Task A_same_named_volume_known_on_one_server_is_not_assumed_known_on_another()
    {
        var first = await AddServerAsync("web-01", local: true);
        var second = await AddServerAsync("web-02");
        var secondDocker = new FakeDockerEngine();
        _engines.On(second.Id, secondDocker);

        var volumeName = await AddAppWithVolumeAsync(first.Id, "blog"); // Volume row points at web-01
        secondDocker.SeedVolume(volumeName); // same name physically present on web-02 too

        var report = await Report().BuildAsync(default);

        report.Servers.Single(s => s.ServerName == "web-01").Orphans.Should().BeEmpty();
        report.Servers.Single(s => s.ServerName == "web-02").Orphans.Should().ContainSingle(o => o.Name == volumeName);
    }

    // ---- a server that could not be reached at all ----

    [Fact]
    public async Task A_server_that_cannot_be_reached_is_named_rather_than_excluded_from_the_total()
    {
        await AddServerAsync("panel", local: true);
        var stranded = await AddServerAsync("web-04");
        _engines.Unreachable(stranded.Id, "no agent endpoint and no node is enrolled on it");

        var report = await Report().BuildAsync(default);

        var result = report.Servers.Should().ContainSingle(s => s.ServerName == "web-04").Subject;
        result.Outcome.Should().Be(ServerVolumeCheckOutcome.Unreachable);
        result.Reason.Should().Contain("no agent endpoint");
        result.Orphans.Should().BeEmpty();
    }

    [Fact]
    public async Task A_server_reached_but_whose_listing_call_fails_is_also_named_unreachable()
    {
        await AddServerAsync("panel", local: true);
        var flaky = await AddServerAsync("web-05");
        var flakyDocker = new FakeDockerEngine { ListVolumesThrows = new InvalidOperationException("connection reset") };
        _engines.On(flaky.Id, flakyDocker);

        var report = await Report().BuildAsync(default);

        var result = report.Servers.Should().ContainSingle(s => s.ServerName == "web-05").Subject;
        result.Outcome.Should().Be(ServerVolumeCheckOutcome.Unreachable);
        result.Reason.Should().Contain("connection reset");
    }

    // ---- a v1 node's named refusal (D4's precedent) ----

    [Fact]
    public async Task A_v1_node_that_has_no_list_volumes_verb_is_refused_by_name_not_marked_unreachable()
    {
        await AddServerAsync("panel", local: true);
        var node = await AddServerAsync("web-03");
        // The real production class, exactly as DiskCleanupTests exercises it for image listing —
        // constructing it directly proves NodeWorkloadEngine's own exception message, not a stand-in.
        _engines.On(node.Id, new NodeWorkloadEngine("web-03-node", null!, null!, null!, NullLogger.Instance));

        var report = await Report().BuildAsync(default);

        var result = report.Servers.Should().ContainSingle(s => s.ServerName == "web-03").Subject;
        result.Outcome.Should().Be(ServerVolumeCheckOutcome.Refused);
        result.Reason.Should().Contain("web-03-node");
        result.Reason.Should().Contain("ListVolumes verb",
            "an operator reading this must be told what is missing, not just that something failed");
    }

    // ---- every server is visited, always ----

    [Fact]
    public async Task Every_registered_server_appears_in_the_result_whatever_happened_to_it()
    {
        var panel = await AddServerAsync("panel", local: true);
        var node = await AddServerAsync("web-03");
        var stranded = await AddServerAsync("web-04");
        _engines.On(node.Id, new NodeWorkloadEngine("web-03-node", null!, null!, null!, NullLogger.Instance));
        _engines.Unreachable(stranded.Id, "no agent endpoint");

        var report = await Report().BuildAsync(default);

        report.Servers.Select(s => s.ServerName).Should().BeEquivalentTo("panel", "web-03", "web-04");
        _engines.Resolved.Should().Contain([panel.Id, node.Id]); // stranded throws inside ResolveAsync itself
    }

    // ---- rendering ----

    [Fact]
    public void The_rendered_report_names_an_orphan_rather_than_only_counting_it()
    {
        var orphan = new DiskOrphanVolume("harbora-vol-gone-app-data", DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var reached = ServerDiskVolumeResult.Reached(Guid.NewGuid(), "panel", [orphan]);
        var report = new DiskVolumeOrphanReportResult([reached]);

        var text = DiskVolumeOrphanReport.Render(report);

        text.Should().Contain("harbora-vol-gone-app-data");
        text.Should().Contain("panel");
    }

    [Fact]
    public void A_clean_reached_server_says_zero_plainly()
    {
        var reached = ServerDiskVolumeResult.Reached(Guid.NewGuid(), "panel", []);
        var report = new DiskVolumeOrphanReportResult([reached]);

        var text = DiskVolumeOrphanReport.Render(report);

        text.Should().Contain("0");
        text.Should().Contain("Every registered server answered.");
    }

    [Fact]
    public void An_unreached_server_is_named_in_its_own_section_never_merged_into_the_clean_total()
    {
        var reached = ServerDiskVolumeResult.Reached(Guid.NewGuid(), "panel", []);
        var refused = ServerDiskVolumeResult.Refused(Guid.NewGuid(), "web-03", "web-03-node cannot list its volumes.");
        var report = new DiskVolumeOrphanReportResult([reached, refused]);

        var text = DiskVolumeOrphanReport.Render(report);

        text.Should().Contain("Servers checked: 1 of 2");
        text.Should().Contain("web-03");
        text.Should().Contain("refused");
        text.Should().NotContain("Every registered server answered.",
            "one server did not answer, so the report must not claim they all did");
    }

    [Fact]
    public void The_report_states_it_is_read_only_and_names_no_delete_action()
    {
        var report = new DiskVolumeOrphanReportResult([ServerDiskVolumeResult.Reached(Guid.NewGuid(), "panel", [])]);

        var text = DiskVolumeOrphanReport.Render(report);

        text.Should().Contain("Read-only");
        text.Should().NotContain("Deleted", "this report only ever finds volumes, it never removes them");
    }

    // ---- the rule that defines the whole report: it writes nothing ----

    [Fact]
    public async Task Building_the_report_leaves_the_database_and_the_engines_untouched()
    {
        var server = await AddServerAsync("panel", local: true);
        var volumeName = await AddAppWithVolumeAsync(server.Id, "blog");
        _panel.SeedVolume(volumeName); // on disk AND known, so "nothing removed" covers both kinds
        _panel.SeedVolume("harbora-vol-gone-app-data");

        await Report().BuildAsync(default);

        (await _db.Volumes.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        _panel.Calls.Should().NotContain(c =>
            c.Operation == nameof(FakeDockerEngine.RemoveVolumeAsync) ||
            c.Operation == nameof(FakeDockerEngine.EnsureVolumeAsync));
        var stillThere = (await _panel.ListVolumesAsync(default)).Select(v => v.Name).ToList();
        stillThere.Should().Contain([volumeName, "harbora-vol-gone-app-data"]);
    }
}
