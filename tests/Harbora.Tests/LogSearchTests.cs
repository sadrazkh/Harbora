using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Nodes;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Searching a fetched container tail — every app here has persisted retention off
/// (<c>App.LogRetentionDays</c> defaults to 0), so <c>SearchLogsAsync</c>'s persisted-store half never
/// runs and behaviour is exactly what it was before 2.2 (2026-09 log-retention plan) added one.
/// <see cref="LogIngestionEngineTests"/> and <see cref="LogSearchPersistedHistoryTests"/> cover what
/// changes once an app turns retention on.
///
/// <see cref="LogFilterTests"/> pins the matching rule itself; this pins the wiring around it —
/// coverage honesty, per-app isolation, and the time-window fallback.
/// </summary>
public class LogSearchTests
{
    private static readonly Guid Workspace = Guid.NewGuid();
    private static readonly Guid ServerA = Guid.NewGuid();

    private sealed record Fixture(HarboraDbContext Db, AppOperationsService Ops, FakeDockerEngine Docker);

    private static Fixture NewFixture()
    {
        var db = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>()
                .UseInMemoryDatabase("log-search-" + Guid.NewGuid()).Options,
            SystemWorkspaceScope.Instance);

        var docker = new FakeDockerEngine();
        var ingress = new NodeIngressRegistry(
            Options.Create(new NodeAgentControlPlaneOptions()), NullLogger<NodeIngressRegistry>.Instance);

        var ops = new AppOperationsService(
            db,
            new FakeServerEngineFactory(docker),
            new RecordingProxyEngine(() => []),
            new BillingGate(db, Options.Create(new BillingOptions())),
            new HostPortAllocator(db, ingress, NullLogger<HostPortAllocator>.Instance),
            NullLogger<AppOperationsService>.Instance);

