using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Logging;
using Harbora.Infrastructure.Logging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 2.2 (2026-09 log-retention plan): the disk budget — "disk is finite and this is the feature most
/// likely to fill it" — enforced by dropping the oldest lines first, per app and across the whole
/// platform, and the honest <c>App.LogRetentionBudgetCapped</c> signal that tells a budget-driven cut
/// short apart from an app simply not having produced that much history yet.
/// </summary>
public class LogBudgetEnforcerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 3, 0, 0, TimeSpan.Zero);

    private static HarboraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<HarboraDbContext>()
                .UseInMemoryDatabase("log-budget-" + Guid.NewGuid()).Options,
            SystemWorkspaceScope.Instance);

    private static App NewApp(int retentionDays = 30, DateTimeOffset? enabledAt = null) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), ServerId = Guid.NewGuid(),
        EnvironmentId = Guid.NewGuid(), Name = "api", Slug = "api-" + Guid.NewGuid().ToString("N")[..8],
        Status = AppStatus.Running, LogRetentionDays = retentionDays,
        LogRetentionEnabledAt = enabledAt ?? Now.AddDays(-365)
    };

    private static AppLogLine Line(App app, DateTimeOffset when, int bytes, string? text = null) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = app.WorkspaceId, AppId = app.Id, ContainerId = "c1",
        Timestamp = when, Text = text ?? new string('x', bytes), SizeBytes = bytes
    };

    // ---- per-app cap ----

    [Fact]
    public async Task EnforcePerAppAsync_drops_the_oldest_lines_first_until_under_the_cap()
    {
        using var db = NewDb();
        var app = NewApp();
        db.Apps.Add(app);
        db.AppLogLines.AddRange(
            Line(app, Now.AddMinutes(-30), 100, "oldest"),
            Line(app, Now.AddMinutes(-20), 100, "middle"),
            Line(app, Now.AddMinutes(-10), 100, "newest"));
        await db.SaveChangesAsync();

        var capped = await LogBudgetEnforcer.EnforcePerAppAsync(db, app.Id, maxBytesPerApp: 150, default);

        capped.Should().BeTrue();
        var remaining = await db.AppLogLines.IgnoreQueryFilters()
            .Where(l => l.AppId == app.Id).Select(l => l.Text).ToListAsync();
        remaining.Should().BeEquivalentTo(["newest"], "the two oldest lines had to go to fit under 150 bytes");
    }

    [Fact]
    public async Task EnforcePerAppAsync_does_nothing_when_already_under_the_cap()
    {
        using var db = NewDb();
        var app = NewApp();
        db.Apps.Add(app);
        db.AppLogLines.Add(Line(app, Now.AddMinutes(-1), 100, "only line"));
        await db.SaveChangesAsync();

        var capped = await LogBudgetEnforcer.EnforcePerAppAsync(db, app.Id, maxBytesPerApp: 10_000, default);

        capped.Should().BeFalse();
        (await db.AppLogLines.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task EnforcePerAppAsync_never_touches_a_different_apps_lines()
    {
        using var db = NewDb();
        var over = NewApp();
        var untouched = NewApp();
        db.Apps.AddRange(over, untouched);
        db.AppLogLines.AddRange(
            Line(over, Now.AddMinutes(-30), 200, "over-old"),
            Line(untouched, Now.AddMinutes(-30), 200, "untouched-old"));
        await db.SaveChangesAsync();

        await LogBudgetEnforcer.EnforcePerAppAsync(db, over.Id, maxBytesPerApp: 50, default);

        (await db.AppLogLines.IgnoreQueryFilters().Where(l => l.AppId == over.Id).CountAsync()).Should().Be(0);
        (await db.AppLogLines.IgnoreQueryFilters().Where(l => l.AppId == untouched.Id).CountAsync()).Should().Be(1);
    }

    // ---- global cap: oldest dropped first, regardless of which app wrote it ----

    [Fact]
    public async Task EnforceGlobalAsync_drops_the_globally_oldest_line_even_from_an_app_under_its_own_cap()
    {
        using var db = NewDb();
        var appA = NewApp();
        var appB = NewApp();
        db.Apps.AddRange(appA, appB);
        // appA's own line is the OLDEST in the whole store, even though appA alone is nowhere near
        // any per-app cap — the requirement's own words: "an overall cap, with the oldest dropped
        // first", not "each app's own oldest".
        db.AppLogLines.AddRange(
            Line(appA, Now.AddDays(-10), 100, "globally oldest, from A"),
            Line(appB, Now.AddDays(-5), 100, "newer, from B"));
        await db.SaveChangesAsync();

        var touched = await LogBudgetEnforcer.EnforceGlobalAsync(db, maxBytesTotal: 150, default);

        touched.Should().BeEquivalentTo([appA.Id]);
        var remaining = await db.AppLogLines.IgnoreQueryFilters().Select(l => l.Text).ToListAsync();
        remaining.Should().BeEquivalentTo(["newer, from B"]);
    }

    [Fact]
    public async Task EnforceGlobalAsync_does_nothing_when_the_whole_store_is_under_the_cap()
    {
        using var db = NewDb();
        var app = NewApp();
        db.Apps.Add(app);
        db.AppLogLines.Add(Line(app, Now, 100));
        await db.SaveChangesAsync();

        var touched = await LogBudgetEnforcer.EnforceGlobalAsync(db, maxBytesTotal: 10_000, default);

        touched.Should().BeEmpty();
        (await db.AppLogLines.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    // ---- App.LogRetentionBudgetCapped: set only on an explicit signal, cleared by self-healing ----

    [Fact]
    public async Task RecomputeBudgetCappedAsync_is_true_when_the_caller_says_the_budget_trimmed_this_pass()
    {
        using var db = NewDb();
        var app = NewApp(retentionDays: 30, enabledAt: Now.AddDays(-365));
        db.Apps.Add(app);
        await db.SaveChangesAsync();

        await LogBudgetEnforcer.RecomputeBudgetCappedAsync(db, app, Now, budgetTrimmedThisPass: true, default);

        app.LogRetentionBudgetCapped.Should().BeTrue();
    }

    [Fact]
    public async Task RecomputeBudgetCappedAsync_never_marks_an_app_capped_purely_for_not_having_enough_history_yet()
    {
        // Retention was turned on two days ago; the only line on hand is from right around then. A
        // 30-day configured window must not read this as "capped" purely from elapsed time — nothing
        // was ever trimmed, so the flag has to stay at its default. This is the false alarm the old,
        // purely time-inferred version of this method used to raise.
        using var db = NewDb();
        var app = NewApp(retentionDays: 30, enabledAt: Now.AddDays(-2));
        db.Apps.Add(app);
        db.AppLogLines.Add(Line(app, Now.AddDays(-2).AddMinutes(5), 100));
        await db.SaveChangesAsync();

        await LogBudgetEnforcer.RecomputeBudgetCappedAsync(db, app, Now, budgetTrimmedThisPass: false, default);

        app.LogRetentionBudgetCapped.Should().BeFalse();
    }

    [Fact]
    public async Task RecomputeBudgetCappedAsync_stays_true_until_the_full_configured_window_is_present_again()
    {
        using var db = NewDb();
        var app = NewApp(retentionDays: 7, enabledAt: Now.AddDays(-365));
        app.LogRetentionBudgetCapped = true; // an earlier pass trimmed something
        db.Apps.Add(app);
        // Oldest line does not yet reach the full 7-day window.
        db.AppLogLines.Add(Line(app, Now.AddDays(-3), 100));
        await db.SaveChangesAsync();

        await LogBudgetEnforcer.RecomputeBudgetCappedAsync(db, app, Now, budgetTrimmedThisPass: false, default);

        app.LogRetentionBudgetCapped.Should().BeTrue("the window has not caught back up yet");
    }

    [Fact]
    public async Task RecomputeBudgetCappedAsync_clears_itself_once_the_full_window_is_present_again()
    {
        using var db = NewDb();
        var app = NewApp(retentionDays: 7, enabledAt: Now.AddDays(-365));
        app.LogRetentionBudgetCapped = true; // an earlier pass trimmed something
        db.Apps.Add(app);
        // The oldest line now reaches the full 7-day window — nothing is missing any more.
        db.AppLogLines.Add(Line(app, Now.AddDays(-7).AddMinutes(-5), 100));
        await db.SaveChangesAsync();

        await LogBudgetEnforcer.RecomputeBudgetCappedAsync(db, app, Now, budgetTrimmedThisPass: false, default);

        app.LogRetentionBudgetCapped.Should().BeFalse("self-healing: nothing is missing from the configured window any more");
    }

    [Fact]
    public async Task RecomputeBudgetCappedAsync_leaves_an_already_false_flag_alone_without_touching_the_database()
    {
        using var db = NewDb();
        var app = NewApp(retentionDays: 30, enabledAt: Now.AddDays(-365));
        db.Apps.Add(app);
        // No AppLogLines seeded at all — if this queried the database it would still get the right
        // answer, but it must not need to for the ordinary "nothing changed" case.
        await db.SaveChangesAsync();

        await LogBudgetEnforcer.RecomputeBudgetCappedAsync(db, app, Now, budgetTrimmedThisPass: false, default);

        app.LogRetentionBudgetCapped.Should().BeFalse();
    }

    [Fact]
    public async Task RecomputeBudgetCappedAsync_is_always_false_when_retention_is_off()
    {
        using var db = NewDb();
        var app = NewApp(retentionDays: 0, enabledAt: null);
        app.LogRetentionBudgetCapped = true; // stale from before it was turned off
        db.Apps.Add(app);
        await db.SaveChangesAsync();

        await LogBudgetEnforcer.RecomputeBudgetCappedAsync(db, app, Now, budgetTrimmedThisPass: true, default);

        app.LogRetentionBudgetCapped.Should().BeFalse("retention being off overrides even a caller claiming a trim happened");
    }
}
