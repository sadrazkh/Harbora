using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Logging;
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
/// 2.2 (2026-09 log-retention plan): <c>AppOperationsService.SearchLogsAsync</c>'s persisted-store half
/// — merged in alongside the live tail rather than instead of it — and the extended
/// <c>AppLogCoverage</c> fields that say how far back a search actually reached and whether the disk
/// budget, not the configured day count, is why it does not reach further. <see cref="LogSearchTests"/>
/// covers the unchanged live-tail-only behaviour for apps that never turned retention on.
/// </summary>
public class LogSearchPersistedHistoryTests
{
    private static readonly Guid Workspace = Guid.NewGuid();
    private static readonly Guid ServerA = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(HarboraDbContext Db, AppOperationsService Ops, FakeDockerEngine Docker, FakeServerEngineFactory Factory);

    private static Fixture NewFixture()
    {
        var db = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>()
                .UseInMemoryDatabase("log-search-persisted-" + Guid.NewGuid()).Options,
            SystemWorkspaceScope.Instance);

        var docker = new FakeDockerEngine();
        var factory = new FakeServerEngineFactory(docker);
        var ingress = new NodeIngressRegistry(
            Options.Create(new NodeAgentControlPlaneOptions()), NullLogger<NodeIngressRegistry>.Instance);

        var ops = new AppOperationsService(
            db, factory, new RecordingProxyEngine(() => []),
            new BillingGate(db, Options.Create(new BillingOptions())),
            new HostPortAllocator(db, ingress, NullLogger<HostPortAllocator>.Instance),
            NullLogger<AppOperationsService>.Instance);

        return new Fixture(db, ops, docker, factory);
    }

    private static Guid SeedApp(Fixture f, int retentionDays, bool budgetCapped = false)
    {
        var appId = Guid.NewGuid();
        f.Db.Apps.Add(new App
        {
            Id = appId, WorkspaceId = Workspace, ServerId = ServerA, EnvironmentId = Guid.NewGuid(),
            Name = "API", Slug = "api", Status = AppStatus.Running,
            LogRetentionDays = retentionDays, LogRetentionEnabledAt = Now.AddDays(-365),
            LogRetentionBudgetCapped = budgetCapped
        });
        f.Db.SaveChanges();
        return appId;
    }

    private static void SeedLine(Fixture f, Guid appId, DateTimeOffset when, string text) =>
        f.Db.AppLogLines.Add(new AppLogLine
        {
            Id = Guid.NewGuid(), WorkspaceId = Workspace, AppId = appId, ContainerId = "persisted",
            Timestamp = when, Text = text, SizeBytes = text.Length
        });

    [Fact]
    public async Task A_search_finds_a_persisted_line_the_live_container_no_longer_has()
    {
        var f = NewFixture();
        var appId = SeedApp(f, retentionDays: 30);
        SeedLine(f, appId, Now.AddDays(-10), "ERROR from a container that is long gone");
        f.Db.SaveChanges();
        // No running container for this app — the live half of SearchLogsAsync finds nothing at all.

        var result = await f.Ops.SearchLogsAsync([appId], "long gone", false, null, 200, default);

        result.Hits.Should().ContainSingle().Which.Line.Should().Contain("long gone");
        var coverage = result.Coverage.Should().ContainSingle().Which;
        coverage.Reached.Should().BeTrue("the persisted store answered even though nothing is running");
        coverage.RetentionEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Coverage_reports_how_far_back_the_search_actually_reached()
    {
        var f = NewFixture();
        var appId = SeedApp(f, retentionDays: 30);
        SeedLine(f, appId, Now.AddDays(-20), "oldest");
        SeedLine(f, appId, Now.AddDays(-2), "newest");
        f.Db.SaveChanges();

        var result = await f.Ops.SearchLogsAsync([appId], null, false, null, 200, default);

        result.Coverage.Should().ContainSingle().Which.ReachedBackTo.Should().Be(Now.AddDays(-20),
            "the earliest row this search actually looked at, not a guess");
    }

    [Fact]
    public async Task Coverage_says_when_the_disk_budget_not_the_configured_days_is_cutting_history_short()
    {
        var f = NewFixture();
        var appId = SeedApp(f, retentionDays: 30, budgetCapped: true);
        SeedLine(f, appId, Now.AddDays(-5), "budget trimmed everything before this");
        f.Db.SaveChanges();

        var result = await f.Ops.SearchLogsAsync([appId], null, false, null, 200, default);

        result.Coverage.Should().ContainSingle().Which.BudgetCapped.Should().BeTrue();
    }

    [Fact]
    public async Task An_app_that_never_turned_retention_on_reports_no_budget_capping_and_no_persisted_reach()
    {
        var f = NewFixture();
        var appId = SeedApp(f, retentionDays: 0);
        f.Db.SaveChanges();
        // No container running, retention off: nothing anywhere for this search to find.

        var result = await f.Ops.SearchLogsAsync([appId], null, false, null, 200, default);

        var coverage = result.Coverage.Should().ContainSingle().Which;
        coverage.RetentionEnabled.Should().BeFalse();
        coverage.BudgetCapped.Should().BeFalse();
        coverage.ReachedBackTo.Should().BeNull();
        coverage.Reached.Should().BeFalse("no container is running and there is no persisted history to fall back on");
    }

    [Fact]
    public async Task A_persisted_line_outside_the_requested_time_window_is_excluded()
    {
        var f = NewFixture();
        var appId = SeedApp(f, retentionDays: 30);
        SeedLine(f, appId, Now.AddDays(-10), "outside the window");
        SeedLine(f, appId, Now.AddHours(-1), "inside the window");
        f.Db.SaveChanges();

        var result = await f.Ops.SearchLogsAsync([appId], null, false, TimeSpan.FromHours(6), 200, default);

        result.Hits.Should().ContainSingle().Which.Line.Should().Be("inside the window");
    }

    [Fact]
    public async Task Live_and_persisted_hits_both_contribute_to_one_search()
    {
        var f = NewFixture();
        var appId = SeedApp(f, retentionDays: 30);
        SeedLine(f, appId, Now.AddDays(-10), "ERROR from persisted history");
        f.Db.SaveChanges();
        var containerId = f.Docker.SeedContainer("harbora-api-1", "api", workspaceId: Workspace);
        f.Docker.ContainerLogsById[containerId] = "ERROR from the live container right now";

        var result = await f.Ops.SearchLogsAsync([appId], "ERROR", false, null, 200, default);

        result.Hits.Should().HaveCount(2);
        result.Hits.Select(h => h.Line).Should().Contain(l => l.Contains("persisted history"))
            .And.Contain(l => l.Contains("live container"));
    }
}
