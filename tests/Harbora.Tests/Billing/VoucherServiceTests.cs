using FluentAssertions;
using Harbora.Domain.Billing;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests.Billing;

public sealed class VoucherServiceTests
{
    [Fact]
    public async Task A_created_voucher_keeps_only_a_hash_and_is_shown_once()
    {
        await using var db = WalletHarness.SystemContext();
        var service = Service(db);

        var created = await service.CreateAsync(
            250_000, "ABCDE-FGHJK", "launch credit", null, WalletHarness.Admin, default);

        created.PlaintextCode.Should().Be("ABCDE-FGHJK");
        created.Voucher.CodeHash.Should().HaveLength(64).And.NotContain("ABCDE");
        created.Voucher.CodeHint.Should().Be("GHJK");
    }

    [Fact]
    public async Task A_member_redeems_one_voucher_into_their_workspace_exactly_once()
    {
        await using var db = WalletHarness.SystemContext();
        var (workspace, user) = Member(db);
        await db.SaveChangesAsync();
        var service = Service(db);
        var created = await service.CreateAsync(
            250_000, "ABCDE-FGHJK", "launch credit", null, WalletHarness.Admin, default);

        var first = await service.RedeemAsync("abcde fghjk", workspace, user, default);
        var second = await service.RedeemAsync("ABCDE-FGHJK", workspace, user, default);

        first.Applied.Should().BeTrue();
        second.Applied.Should().BeFalse();
        second.BalanceMinor.Should().Be(250_000);
        db.ChangeTracker.Clear();
        (await db.BillingLedger.SingleAsync(l => l.Id == created.Voucher.Id)).Kind.Should().Be(LedgerKind.Credit);
        (await db.BillingVouchers.SingleAsync(v => v.Id == created.Voucher.Id))
            .RedeemedWorkspaceId.Should().Be(workspace);
    }

    [Fact]
    public async Task A_voucher_used_by_one_workspace_cannot_credit_another()
    {
        await using var db = WalletHarness.SystemContext();
        var first = Member(db);
        var second = Member(db);
        await db.SaveChangesAsync();
        var service = Service(db);
        await service.CreateAsync(100_000, "ABCDE-FGHJK", null, null, WalletHarness.Admin, default);

        await service.RedeemAsync("ABCDE-FGHJK", first.Workspace, first.User, default);
        var act = () => service.RedeemAsync("ABCDE-FGHJK", second.Workspace, second.User, default);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already been used*");
        (await db.Wallets.IgnoreQueryFilters().SingleOrDefaultAsync(w => w.WorkspaceId == second.Workspace))
            .Should().BeNull();
    }

    [Fact]
    public async Task An_expired_or_disabled_voucher_moves_no_money()
    {
        await using var db = WalletHarness.SystemContext();
        var member = Member(db);
        await db.SaveChangesAsync();
        var service = Service(db);
        var expired = await service.CreateAsync(
            100_000, "ABCDE-FGHJK", null, WalletHarness.Now.AddMinutes(1), WalletHarness.Admin, default);
        expired.Voucher.ExpiresAt = WalletHarness.Now.AddMinutes(-1);
        await db.SaveChangesAsync();
        var disabled = await service.CreateAsync(
            100_000, "KLMNP-QRSTU", null, null, WalletHarness.Admin, default);
        await service.DisableAsync(disabled.Voucher.Id, default);

        Func<Task> redeemExpired = () => service.RedeemAsync(
            "ABCDE-FGHJK", member.Workspace, member.User, default);
        Func<Task> redeemDisabled = () => service.RedeemAsync(
            "KLMNP-QRSTU", member.Workspace, member.User, default);
        await redeemExpired.Should().ThrowAsync<InvalidOperationException>().WithMessage("*expired*");
        await redeemDisabled.Should().ThrowAsync<InvalidOperationException>().WithMessage("*disabled*");
        (await db.BillingLedger.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_voucher_in_another_currency_cannot_redenominate_an_account()
    {
        await using var db = WalletHarness.SystemContext();
        var member = Member(db);
        await db.SaveChangesAsync();
        var service = Service(db);
        var created = await service.CreateAsync(
            100_000, "ABCDE-FGHJK", null, null, WalletHarness.Admin, default);
        created.Voucher.Currency = "USD";
        await db.SaveChangesAsync();

        var act = () => service.RedeemAsync("ABCDE-FGHJK", member.Workspace, member.User, default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*USD*IRR*");
        (await db.BillingLedger.CountAsync()).Should().Be(0);
    }

    private static VoucherService Service(BillingContext db) => new(
        db,
        WalletHarness.Wallets(db, through: db),
        WalletHarness.Clock,
        Options.Create(new BillingOptions()));

    private static (Guid Workspace, Guid User) Member(BillingContext db)
    {
        var workspace = WalletHarness.SeedWorkspace(db, withWallet: false);
        var user = Guid.CreateVersion7();
        db.Users.Add(new User { Id = user, Email = $"{user:n}@example.test", DisplayName = "member" });
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            WorkspaceId = workspace,
            UserId = user,
            Role = WorkspaceRole.Member
        });
        return (workspace, user);
    }
}
