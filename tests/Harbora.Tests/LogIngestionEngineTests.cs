using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Logging;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Logging;
using Harbora.Infrastructure.Nodes;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 2.2 (2026-09 log-retention plan): the seam that gives search a history surviving the container.
///
/// <para>
/// The flagship scenario — "a line ingested before a container is replaced is still searchable
/// after" — is <see cref="A_line_ingested_before_the_container_is_replaced_is_still_searchable_after"/>:
/// the whole reason this sub-project exists is that today's search only ever reaches a fetched
/// container tail, and that tail is destroyed the moment a container is replaced (every deploy, every
/// crash-restart that recreates rather than restarts in place). This proves the persisted store
/// answers a search the live tail alone no longer can.
/// </para>
/// </summary>
public class LogIngestionEngineTests
{
    private static readonly Guid Workspace = Guid.NewGuid();
    private static readonly Guid ServerA = Guid.NewGuid();

    private static string Stamp(DateTimeOffset when) =>
        when.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff") + "Z";

    private sealed record Fixture(
        HarboraDbContext Db, LogIngestionEngine Engine, AppOperationsService Ops, FakeDockerEngine Docker,
        FakeServerEngineFactory Factory, FixedClock Clock, Guid AppId);

    private static Fixture NewFixture(int logRetentionDays = 7, long maxBytesPerApp = 50 * 1024 * 1024)
    {
        var db = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>()
                .UseInMemoryDatabase("log-ingestion-" + Guid.NewGuid()).Options,
            SystemWorkspaceScope.Instance);

        var appId = Guid.NewGuid();
        db.Apps.Add(new App
        {
            Id = appId, WorkspaceId = Workspace, ServerId = ServerA, EnvironmentId = Guid.NewGuid(),
            Name = "API", Slug = "api", Status = AppStatus.Running, LogRetentionDays = logRetentionDays,
            LogRetentionEnabledAt = logRetentionDays > 0 ? DateTimeOffset.UtcNow.AddDays(-365) : null
        });
        db.SaveChanges();

        var docker = new FakeDockerEngine();
        var factory = new FakeServerEngineFactory(docker);
        var clock = new FixedClock(DateTimeOffset.UtcNow);

        var engine = new LogIngestionEngine(
            db, factory, Options.Create(new LogIngestionOptions { MaxBytesPerApp = maxBytesPerApp }),
            clock, NullLogger<LogIngestionEngine>.Instance);

        var ingress = new NodeIngressRegistry(
            Options.Create(new NodeAgentControlPlaneOptions()), NullLogger<NodeIngressRegistry>.Instance);
        var ops = new AppOperationsService(
            db, factory, new RecordingProxyEngine(() => []),
            new BillingGate(db, Options.Create(new BillingOptions())),
            new HostPortAllocator(db, ingress, NullLogger<HostPortAllocator>.Instance),
            NullLogger<AppOperationsService>.Instance,
            clock: clock, events: null, runtimeOptions: null,
            logIngestionOptions: Options.Create(new LogIngestionOptions()));

