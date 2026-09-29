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
/// The third sweep: dangling images, the half of the disk the cleanup could not see.
///
/// <para>
/// On the live server the disk reached 96% with 165 dangling images (70 GB) against about 10 GB of
/// Harbora's own tagged build images — and <see cref="DiskCleanupService"/> only ever considers tagged
/// images under the build prefix, so "Clean up disk" would have freed almost nothing. A manual
/// <c>docker image prune -f</c> reclaimed 69.78 GB and took it to 28%.
/// </para>
///
/// <para>
/// <b>What these prove and what they cannot.</b> There is no Docker on the machine that runs this
/// suite. What is proven here is what the SERVICE asks the engine to do (only the prune call, never a
/// removal by name) and how it reports what the engine answers, including the two machines that cannot
/// answer. What the daemon then does with a prune call is Docker's behaviour, cited from the manual
/// prune above and not reproduced here; the request the real <c>DockerEngine</c> makes is pinned in
/// <see cref="DockerEngineDanglingTests"/>.
/// </para>
/// </summary>
public sealed class DiskCleanupDanglingTests : IDisposable
{
    private readonly HarboraDbContext _db = new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("disk-cleanup-dangling-" + Guid.NewGuid()).Options);

    private readonly FakeDockerEngine _panel = new();
    private readonly FakeServerEngineFactory _engines;

    public DiskCleanupDanglingTests() => _engines = new FakeServerEngineFactory(_panel);

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

    // ---- the sweep itself ----

    [Fact]
    public async Task Dangling_images_are_pruned_through_the_engines_prune_call_and_its_own_figures_are_reported()
    {
        var panel = await AddServerAsync("panel", local: true);
        _panel.SeedDangling(count: 3, bytesEach: 1000);
        // The daemon's own claim, deliberately not the sum of the image sizes: SpaceReclaimed is what
        // Docker says, and the report must carry that figure, not this test's arithmetic.
        _panel.DaemonReportsReclaimed = 2500;

        var result = await Service().RunAsync(default);

        _panel.CountOf(nameof(IDockerEngine.PruneDanglingImagesAsync)).Should().Be(1);
        _panel.DanglingCount.Should().Be(0);

        result.DanglingRemoved.Should().Be(3);
        result.DanglingReclaimedBytes.Should().Be(2500);

        var server = result.Servers.Should().ContainSingle(s => s.ServerId == panel.Id).Subject;
        server.DanglingRemoved.Should().Be(3);
        server.DanglingReclaimedBytes.Should().Be(2500);
        server.DanglingNotExamined.Should().BeNull("the machine was asked and answered");
    }

    [Fact]
    public async Task A_machine_that_was_pruned_and_had_nothing_reads_as_zero_examined_not_as_not_examined()
    {
        await AddServerAsync("panel", local: true);

        var result = await Service().RunAsync(default);

        var server = result.Servers.Single();
        server.DanglingNotExamined.Should().BeNull();
        server.DanglingRemoved.Should().Be(0);
        server.DanglingReclaimedBytes.Should().Be(0, "the daemon answered, and its answer was zero bytes");
    }

    [Fact]
    public async Task Tagged_images_including_unused_ones_are_never_passed_to_anything_that_removes_them()
    {
        var panel = await AddServerAsync("panel", local: true);
        await AddAppAsync(panel.Id, "blog");

        // A build base nothing uses, a customer's image, and the panel's own rollback tag: the three
        // things "prune -a" semantics would delete, and the reason this sweep must not have them.
        _panel.SeedImage(
            "mcr.microsoft.com/dotnet/sdk:10.0",
            "customer/app:latest",
            "harbora/blog:build-1",
            "harbora/gone:build-1"); // an orphan — the ONE tagged image this cleanup may legitimately remove
        _panel.SeedDangling(5, 1000);

        await Service().RunAsync(default);

        _panel.Calls.Where(c => c.Operation == nameof(IDockerEngine.RemoveImageAsync))
            .Select(c => c.Target).Should().Equal(new[] { "harbora/gone:build-1" },
                "the only named removal is the orphan the first sweep always removed — the dangling sweep " +
                "added none, and no tagged base, customer image or rollback tag was ever named");
        _panel.StoredImageTags.Should().BeEquivalentTo(
            "mcr.microsoft.com/dotnet/sdk:10.0", "customer/app:latest", "harbora/blog:build-1");
        _panel.DanglingCount.Should().Be(0, "and the untagged images went, through the prune call");
    }

    [Fact]
    public async Task The_dangling_only_sweep_calls_the_prune_and_nothing_else_not_even_the_orphan_removal()
    {
        var panel = await AddServerAsync("panel", local: true);
        await AddAppAsync(panel.Id, "blog");
        _panel.SeedImage("mcr.microsoft.com/dotnet/sdk:10.0", "harbora/blog:build-1", "harbora/gone:build-1");
        _panel.SeedDangling(4, 1000);

        var result = await Service().PruneDanglingImagesAsync(default);

        _panel.Calls.Select(c => c.Operation).Should().Equal(
            new[] { nameof(IDockerEngine.PruneDanglingImagesAsync) },
            "an unattended sweep that could reach the orphan or retention removals is one wrong argument " +
            "from deleting a rollback image at three in the morning; this one has no path to them");
        _panel.StoredImageTags.Should().Contain("harbora/gone:build-1",
            "the orphan is the button's business, not the background sweep's");
        result.Removed.Should().Be(4);
    }

    // ---- honesty about what was measured ----

    [Fact]
    public async Task Freed_bytes_stays_the_disks_own_difference_and_the_daemons_figure_sits_beside_it()
    {
        await AddServerAsync("panel", local: true);
        _panel.FreeDiskBytes = 10L << 30;
        _panel.SeedDangling(2, 100);
        _panel.DaemonReportsReclaimed = 999_999;
        // What the disk actually did: 5000 bytes came free. A different number from what the daemon said.
        _panel.OnPruneDangling = () => _panel.FreeDiskBytes += 5000;

        var result = await Service().RunAsync(default);

        result.FreedBytes.Should().Be(5000,
            "FreedBytes is measured before all three sweeps and after all three, and stays the honest total");
        result.DanglingReclaimedBytes.Should().Be(999_999,
            "the daemon's own claim is reported as its own figure, never blended into the measured one");
    }

    // ---- the two machines that cannot be asked ----

    [Fact]
    public async Task A_v1_node_is_not_examined_for_dangling_images_with_the_reason_it_already_gets_and_does_not_read_as_clean()
    {
        await AddServerAsync("panel", local: true);
        var node = await AddServerAsync("web-03");
        _engines.On(node.Id, new NodeWorkloadEngine("web-03-node", null!, null!, null!, NullLogger.Instance));

        var result = await Service().RunAsync(default);

        var reported = result.Servers.Should().ContainSingle(s => s.ServerName == "web-03").Subject;
        reported.DanglingNotExamined.Should().Be(NodeWorkloadEngine.ImagesManagedByNodeReason("web-03-node"));
        reported.DanglingNotExamined.Should().Be(reported.Skipped,
            "the same reason the whole-machine skip already gives, not a second wording of it");
        reported.DanglingReclaimedBytes.Should().BeNull("unmeasured is not zero");
    }

    [Fact]
    public async Task The_v1_node_engine_itself_refuses_by_name_rather_than_answering_empty()
    {
        var engine = new NodeWorkloadEngine("web-03-node", null!, null!, null!, NullLogger.Instance);

        var read = await FluentActions.Awaiting(() => engine.GetDanglingImagesAsync(default))
            .Should().ThrowAsync<ImageSweepUnavailableException>();
        var prune = await FluentActions.Awaiting(() => engine.PruneDanglingImagesAsync(default))
            .Should().ThrowAsync<ImageSweepUnavailableException>();

        read.Which.Reason.Should().Be(NodeWorkloadEngine.ImagesManagedByNodeReason("web-03-node"));
        prune.Which.Reason.Should().Be(NodeWorkloadEngine.ImagesManagedByNodeReason("web-03-node"));
    }

    [Fact]
    public async Task An_agent_that_answers_404_is_not_examined_for_dangling_images_but_is_still_swept_for_the_rest()
    {
        await AddServerAsync("panel", local: true);
        var remote = await AddServerAsync("web-02");
        var agent = StubAgentHandler.OldAgent(
            """[{"id":"sha256:1","tag":"harbora/gone:build-1","createdAt":"2026-01-01T00:00:00+00:00","sizeBytes":100}]""");
        _engines.On(remote.Id, new RemoteDockerEngine(
            new StubAgentHandler.Factory(agent), "http://web-02.example.com", "token"));

        var result = await Service().RunAsync(default);

        var reported = result.Servers.Should().ContainSingle(s => s.ServerName == "web-02").Subject;
        reported.DanglingNotExamined.Should().Be(RemoteDockerEngine.AgentTooOldReason);
        reported.DanglingNotExamined.Should().Contain("too old");
        reported.DanglingReclaimedBytes.Should().BeNull("a 404 is 'not asked', never zero");
        reported.DanglingRemoved.Should().Be(0);

        reported.Skipped.Should().BeNull("the machine WAS swept — an old agent lists and removes tagged images fine");
        reported.OrphanRemoved.Should().Be(1, "so the orphan on that agent was removed as before");
        reported.Failed.Should().Be(0, "a 404 is the agent being too old, never a failed removal");

        agent.Requests.Should().Contain(r => r.Path == "/agent/images/dangling/prune",
            "the endpoint WAS asked — a 404 from it is how the panel learned the agent is too old");
    }

    [Fact]
    public async Task An_engine_that_cannot_be_asked_reads_as_not_examined_even_if_it_holds_dangling_images()
    {
        await AddServerAsync("panel", local: true);
        _panel.SeedDangling(9, 1000);
        _panel.DanglingThrows = new ImageSweepUnavailableException("this engine cannot be asked");

        var result = await Service().RunAsync(default);

        var server = result.Servers.Single();
        server.DanglingNotExamined.Should().Be("this engine cannot be asked");
        server.DanglingRemoved.Should().Be(0);
        result.DanglingReclaimedBytes.Should().BeNull();
    }

    // ---- a prune that fails ----

    [Fact]
    public async Task A_prune_that_fails_is_reported_with_its_reason_and_the_next_server_is_still_swept()
    {
        await AddServerAsync("panel", local: true);
        var second = await AddServerAsync("web-02");
        var secondDocker = new FakeDockerEngine { PruneDanglingThrows = new InvalidOperationException("daemon said no") };
        secondDocker.SeedImage("harbora/gone:build-1");
        _engines.On(second.Id, secondDocker);
        _panel.SeedDangling(2, 500);

        var result = await Service().RunAsync(default);

        var failing = result.Servers.Single(s => s.ServerName == "web-02");
        failing.DanglingNotExamined.Should().Contain("daemon said no").And.Contain("failed");
        failing.OrphanRemoved.Should().Be(1, "the failed prune did not undo the sweeps that came before it");
        failing.Failed.Should().Be(0, "Failed counts refused removals; a failed prune carries its reason instead");

        result.DanglingRemoved.Should().Be(2, "the other machine was still pruned");
    }

    [Fact]
    public async Task The_dangling_only_sweep_visits_every_server_and_names_the_ones_it_could_not_ask()
    {
        await AddServerAsync("panel", local: true);
        var stranded = await AddServerAsync("web-04");
        var node = await AddServerAsync("web-03");
        var failing = await AddServerAsync("web-05");
        _engines.Unreachable(stranded.Id, "no agent endpoint and no node is enrolled on it");
        _engines.On(node.Id, new NodeWorkloadEngine("web-03-node", null!, null!, null!, NullLogger.Instance));
        _engines.On(failing.Id, new FakeDockerEngine { PruneDanglingThrows = new InvalidOperationException("boom") });
        _panel.SeedDangling(6, 100);

        var result = await Service().PruneDanglingImagesAsync(default);

        result.Servers.Select(s => s.ServerName).Should().BeEquivalentTo("panel", "web-04", "web-03", "web-05");

        result.Servers.Single(s => s.ServerName == "panel").Should().Match<DanglingSweepServerResult>(
            s => s.Removed == 6 && s.ReclaimedBytes == 600 && s.NotExamined == null && !s.Faulted);

        var unreachable = result.Servers.Single(s => s.ServerName == "web-04");
        unreachable.NotExamined.Should().Contain("no agent endpoint");
        unreachable.Faulted.Should().BeFalse("not being able to reach a machine is reported, not raised");
        unreachable.ReclaimedBytes.Should().BeNull();

        result.Servers.Single(s => s.ServerName == "web-03").NotExamined
            .Should().Be(NodeWorkloadEngine.ImagesManagedByNodeReason("web-03-node"));

        var faulted = result.Servers.Single(s => s.ServerName == "web-05");
        faulted.Faulted.Should().BeTrue();
        faulted.NotExamined.Should().Contain("boom");

        result.Removed.Should().Be(6);
        result.AnyFaulted.Should().BeTrue();
    }

    [Fact]
    public async Task A_cancellation_is_the_callers_own_and_is_not_reported_as_a_failed_prune()
    {
        await AddServerAsync("panel", local: true);
        var second = await AddServerAsync("web-02");
        var secondDocker = new FakeDockerEngine();
        _engines.On(second.Id, secondDocker);
        using var dying = new CancellationTokenSource();
        // Whichever machine is visited first has the token die inside its prune.
        _panel.CancelWhenPruneIsAttempted = dying;
        secondDocker.CancelWhenPruneIsAttempted = dying;

        var run = () => Service().PruneDanglingImagesAsync(dying.Token);

        await run.Should().ThrowAsync<OperationCanceledException>(
            "a shutdown mid-prune is the host stopping, not a machine that failed");
        (_panel.CountOf(nameof(IDockerEngine.PruneDanglingImagesAsync))
         + secondDocker.CountOf(nameof(IDockerEngine.PruneDanglingImagesAsync)))
            .Should().Be(1, "the run stopped at the first machine; the other was never asked");
    }

    [Fact]
    public async Task An_app_carrying_no_real_server_still_gets_its_local_machine_pruned()
    {
        // No Server row at all — only an app that predates servers and carries Guid.Empty. The factory
        // answers for it with the local machine, and the sweep must visit it exactly as the other two do.
        await AddAppAsync(Guid.Empty, "legacy");
        _panel.SeedDangling(2, 10);

        var result = await Service().PruneDanglingImagesAsync(default);

        result.Servers.Should().ContainSingle();
        result.Removed.Should().Be(2);
    }

    public void Dispose() => _db.Dispose();
}
