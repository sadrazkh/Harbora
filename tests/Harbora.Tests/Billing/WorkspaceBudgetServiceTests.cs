using FluentAssertions;
using Harbora.Domain.Billing;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Billing;
using Harbora.Tests.Fakes;
using Xunit;

namespace Harbora.Tests.Billing;

public sealed class WorkspaceBudgetServiceTests
{
    [Fact]
    public async Task Spend_uses_only_cost_lines_inside_the_utc_calendar_month()
    {
        await using var db = Harness.SystemContext();
        var workspace = new Workspace
        {
            Name = "Budgeted", Slug = Guid.NewGuid().ToString("N"),
            MonthlyBudgetMinor = 250, MonthlySpendLimitMinor = 500
        };
        db.Workspaces.Add(workspace);
        db.BillingLedger.AddRange(
            Line(workspace.Id, new DateTimeOffset(2026, 7, 31, 23, 0, 0, TimeSpan.Zero), LedgerKind.Charge, -900),
            Line(workspace.Id, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), LedgerKind.Charge, -200),
            Line(workspace.Id, new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero), LedgerKind.PlanMinimumTopUp, -50),
            Line(workspace.Id, new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero), LedgerKind.Adjustment, -100));
        await db.SaveChangesAsync();

        var state = await new WorkspaceBudgetService(db).GetAsync(
            workspace.Id, new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero), default);

        state.SpendMinor.Should().Be(250);
        state.BudgetExceeded.Should().BeTrue();
        state.RemainingMinor.Should().Be(250);
        (await new WorkspaceBudgetService(db).CanSpendAsync(workspace.Id, 250,
            new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero), default)).Should().BeTrue();
        (await new WorkspaceBudgetService(db).CanSpendAsync(workspace.Id, 251,
            new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero), default)).Should().BeFalse();
    }

    private static BillingLedgerEntry Line(Guid workspaceId, DateTimeOffset hour, LedgerKind kind, long amount) => new()
    {
        WorkspaceId = workspaceId, BillingHour = hour, Kind = kind, AmountMinor = amount,
        ResourceType = BilledResourceType.App, ResourceId = Guid.NewGuid()
    };
}