        return new Fixture(db, engine, ops, docker, factory, clock, appId);
    }

    // ---- the flagship scenario ----

    [Fact]
    public async Task A_line_ingested_before_the_container_is_replaced_is_still_searchable_after()
    {
        var f = NewFixture();
        var now = f.Clock.UtcNow;
        var containerA = f.Docker.SeedContainer("harbora-api-1", "api", workspaceId: Workspace);
        f.Docker.ContainerLogsById[containerA] =
            $"{Stamp(now.AddMinutes(-2))} starting up\n" +
            $"{Stamp(now.AddMinutes(-1))} ERROR out of memory, exiting";

        var outcome = await f.Engine.IngestAsync(f.AppId, default);
        outcome.Status.Should().Be(LogIngestionStatus.Ingested);
        outcome.LinesIngested.Should().Be(2);

        // The container is replaced: removed, and a fresh one takes over with none of the old
        // container's history — exactly what a redeploy or a recreate-on-crash does in production.
        await f.Docker.RemoveContainerAsync(containerA, force: true, default);
        var containerB = f.Docker.SeedContainer("harbora-api-2", "api", workspaceId: Workspace);
        f.Docker.ContainerLogsById[containerB] = $"{Stamp(now)} fresh start, nothing about the crash";

        var result = await f.Ops.SearchLogsAsync([f.AppId], "out of memory", false, null, 200, default);

        result.Hits.Should().ContainSingle(
            "the persisted store answers this even though the container that wrote the line is gone")
            .Which.Line.Should().Contain("out of memory");
        var coverage = result.Coverage.Should().ContainSingle().Which;
        coverage.Reached.Should().BeTrue();
        coverage.RetentionEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task A_redeploys_cutover_flushes_the_retiring_containers_last_lines_before_removing_it()
    {
        // DeploymentPipeline.RetireOldContainersAsync's own best-effort flush — unlike
        // AppOperationsService.DeleteAsync (see that method's own remark), the app SURVIVES a
        // redeploy, so this is the one place a container's final lines are genuinely about to be
        // destroyed for an app somebody can still come back and search.
        using var h = new PipelineHarness();
        h.WithPreviousDeployment(number: 1);
        h.App.LogRetentionDays = 7;
        h.App.LogRetentionEnabledAt = h.Clock.UtcNow.AddDays(-365);
        h.Db.SaveChanges();

        var oldContainerId = (await h.Docker.ListContainersAsync(null, default))
            .Single(c => c.Name == h.ContainerFor(1)).Id;
        h.Docker.ContainerLogsById[oldContainerId] =
            $"{Stamp(h.Clock.UtcNow)} ERROR the old container's last words before cutover";

        h.LogIngestion = new LogIngestionEngine(
            h.Db, new FakeServerEngineFactory(h.Docker), Options.Create(new LogIngestionOptions()),
            h.Clock, NullLogger<LogIngestionEngine>.Instance);

        var deployment = h.QueueDeployment(number: 2);
        await h.RunAsync(deployment);

        var stored = await h.Db.AppLogLines.IgnoreQueryFilters()
            .Where(l => l.AppId == h.App.Id).ToListAsync();
        stored.Should().ContainSingle().Which.Text.Should().Contain("old container's last words");
    }

    // ---- ordinary ingestion behaviour ----

    [Fact]
    public async Task Disabled_retention_ingests_nothing()
    {
        var f = NewFixture(logRetentionDays: 0);
        var containerId = f.Docker.SeedContainer("harbora-api-1", "api", workspaceId: Workspace);
        f.Docker.ContainerLogsById[containerId] = $"{Stamp(f.Clock.UtcNow)} hello";

        var outcome = await f.Engine.IngestAsync(f.AppId, default);

        outcome.Status.Should().Be(LogIngestionStatus.Disabled);
        (await f.Db.AppLogLines.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task No_running_container_is_reported_rather_than_silently_ingesting_nothing()
    {
        var f = NewFixture();
        // No SeedContainer call.

        var outcome = await f.Engine.IngestAsync(f.AppId, default);

        outcome.Status.Should().Be(LogIngestionStatus.NoContainer);
    }

    [Fact]
    public async Task A_second_ingest_pass_does_not_re_store_lines_already_persisted()
    {
        var f = NewFixture();
        var now = f.Clock.UtcNow;
        var containerId = f.Docker.SeedContainer("harbora-api-1", "api", workspaceId: Workspace);
        f.Docker.ContainerLogsById[containerId] = $"{Stamp(now.AddMinutes(-1))} one line";

        var first = await f.Engine.IngestAsync(f.AppId, default);
        var second = await f.Engine.IngestAsync(f.AppId, default);

        first.LinesIngested.Should().Be(1);
        second.LinesIngested.Should().Be(0, "nothing new has appeared since the cursor advanced");
        (await f.Db.AppLogLines.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_later_ingest_pass_only_stores_lines_newer_than_the_cursor()
    {
        var f = NewFixture();
        var now = f.Clock.UtcNow;
        var containerId = f.Docker.SeedContainer("harbora-api-1", "api", workspaceId: Workspace);
        f.Docker.ContainerLogsById[containerId] = $"{Stamp(now.AddMinutes(-2))} first";

        await f.Engine.IngestAsync(f.AppId, default);

        f.Docker.ContainerLogsById[containerId] =
            $"{Stamp(now.AddMinutes(-2))} first\n{Stamp(now.AddMinutes(-1))} second";
        var second = await f.Engine.IngestAsync(f.AppId, default);

        second.LinesIngested.Should().Be(1);
        (await f.Db.AppLogLines.IgnoreQueryFilters().Select(l => l.Text).ToListAsync())
            .Should().BeEquivalentTo(["first", "second"]);
    }

    [Fact]
    public async Task A_host_that_cannot_attach_timestamps_reports_unsupported_rather_than_a_false_empty_pass()
    {
        var f = NewFixture();
        var containerId = f.Docker.SeedContainer("harbora-api-1", "api", workspaceId: Workspace);
        f.Docker.ContainerLogsById[containerId] = "cannot be timestamped";
        f.Docker.TimeWindowUnsupportedFor.Add(containerId);

        var outcome = await f.Engine.IngestAsync(f.AppId, default);

        outcome.Status.Should().Be(LogIngestionStatus.Unsupported);
        (await f.Db.AppLogLines.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_engine_that_cannot_be_reached_is_reported_rather_than_thrown()
    {
        var f = NewFixture();
        f.Factory.Unreachable(ServerA, "node is offline");

        var outcome = await f.Engine.IngestAsync(f.AppId, default);

        outcome.Status.Should().Be(LogIngestionStatus.EngineUnreachable);
    }

    [Fact]
    public async Task Ingesting_for_an_app_replaced_by_a_new_container_starts_fresh_at_the_new_containers_own_window()
    {
        // A different container id must not inherit the old container's cursor: the two logs are not
        // the same stream, and reusing the old cursor could skip the new container's own earliest
        // lines if that cursor were already ahead of them.
        var f = NewFixture();
        var now = f.Clock.UtcNow;
        var containerA = f.Docker.SeedContainer("harbora-api-1", "api", workspaceId: Workspace);
        f.Docker.ContainerLogsById[containerA] = $"{Stamp(now)} from A";
        await f.Engine.IngestAsync(f.AppId, default);

        await f.Docker.RemoveContainerAsync(containerA, force: true, default);
        var containerB = f.Docker.SeedContainer("harbora-api-2", "api", workspaceId: Workspace);
        // Older than A's own last line — would be skipped by a cursor carried over from A.
        f.Docker.ContainerLogsById[containerB] = $"{Stamp(now.AddMinutes(-10))} from B, earlier than A's cursor";

        var outcome = await f.Engine.IngestAsync(f.AppId, default);

        outcome.LinesIngested.Should().Be(1);
        (await f.Db.AppLogLines.IgnoreQueryFilters().Select(l => l.Text).ToListAsync())
            .Should().Contain(t => t.Contains("from B"));
    }
}
