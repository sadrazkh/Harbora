using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Common;
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
}