        return new Fixture(db, ops, docker);
    }

    /// <summary>Seeds an app with a running, labelled container so ResolveAsync can find it.</summary>
    private static Guid SeedRunningApp(Fixture f, string name, string slug, out string containerId)
    {
        var appId = Guid.NewGuid();
        f.Db.Apps.Add(new App
        {
            Id = appId, WorkspaceId = Workspace, ServerId = ServerA, EnvironmentId = Guid.NewGuid(),
            Name = name, Slug = slug, Status = AppStatus.Running
        });
        f.Db.SaveChanges();

        containerId = f.Docker.SeedContainer($"harbora-{slug}-1", slug, workspaceId: Workspace);
        return appId;
    }

    // ---- one app ----

    [Fact]
    public async Task Searching_one_apps_logs_returns_matching_lines_tagged_with_that_app()
    {
        var f = NewFixture();
        var appId = SeedRunningApp(f, "API", "api", out var containerId);
        f.Docker.ContainerLogsById[containerId] = "Starting up\nERROR could not connect\nListening on 8080";

        var result = await f.Ops.SearchLogsAsync([appId], "error", problemsOnly: false, window: null, 200, default);

        result.Hits.Should().ContainSingle();
        result.Hits[0].AppId.Should().Be(appId);
        result.Hits[0].AppName.Should().Be("API");
        result.Hits[0].Line.Should().Contain("could not connect");
    }

    [Fact]
    public async Task A_search_that_matches_nothing_still_reports_how_much_it_scanned()
    {
        var f = NewFixture();
        var appId = SeedRunningApp(f, "API", "api", out var containerId);
        f.Docker.ContainerLogsById[containerId] = "one\ntwo\nthree";

        var result = await f.Ops.SearchLogsAsync([appId], "nowhere-to-be-found", false, null, 200, default);

        result.Hits.Should().BeEmpty();
        var coverage = result.Coverage.Should().ContainSingle().Which;
        coverage.Reached.Should().BeTrue();
        coverage.LinesScanned.Should().Be(3, "a caller must be able to tell 'searched 3 lines, found nothing' from 'searched nothing'");
    }

    [Fact]
    public async Task An_app_with_no_running_container_is_reported_unreached_rather_than_silently_skipped()
    {
        var f = NewFixture();
        var appId = Guid.NewGuid();
        f.Db.Apps.Add(new App
        {
            Id = appId, WorkspaceId = Workspace, ServerId = ServerA, EnvironmentId = Guid.NewGuid(),
            Name = "idle", Slug = "idle", Status = AppStatus.Stopped
        });
        f.Db.SaveChanges();
        // No SeedContainer call: nothing running for this app.

        var result = await f.Ops.SearchLogsAsync([appId], null, false, null, 200, default);

        result.Hits.Should().BeEmpty();
        var coverage = result.Coverage.Should().ContainSingle().Which;
        coverage.Reached.Should().BeFalse();
        coverage.UnavailableReason.Should().NotBeNullOrWhiteSpace();
        coverage.LinesScanned.Should().Be(0);
    }

    [Fact]
    public async Task An_engine_that_cannot_be_reached_does_not_abort_the_other_apps_search()
    {
        var f = NewFixture();
        var reachableApp = SeedRunningApp(f, "API", "api", out var reachableContainer);
        f.Docker.ContainerLogsById[reachableContainer] = "ERROR reachable app failed";

        var brokenServer = Guid.NewGuid();
        var brokenAppId = Guid.NewGuid();
        f.Db.Apps.Add(new App
        {
            Id = brokenAppId, WorkspaceId = Workspace, ServerId = brokenServer, EnvironmentId = Guid.NewGuid(),
            Name = "worker", Slug = "worker", Status = AppStatus.Running
        });
        f.Db.SaveChanges();

        var factory = new FakeServerEngineFactory(f.Docker).Unreachable(brokenServer, "node is offline");
        var ops = new AppOperationsService(
            f.Db, factory, new RecordingProxyEngine(() => []),
            new BillingGate(f.Db, Options.Create(new BillingOptions())),
            new HostPortAllocator(f.Db,
                new NodeIngressRegistry(Options.Create(new NodeAgentControlPlaneOptions()), NullLogger<NodeIngressRegistry>.Instance),
                NullLogger<HostPortAllocator>.Instance),
            NullLogger<AppOperationsService>.Instance);

        var result = await ops.SearchLogsAsync([reachableApp, brokenAppId], "error", false, null, 200, default);

        result.Hits.Should().ContainSingle().Which.AppId.Should().Be(reachableApp,
            "the app on the offline node has nothing to contribute, but the reachable one still does");
        result.Coverage.Should().HaveCount(2);
        result.Coverage.Should().Contain(c => c.AppId == reachableApp && c.Reached);
        result.Coverage.Should().Contain(c => c.AppId == brokenAppId && !c.Reached);
    }

    // ---- across apps: isolation ----

    [Fact]
    public async Task One_apps_lines_never_appear_tagged_as_another_apps_hit()
    {
        var f = NewFixture();
        var apiId = SeedRunningApp(f, "API", "api", out var apiContainer);
        var workerId = SeedRunningApp(f, "Worker", "worker", out var workerContainer);
        f.Docker.ContainerLogsById[apiContainer] = "ERROR api exploded";
        f.Docker.ContainerLogsById[workerContainer] = "ERROR worker exploded";

        var result = await f.Ops.SearchLogsAsync([apiId, workerId], "exploded", false, null, 200, default);

        result.Hits.Should().HaveCount(2);
        result.Hits.Single(h => h.AppId == apiId).Line.Should().Contain("api exploded");
        result.Hits.Single(h => h.AppId == workerId).Line.Should().Contain("worker exploded");
    }

    // ---- time window ----

    private static string Stamp(DateTimeOffset when) =>
        when.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff") + "Z";

    [Fact]
    public async Task A_time_window_the_engine_can_honor_excludes_lines_from_before_it()
    {
        var f = NewFixture();
        var appId = SeedRunningApp(f, "API", "api", out var containerId);
        var now = DateTimeOffset.UtcNow;
        f.Docker.ContainerLogsById[containerId] =
            $"{Stamp(now.AddHours(-3))} ERROR ancient history\n" +
            $"{Stamp(now.AddMinutes(-5))} ERROR just now";

        var result = await f.Ops.SearchLogsAsync([appId], "error", false, TimeSpan.FromHours(1), 200, default);

        result.Hits.Should().ContainSingle().Which.Line.Should().Contain("just now");
        result.Coverage.Should().ContainSingle().Which.TimeWindowHonored.Should().BeTrue();
    }

    [Fact]
    public async Task A_host_that_cannot_attach_real_timestamps_still_returns_its_tail_and_says_the_window_was_not_honored()
    {
        var f = NewFixture();
        var appId = SeedRunningApp(f, "API", "api", out var containerId);
        f.Docker.ContainerLogsById[containerId] = "ERROR something old the engine cannot time-bound";
        f.Docker.TimeWindowUnsupportedFor.Add(containerId);

        var result = await f.Ops.SearchLogsAsync([appId], "error", false, TimeSpan.FromMinutes(15), 200, default);

        result.Hits.Should().ContainSingle("a host that cannot honor the window must not be treated as having no matches");
        var coverage = result.Coverage.Should().ContainSingle().Which;
        coverage.TimeWindowRequested.Should().BeTrue();
        coverage.TimeWindowHonored.Should().BeFalse();
    }

    [Fact]
    public async Task No_window_requested_means_nothing_is_reported_about_one_being_honored()
    {
        var f = NewFixture();
        var appId = SeedRunningApp(f, "API", "api", out var containerId);
        f.Docker.ContainerLogsById[containerId] = "hello";

        var result = await f.Ops.SearchLogsAsync([appId], null, false, null, 200, default);

        result.Coverage.Should().ContainSingle().Which.TimeWindowRequested.Should().BeFalse();
    }
}
