using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Functions;
using Harbora.Domain.Notifications;
using Harbora.Infrastructure.Dashboard;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <c>AttentionService.BuildAsync</c> actually reading the tables <see cref="AttentionRulesTests"/>
/// only exercises through the pure rule (P6, 2026-08-20 platform-options plan). Complements that file
/// the same way <c>AlertManagementHttpTests</c> complements <c>AlertManagementTests</c>: the rule's
/// own doc proves the rendering decision; this proves the query behind it is real.
/// </summary>
public class AttentionServiceTests
{
    private static readonly Guid Workspace = Guid.CreateVersion7();
    private static readonly Guid OtherWorkspace = Guid.CreateVersion7();

    private static AttentionService NewService(HarboraDbContext db) =>
        new(db, new FixedClock(), Options.Create(new Harbora.Infrastructure.Monitoring.MonitoringOptions()));

    [Fact]
    public async Task A_failing_event_subscription_reaches_the_dashboards_attention_block()
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("attention-events-" + Guid.NewGuid()).Options);
        db.EventSubscriptions.Add(new EventSubscription
        {
            WorkspaceId = Workspace, Name = "billing-webhook", Channel = AlertChannel.Webhook,
            EncryptedTarget = "{}", Events = EventKind.DeploymentFailed, IsEnabled = true,
            LastError = "The webhook returned 500 Internal Server Error", LastAttemptAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var items = await NewService(db).BuildAsync(Workspace, default);

        var item = items.Should().ContainSingle().Subject;
        item.TitleArgs.Should().Contain("billing-webhook");
        item.DetailKey.Should().Be(AttentionRules.ChannelEventDetail);
        item.ActionUrl.Should().Be("/notifications/webhooks");
    }

    [Fact]
    public async Task A_disabled_subscriptions_own_error_does_not_reach_the_dashboard()
    {
        // Alert follows the same rule (a.IsEnabled) — a rule someone deliberately turned off is not
        // "broken", it is off, and nagging about it would train people to ignore this block.
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("attention-events-" + Guid.NewGuid()).Options);
        db.EventSubscriptions.Add(new EventSubscription
        {
            WorkspaceId = Workspace, Name = "off-hook", Channel = AlertChannel.Webhook,
            EncryptedTarget = "{}", Events = EventKind.DeploymentFailed, IsEnabled = false,
            LastError = "The webhook returned 500 Internal Server Error"
        });
        await db.SaveChangesAsync();

        var items = await NewService(db).BuildAsync(Workspace, default);

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task Another_workspaces_failing_subscription_never_appears()
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("attention-events-" + Guid.NewGuid()).Options);
        db.EventSubscriptions.Add(new EventSubscription
        {
            WorkspaceId = OtherWorkspace, Name = "not-mine", Channel = AlertChannel.Webhook,
            EncryptedTarget = "{}", Events = EventKind.DeploymentFailed, IsEnabled = true,
            LastError = "500"
        });
        await db.SaveChangesAsync();

        var items = await NewService(db).BuildAsync(Workspace, default);

        items.Should().BeEmpty();
    }

    // ------------------------------------------- F4: repeated function failures on the dashboard

    private static HarboraDbContext NewDb() => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("attention-functions-" + Guid.NewGuid()).Options);

    private static (App App, FunctionDefinition Function) SeedFunction(HarboraDbContext db, Guid workspaceId)
    {
        var app = new App
        {
            WorkspaceId = workspaceId, Name = "reports", Slug = "reports-" + Guid.NewGuid().ToString("N")[..8],
            SourceType = AppSourceType.InlineCode
        };
        var fn = new FunctionDefinition
        {
            AppId = app.Id, WorkspaceId = workspaceId, Name = "nightly-report", Slug = "nightly-report",
            Trigger = FunctionTrigger.Cron, Code = "export default async () => {}"
        };
        db.Apps.Add(app);
        db.FunctionDefinitions.Add(fn);
        return (app, fn);
    }

    private static FunctionInvocation Invocation(
        Guid workspaceId, Guid appId, Guid functionId, bool succeeded, DateTimeOffset startedAt, string? error = null) =>
        new()
        {
            WorkspaceId = workspaceId, AppId = appId, FunctionId = functionId, Trigger = FunctionTrigger.Cron,
            StartedAt = startedAt, CompletedAt = startedAt.AddSeconds(1), Succeeded = succeeded, Error = error
        };

    /// <summary>
    /// "Repeated" per AttentionFacts.RepeatedFunctionFailures' own doc: the two most recent completed
    /// runs both failed. Proves the real query, not just the rule that renders its result.
    /// </summary>
    [Fact]
    public async Task Two_failures_in_a_row_reach_the_dashboard()
    {
        var db = NewDb();
        var (app, fn) = SeedFunction(db, Workspace);
        var now = DateTimeOffset.UtcNow;
        db.FunctionInvocations.AddRange(
            Invocation(Workspace, app.Id, fn.Id, succeeded: false, now.AddMinutes(-2), "Could not reach the function app."),
            Invocation(Workspace, app.Id, fn.Id, succeeded: false, now.AddMinutes(-1), "Could not reach the function app."));
        await db.SaveChangesAsync();

        var items = await NewService(db).BuildAsync(Workspace, default);

        var item = items.Should().ContainSingle(i => i.TitleKey == AttentionRules.FunctionFailedTitle).Subject;
        item.TitleArgs.Should().Equal("nightly-report", "reports");
        item.ActionUrl.Should().Be($"/functions/{app.Id}/{fn.Id}");
    }

    [Fact]
    public async Task A_single_failure_is_not_repeated_yet()
    {
        // One failure is exactly the "next scheduled run quietly clears it" blip this dashboard's own
        // rule treats as decoration, not attention — EventKind.FunctionFailed already told anyone
        // subscribed about this one run.
        var db = NewDb();
        var (app, fn) = SeedFunction(db, Workspace);
        db.FunctionInvocations.Add(
            Invocation(Workspace, app.Id, fn.Id, succeeded: false, DateTimeOffset.UtcNow, "boom"));
        await db.SaveChangesAsync();

        var items = await NewService(db).BuildAsync(Workspace, default);

        items.Should().NotContain(i => i.TitleKey == AttentionRules.FunctionFailedTitle);
    }

    [Fact]
    public async Task A_failure_the_next_run_already_cleared_does_not_reach_the_dashboard()
    {
        var db = NewDb();
        var (app, fn) = SeedFunction(db, Workspace);
        var now = DateTimeOffset.UtcNow;
        db.FunctionInvocations.AddRange(
            Invocation(Workspace, app.Id, fn.Id, succeeded: false, now.AddMinutes(-2), "boom"),
            Invocation(Workspace, app.Id, fn.Id, succeeded: true, now.AddMinutes(-1)));
        await db.SaveChangesAsync();

        var items = await NewService(db).BuildAsync(Workspace, default);

        items.Should().NotContain(i => i.TitleKey == AttentionRules.FunctionFailedTitle,
            "the most recent run succeeded, so this is history, not attention");
    }

    [Fact]
    public async Task A_success_followed_by_two_failures_still_counts_as_repeated()
    {
        var db = NewDb();
        var (app, fn) = SeedFunction(db, Workspace);
        var now = DateTimeOffset.UtcNow;
        db.FunctionInvocations.AddRange(
            Invocation(Workspace, app.Id, fn.Id, succeeded: true, now.AddMinutes(-3)),
            Invocation(Workspace, app.Id, fn.Id, succeeded: false, now.AddMinutes(-2), "boom"),
            Invocation(Workspace, app.Id, fn.Id, succeeded: false, now.AddMinutes(-1), "boom"));
        await db.SaveChangesAsync();

        var items = await NewService(db).BuildAsync(Workspace, default);

        items.Should().ContainSingle(i => i.TitleKey == AttentionRules.FunctionFailedTitle);
    }

    [Fact]
    public async Task Another_workspaces_repeated_function_failure_never_appears()
    {
        var db = NewDb();
        var (app, fn) = SeedFunction(db, OtherWorkspace);
        var now = DateTimeOffset.UtcNow;
        db.FunctionInvocations.AddRange(
            Invocation(OtherWorkspace, app.Id, fn.Id, succeeded: false, now.AddMinutes(-2), "not yours"),
            Invocation(OtherWorkspace, app.Id, fn.Id, succeeded: false, now.AddMinutes(-1), "not yours"));
        await db.SaveChangesAsync();

        var items = await NewService(db).BuildAsync(Workspace, default);

        items.Should().NotContain(i => i.TitleKey == AttentionRules.FunctionFailedTitle);
    }

    /// <summary>An invocation still queued or running (no CompletedAt) is neither a success nor a
    /// failure — it must not be read as either half of a "two in a row" streak.</summary>
    [Fact]
    public async Task A_still_running_invocation_does_not_count_toward_the_streak()
    {
        var db = NewDb();
        var (app, fn) = SeedFunction(db, Workspace);
        var now = DateTimeOffset.UtcNow;
        db.FunctionInvocations.AddRange(
            Invocation(Workspace, app.Id, fn.Id, succeeded: false, now.AddMinutes(-2), "boom"),
            new FunctionInvocation
            {
                WorkspaceId = Workspace, AppId = app.Id, FunctionId = fn.Id, Trigger = FunctionTrigger.Cron,
                StartedAt = now.AddMinutes(-1), CompletedAt = null
            });
        await db.SaveChangesAsync();

        var items = await NewService(db).BuildAsync(Workspace, default);

        items.Should().NotContain(i => i.TitleKey == AttentionRules.FunctionFailedTitle,
            "the only completed run is a single failure, not a repeated one");
    }
}
