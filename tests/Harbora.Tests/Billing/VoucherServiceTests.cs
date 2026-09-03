using FluentAssertions;
using Harbora.Domain.Billing;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Harbora.Tests.Billing;

public sealed class VoucherServiceTests
{
    [Fact]
    public async Task A_voucher_can_be_minted_as_a_trial_credit_and_it_shows_on_the_row()
    {
        // The only door SignupTrialCreditService uses — isTrialCredit is trailing and defaulted, so
        // every other caller (VouchersController's own admin form included) is unaffected.
        await using var db = WalletHarness.SystemContext();
        var service = Service(db);

        var created = await service.CreateAsync(
            50_000, requestedCode: null, note: "Signup trial credit", expiresAt: null,
            createdByUserId: WalletHarness.Admin, ct: default, isTrialCredit: true);

        created.Voucher.IsTrialCredit.Should().BeTrue();
        (await db.BillingVouchers.AsNoTracking().SingleAsync(v => v.Id == created.Voucher.Id))
            .IsTrialCredit.Should().BeTrue();
    }

    [Fact]
    public async Task An_ordinary_voucher_is_never_marked_as_a_trial_credit()
    {
        await using var db = WalletHarness.SystemContext();
        var service = Service(db);

        var created = await service.CreateAsync(
            50_000, "ABCDE-FGHJK", "launch credit", null, WalletHarness.Admin, default);

        created.Voucher.IsTrialCredit.Should().BeFalse();
    }

    [Fact]
    public async Task A_unique_violation_on_a_trial_credit_insert_is_refused_the_same_way_a_code_collision_is()
    {
        // 23505 alone does not say which index refused the insert, and a trial-credit insert
        // touches two: CodeHash (checked by the pre-read above, but a race can still lose to it)
        // and, for a trial credit only, the live-Postgres-verified partial index
        // IX_BillingVouchers_TrialCreditOwner (see Harbora.Postgres.Tests.BillingRuntimeIndexTests,
        // which proves that constraint fires for real). Both are refused the same honest way here —
        // "this could not be created" — rather than one being silently swallowed; it is
        // SignupTrialCreditService, the only caller that ever sets isTrialCredit, that decides a
        // refusal here means "already granted" and treats it as a safe no-op.
        await using var db = WalletHarness.SystemContext();
        var hostile = WalletHarness.ProviderContext(db) as BillingContext
            ?? throw new InvalidOperationException("expected a BillingContext");
        hostile.FailTheNextSaveWith = Refusal(
            PostgresErrorCodes.UniqueViolation,
            "duplicate key value violates unique constraint \"IX_BillingVouchers_TrialCreditOwner\"");

        var act = () => Service(hostile).CreateAsync(
            50_000, requestedCode: null, note: null, expiresAt: null,
            createdByUserId: WalletHarness.Admin, ct: default, isTrialCredit: true);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    private static DbUpdateException Refusal(string sqlState, string message) =>
        new(message, new PostgresException(message, "ERROR", "ERROR", sqlState));

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
