using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Logging;
using Harbora.Infrastructure.Maintenance;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 2.2 (2026-09 log-retention plan): the nightly age-based half of persisted log retention — each
/// app's own <c>LogRetentionDays</c>, which is why this cannot go through <c>DataRetentionSweeper</c>'s
/// single-shared-cutoff <c>SweepAgedTableAsync</c> the way every other table it owns does.
///
/// <para>
/// The tenancy pair below is the platform's own standing trap, restated for this table: a sweep with
/// no session that reads through the wrong lens finds nothing and reports a clean pass. One test
/// proves the sweep reaches a workspace that is not the ambient scope's own; the other proves it does
/// not reach further than it should — a different workspace's still-valid rows are left alone.
/// </para>
/// </summary>
public class AppLogRetentionSweepTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 3, 0, 0, TimeSpan.Zero);

    private static DataRetentionSweeper NewSweeper(HarboraDbContext db)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        var provider = services.BuildServiceProvider();

        return new DataRetentionSweeper(
            provider.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            Options.Create(new RetentionOptions()),
            new FixedClock(Now),
            NullLogger<DataRetentionSweeper>.Instance);
    }

    private static App NewApp(Guid workspaceId, int retentionDays) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = workspaceId, ServerId = Guid.NewGuid(),
        EnvironmentId = Guid.NewGuid(), Name = "api", Slug = "api-" + Guid.NewGuid().ToString("N")[..8],
        Status = AppStatus.Running, LogRetentionDays = retentionDays, LogRetentionEnabledAt = Now.AddDays(-365)
    };

    private static AppLogLine Line(App app, DateTimeOffset when, string text) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = app.WorkspaceId, AppId = app.Id, ContainerId = "c1",
        Timestamp = when, Text = text, SizeBytes = text.Length
    };

    [Fact]
    public async Task Sweep_deletes_an_apps_lines_past_its_own_configured_days_and_keeps_the_rest()
    {
        using var db = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase("app-log-sweep-" + Guid.NewGuid()).Options,
            SystemWorkspaceScope.Instance);

        var app = NewApp(Guid.NewGuid(), retentionDays: 7);
        db.Apps.Add(app);
        db.AppLogLines.AddRange(
            Line(app, Now.AddDays(-8), "past retention"),
            Line(app, Now.AddDays(-6), "within retention"));
        await db.SaveChangesAsync();

        var result = await NewSweeper(db).SweepAsync(CancellationToken.None);

        result.Failures.Should().BeEmpty();
        result.Deleted[RetentionTables.AppLogLines].Should().Be(1);
        var remaining = await db.AppLogLines.IgnoreQueryFilters().Select(l => l.Text).ToListAsync();
        remaining.Should().BeEquivalentTo(["within retention"]);
    }

    [Fact]
    public async Task Sweep_uses_each_apps_own_day_count_not_one_shared_cutoff()
    {
        using var db = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase("app-log-sweep-" + Guid.NewGuid()).Options,
            SystemWorkspaceScope.Instance);

        // Both lines are 10 days old. Only the app configured to keep 7 days should lose its line;
        // the app configured to keep 30 days must keep the same-age line untouched — proof there is
        // no single shared cutoff the way every other table in DataRetentionSweeper has one.
        var shortRetention = NewApp(Guid.NewGuid(), retentionDays: 7);
        var longRetention = NewApp(Guid.NewGuid(), retentionDays: 30);
        db.Apps.AddRange(shortRetention, longRetention);
        db.AppLogLines.AddRange(
            Line(shortRetention, Now.AddDays(-10), "should go"),
            Line(longRetention, Now.AddDays(-10), "should stay"));
        await db.SaveChangesAsync();

        await NewSweeper(db).SweepAsync(CancellationToken.None);

        (await db.AppLogLines.IgnoreQueryFilters().Where(l => l.AppId == shortRetention.Id).CountAsync())
            .Should().Be(0);
        (await db.AppLogLines.IgnoreQueryFilters().Where(l => l.AppId == longRetention.Id).CountAsync())
            .Should().Be(1);
    }

    [Fact]
    public async Task Sweep_never_touches_an_app_with_retention_turned_off()
    {
        using var db = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase("app-log-sweep-" + Guid.NewGuid()).Options,
            SystemWorkspaceScope.Instance);

        var app = NewApp(Guid.NewGuid(), retentionDays: 0);
        db.Apps.Add(app);
        db.AppLogLines.Add(Line(app, Now.AddYears(-1), "ancient, but retention is off"));
        await db.SaveChangesAsync();

        var result = await NewSweeper(db).SweepAsync(CancellationToken.None);

        result.Deleted[RetentionTables.AppLogLines].Should().Be(0);
        (await db.AppLogLines.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    // ---- tenancy, both directions ----

    [Fact]
    public async Task Sweep_reaches_a_workspace_that_is_not_the_ambient_scopes_own()
    {
        // The trap this codebase has paid for repeatedly: a filtered read from a sessionless path
        // finds nothing, deletes nothing, and reports a clean pass. The sweep runs under an ambient
        // scope bound to a DIFFERENT workspace than the one whose app actually needs sweeping.
        var tenant = Guid.NewGuid();
        var someoneElse = new FixedWorkspaceScope(Guid.NewGuid());

        using var db = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase("app-log-sweep-" + Guid.NewGuid()).Options,
            someoneElse);

        var app = NewApp(tenant, retentionDays: 7);
        db.Apps.Add(app);
        db.AppLogLines.Add(Line(app, Now.AddDays(-8), "past retention, wrong ambient scope"));
        await db.SaveChangesAsync();

        var result = await NewSweeper(db).SweepAsync(CancellationToken.None);

        result.Deleted[RetentionTables.AppLogLines].Should().Be(1,
            "IgnoreQueryFilters plus the explicit per-app cutoff must reach this tenant despite the ambient scope");
        (await db.AppLogLines.IgnoreQueryFilters().Where(l => l.AppId == app.Id).CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Sweep_never_deletes_a_different_workspaces_still_valid_lines()
    {
        // The other direction: reaching every tenant must not become reaching too FAR — a workspace
        // whose own app is not yet past ITS cutoff must come out untouched, even while a sibling
        // workspace's expired rows are being removed in the same pass.
        var expiring = Guid.NewGuid();
        var untouched = Guid.NewGuid();

        using var db = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase("app-log-sweep-" + Guid.NewGuid()).Options,
            SystemWorkspaceScope.Instance);

        var expiringApp = NewApp(expiring, retentionDays: 7);
        var untouchedApp = NewApp(untouched, retentionDays: 7);
        db.Apps.AddRange(expiringApp, untouchedApp);
        db.AppLogLines.AddRange(
            Line(expiringApp, Now.AddDays(-8), "past retention"),
            Line(untouchedApp, Now.AddDays(-1), "well within retention"));
        await db.SaveChangesAsync();

        await NewSweeper(db).SweepAsync(CancellationToken.None);

        (await db.AppLogLines.IgnoreQueryFilters().Where(l => l.AppId == expiringApp.Id).CountAsync())
            .Should().Be(0);
        (await db.AppLogLines.IgnoreQueryFilters().Where(l => l.AppId == untouchedApp.Id).CountAsync())
            .Should().Be(1, "a sibling workspace's still-valid row must never be caught by another workspace's sweep");
    }
}
