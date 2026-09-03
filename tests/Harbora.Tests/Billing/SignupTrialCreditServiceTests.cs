using FluentAssertions;
using Harbora.Domain.Billing;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Settings;
using Harbora.Infrastructure.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Harbora.Tests.Billing;

/// <summary>
/// The administrator-set signup credit (sub-project 1.9): an amount that defaults to zero, granted
/// to a brand-new workspace exactly once, through the same <see cref="VoucherService"/> a human
/// administrator's own voucher screen uses.
/// </summary>
public sealed class SignupTrialCreditServiceTests
{
    [Fact]
    public async Task A_default_of_zero_grants_nothing()
    {
        await using var db = WalletHarness.SystemContext();
        var owner = SeedOwner(db);
        var ws = WalletHarness.SeedWorkspace(db, withWallet: false);
        AddMember(db, ws, owner);
        await db.SaveChangesAsync();

        // No SettingKeys.SignupTrialCreditMinor row at all — the shipped, untouched state.
        await Service(db).GrantAsync(ws, owner, default);

        (await db.BillingVouchers.AsNoTracking().AnyAsync()).Should().BeFalse();
        (await db.Wallets.IgnoreQueryFilters().AsNoTracking().AnyAsync(w => w.WorkspaceId == ws))
            .Should().BeFalse("a zero credit must not even open a wallet");
        (await db.BillingLedger.IgnoreQueryFilters().AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task An_explicit_zero_also_grants_nothing()
    {
        await using var db = WalletHarness.SystemContext();
        var owner = SeedOwner(db);
        var ws = WalletHarness.SeedWorkspace(db, withWallet: false);
        AddMember(db, ws, owner);
        db.Settings.Add(new Setting { Key = SettingKeys.SignupTrialCreditMinor, Value = "0" });
        await db.SaveChangesAsync();

        await Service(db).GrantAsync(ws, owner, default);

        (await db.BillingVouchers.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_configured_amount_grants_exactly_once_as_an_ordinary_voucher()
    {
        await using var db = WalletHarness.SystemContext();
        var owner = SeedOwner(db);
        var ws = WalletHarness.SeedWorkspace(db, withWallet: false);
        AddMember(db, ws, owner);
        await SetAmount(db, 50_000);

        await Service(db).GrantAsync(ws, owner, default);

        var wallet = await db.Wallets.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.WorkspaceId == ws);
        wallet.BalanceMinor.Should().Be(50_000);

        var voucher = await db.BillingVouchers.AsNoTracking().SingleAsync();
        voucher.AmountMinor.Should().Be(50_000);
        voucher.IsTrialCredit.Should().BeTrue();
        voucher.CreatedByUserId.Should().Be(owner);
        voucher.RedeemedWorkspaceId.Should().Be(ws);
        voucher.RedeemedAt.Should().NotBeNull();

        // Issued through VoucherService.RedeemAsync -> WalletService.CreditAsync: an ordinary Credit
        // line, under the voucher's own id — there is no second way money entered this ledger.
        var line = await db.BillingLedger.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.Id == voucher.Id);
        line.Kind.Should().Be(LedgerKind.Credit);
        line.WorkspaceId.Should().Be(ws);
        line.AmountMinor.Should().Be(50_000);
    }

    [Fact]
    public async Task A_retried_grant_for_the_same_owner_collects_nothing_more()
    {
        await using var db = WalletHarness.SystemContext();
        var owner = SeedOwner(db);
        var ws = WalletHarness.SeedWorkspace(db, withWallet: false);
        AddMember(db, ws, owner);
        await SetAmount(db, 50_000);

        await Service(db).GrantAsync(ws, owner, default);
        await Service(db).GrantAsync(ws, owner, default);

        (await db.BillingVouchers.AsNoTracking().CountAsync(v => v.IsTrialCredit)).Should().Be(1);
        (await db.Wallets.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.WorkspaceId == ws))
            .BalanceMinor.Should().Be(50_000, "a retried request must not collect a second time");
    }

    [Fact]
    public async Task A_workspace_deleted_and_recreated_by_the_same_owner_does_not_collect_again()
    {
        // The identity of "already granted" is the owner, not the workspace's own id — a workspace
        // deleted and recreated gets a fresh Guid and would look like a brand-new grant target if the
        // check were keyed on that. This is the abuse the design has to refuse: delete the trial
        // workspace, create a new one, collect again.
        await using var db = WalletHarness.SystemContext();
        var owner = SeedOwner(db);
        var firstWorkspace = WalletHarness.SeedWorkspace(db, withWallet: false);
        AddMember(db, firstWorkspace, owner);
        await SetAmount(db, 50_000);
        await Service(db).GrantAsync(firstWorkspace, owner, default);

        // The workspace (and its wallet/membership) is gone; the voucher row survives — it is not
        // scoped to the workspace at all, only to the owner who redeemed it.
        db.Workspaces.Remove(await db.Workspaces.IgnoreQueryFilters().SingleAsync(w => w.Id == firstWorkspace));
        db.Wallets.RemoveRange(db.Wallets.IgnoreQueryFilters().Where(w => w.WorkspaceId == firstWorkspace));
        db.WorkspaceMembers.RemoveRange(db.WorkspaceMembers.IgnoreQueryFilters().Where(m => m.WorkspaceId == firstWorkspace));
        await db.SaveChangesAsync();

        var secondWorkspace = WalletHarness.SeedWorkspace(db, withWallet: false);
        AddMember(db, secondWorkspace, owner);
        await db.SaveChangesAsync();

        await Service(db).GrantAsync(secondWorkspace, owner, default);

        (await db.BillingVouchers.AsNoTracking().CountAsync(v => v.IsTrialCredit)).Should().Be(1);
        (await db.Wallets.IgnoreQueryFilters().AsNoTracking().AnyAsync(w => w.WorkspaceId == secondWorkspace))
            .Should().BeFalse("the recreated workspace must not open a wallet for a credit that never lands");
    }

    [Fact]
    public async Task A_second_workspace_by_a_different_owner_collects_its_own_credit()
    {
        // Proves the refusal above is keyed on the owner and not, say, on "a trial credit already
        // exists anywhere on the install".
        await using var db = WalletHarness.SystemContext();
        var first = SeedOwner(db);
        var second = SeedOwner(db);
        var ws1 = WalletHarness.SeedWorkspace(db, withWallet: false);
        var ws2 = WalletHarness.SeedWorkspace(db, withWallet: false);
        AddMember(db, ws1, first);
        AddMember(db, ws2, second);
        await SetAmount(db, 50_000);

        await Service(db).GrantAsync(ws1, first, default);
        await Service(db).GrantAsync(ws2, second, default);

        (await db.BillingVouchers.AsNoTracking().CountAsync(v => v.IsTrialCredit)).Should().Be(2);
        (await db.Wallets.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.WorkspaceId == ws1)).BalanceMinor.Should().Be(50_000);
        (await db.Wallets.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.WorkspaceId == ws2)).BalanceMinor.Should().Be(50_000);
    }

    [Fact]
    public async Task A_concurrent_double_signup_for_the_same_owner_collects_exactly_once()
    {
        // The read-based fast path cannot win a real race — this stages the case where two grants for
        // the same brand-new owner both pass it, and only the database's own partial unique index
        // (IX_BillingVouchers_TrialCreditOwner) decides who gets the money. The losing side's insert
        // fails with 23505, exactly the way WalletService's own ledger primary key already settles a
        // credit race — see WalletServiceTests' own "when two credits collide for real" tests, which
        // this mirrors.
        await using var db = WalletHarness.SystemContext();
        var owner = SeedOwner(db);
        var ws = WalletHarness.SeedWorkspace(db, withWallet: false);
        AddMember(db, ws, owner);
        await SetAmount(db, 50_000);

        var hostile = WalletHarness.ProviderContext(db) as BillingContext
            ?? throw new InvalidOperationException("expected a BillingContext");
        var winner = Service(db);
        var loser = Service(hostile);

        // The winner's write really happens (it is what makes the loser's collision real rather than
        // imagined), immediately before the loser's own insert is refused. GrantAsync's happy path is
        // exactly one save (VoucherService.CreateAsync's), so "the next save" is unambiguously it.
        hostile.FailTheNextSaveWith = Refusal(
            PostgresErrorCodes.UniqueViolation,
            "duplicate key value violates unique constraint \"IX_BillingVouchers_TrialCreditOwner\"");
        hostile.WhenItRefuses = () => winner.GrantAsync(ws, owner, default).GetAwaiter().GetResult();

        await loser.GrantAsync(ws, owner, default);

        // A failed insert leaves no row behind — the loser's attempt never reached the store at all,
        // so there is nothing here to clean up, unlike a design that flagged an already-inserted row.
        (await db.BillingVouchers.AsNoTracking().CountAsync()).Should().Be(1);
        (await db.BillingVouchers.AsNoTracking().SingleAsync()).IsTrialCredit.Should().BeTrue();
        (await db.Wallets.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.WorkspaceId == ws))
            .BalanceMinor.Should().Be(50_000, "exactly one of the two concurrent grants may land");
    }

    [Fact]
    public async Task The_totals_count_only_what_actually_redeemed()
    {
        await using var db = WalletHarness.SystemContext();
        var first = SeedOwner(db);
        var second = SeedOwner(db);
        var ws1 = WalletHarness.SeedWorkspace(db, withWallet: false);
        var ws2 = WalletHarness.SeedWorkspace(db, withWallet: false);
        AddMember(db, ws1, first);
        AddMember(db, ws2, second);
        await SetAmount(db, 30_000);

        await Service(db).GrantAsync(ws1, first, default);
        await Service(db).GrantAsync(ws2, second, default);

        var totals = await Service(db).TotalsAsync(default);
        totals.WorkspacesGranted.Should().Be(2);
        totals.TotalGrantedMinor.Should().Be(60_000);
    }

    [Fact]
    public async Task Reading_the_amount_ignores_garbage_left_in_the_setting()
    {
        await using var db = WalletHarness.SystemContext();
        db.Settings.Add(new Setting { Key = SettingKeys.SignupTrialCreditMinor, Value = "not a number" });
        await db.SaveChangesAsync();

        (await Service(db).GetAmountMinorAsync(default)).Should().Be(0);
    }

    private static SignupTrialCreditService Service(BillingContext db) =>
        new(db, new VoucherService(db, WalletHarness.Wallets(db, through: db), WalletHarness.Clock,
            Options.Create(new BillingOptions())));

    private static Guid SeedOwner(BillingContext db)
    {
        var id = Guid.CreateVersion7();
        db.Users.Add(new User { Id = id, Email = $"{id:n}@example.test", DisplayName = "owner" });
        return id;
    }

    private static void AddMember(BillingContext db, Guid workspaceId, Guid userId) =>
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = WorkspaceRole.Admin
        });

    private static async Task SetAmount(BillingContext db, long amountMinor)
    {
        db.Settings.Add(new Setting
        {
            Key = SettingKeys.SignupTrialCreditMinor,
            Value = amountMinor.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        await db.SaveChangesAsync();
    }

    private static DbUpdateException Refusal(string sqlState, string message) =>
        new(message, new PostgresException(message, "ERROR", "ERROR", sqlState));
}
