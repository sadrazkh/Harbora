using FluentAssertions;
using Harbora.Domain.Billing;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Billing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Billing;

public sealed class WalletAdjustmentTests
{
    [Fact]
    public async Task An_adjustment_appends_the_opposing_line_and_keeps_wallet_and_ledger_equal()
    {
        await using var db = WalletHarness.SystemContext();
        var workspace = WalletHarness.SeedWorkspace(db, balanceMinor: 1_000);
        db.BillingLedger.Add(WalletHarness.Line(
            workspace, WalletHarness.Hour, 1_000, LedgerKind.Credit,
            BilledResourceType.None, name: string.Empty, state: BilledRunState.NotApplicable, hours: 0));
        await db.SaveChangesAsync();
        var service = WalletHarness.Wallets(db, through: db);

        var result = await service.AdjustAsync(new AdjustmentRequest(
            Guid.CreateVersion7(), workspace, -250, "reverse duplicate credit", WalletHarness.Admin), default);
        var reconciliation = await service.ReconcileAsync(workspace, default);

        result.BalanceMinor.Should().Be(750);
        reconciliation.IsBalanced.Should().BeTrue();
        var line = await db.BillingLedger.SingleAsync(l => l.Kind == LedgerKind.Adjustment);
        line.AmountMinor.Should().Be(-250);
        line.ResourceName.Should().Be("Balance adjustment", "the customer bill must not render a blank correction row");
    }

    [Fact]
    public async Task Replaying_one_adjustment_does_not_move_the_balance_twice()
    {
        await using var db = WalletHarness.SystemContext();
        var workspace = WalletHarness.SeedWorkspace(db, balanceMinor: 1_000);
        await db.SaveChangesAsync();
        var service = WalletHarness.Wallets(db, through: db);
        var id = Guid.CreateVersion7();
        var request = new AdjustmentRequest(id, workspace, 100, "manual correction", WalletHarness.Admin);

        var first = await service.AdjustAsync(request, default);
        var second = await service.AdjustAsync(request, default);

        first.Applied.Should().BeTrue();
        second.Applied.Should().BeFalse();
        second.BalanceMinor.Should().Be(1_100);
        (await db.BillingLedger.CountAsync(l => l.Id == id)).Should().Be(1);
    }

    [Fact]
    public async Task A_downward_adjustment_to_zero_uses_the_billing_suspension()
    {
        await using var db = WalletHarness.SystemContext();
        var workspace = WalletHarness.SeedWorkspace(db, balanceMinor: 100);
        await db.SaveChangesAsync();
        var service = WalletHarness.Wallets(db, through: db);

        var result = await service.AdjustAsync(new AdjustmentRequest(
            Guid.CreateVersion7(), workspace, -100, "reverse mistaken credit", WalletHarness.Admin), default);

        result.StillSuspended.Should().BeTrue();
        db.ChangeTracker.Clear();
        (await db.Workspaces.SingleAsync(w => w.Id == workspace)).SuspendedReason
            .Should().Be(SuspensionReason.NoBalance);
    }

    [Fact]
    public async Task Reconciliation_reports_drift_without_silently_rewriting_money()
    {
        await using var db = WalletHarness.SystemContext();
        var workspace = WalletHarness.SeedWorkspace(db, balanceMinor: 500);
        db.BillingLedger.Add(WalletHarness.Line(workspace, WalletHarness.Hour, 450, LedgerKind.Credit));
        await db.SaveChangesAsync();
        var service = WalletHarness.Wallets(db, through: db);

        var result = await service.ReconcileAsync(workspace, default);

        result.IsBalanced.Should().BeFalse();
        result.DifferenceMinor.Should().Be(50);
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == workspace)).BalanceMinor.Should().Be(500);
    }
}
