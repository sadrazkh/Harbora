using FluentAssertions;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Projects;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests.Billing;

/// <summary>
/// The wiring point itself: <see cref="WorkspaceAccountService.CreateAsync"/> is the single place a
/// new personal or team workspace comes into being, so it is the single place sub-project 1.9's
/// signup credit is asked to grant. <see cref="SignupTrialCreditServiceTests"/> proves the service's
/// own behaviour in isolation; this proves the caller actually reaches it, with the same
/// <see cref="Harbora.Data.HarboraDbContext"/> instance every production request would hand both
/// services (see <see cref="WorkspaceAccountService"/>'s own remark on the call).
/// </summary>
public sealed class WorkspaceAccountServiceSignupCreditTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Signing_up_grants_the_configured_credit_to_the_new_personal_workspace()
    {
        await using var db = WalletHarness.SystemContext();
        var user = new User { Email = "new@example.com", DisplayName = "New Person", PasswordHash = "hash" };
        db.Users.Add(user);
        await SetAmount(db, 25_000);

        var workspace = await Service(db).EnsurePersonalWorkspaceAsync(user, default);

        (await db.Wallets.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.WorkspaceId == workspace.Id))
            .BalanceMinor.Should().Be(25_000);
        var voucher = await db.BillingVouchers.AsNoTracking().SingleAsync();
        voucher.IsTrialCredit.Should().BeTrue();
        voucher.CreatedByUserId.Should().Be(user.Id);
        voucher.RedeemedWorkspaceId.Should().Be(workspace.Id);
    }

    [Fact]
    public async Task A_second_team_workspace_by_the_same_owner_collects_nothing_more()
    {
        // "Once per owner", not "once per workspace" — a second workspace is a real new workspace
        // but not a new signup, and must not compound the credit.
        await using var db = WalletHarness.SystemContext();
        var user = new User { Email = "owner@example.com", DisplayName = "Owner", PasswordHash = "hash" };
        db.Users.Add(user);
        await SetAmount(db, 25_000);

        var personal = await Service(db).EnsurePersonalWorkspaceAsync(user, default);
        var team = await Service(db).CreateTeamWorkspaceAsync(user.Id, "Second workspace", default);

        (await db.BillingVouchers.AsNoTracking().CountAsync(v => v.IsTrialCredit)).Should().Be(1);
        (await db.Wallets.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.WorkspaceId == personal.Id))
            .BalanceMinor.Should().Be(25_000);
        (await db.Wallets.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.WorkspaceId == team.Id))
            .BalanceMinor.Should().Be(0, "the second workspace by the same owner gets no credit of its own");
    }

    [Fact]
    public async Task A_default_of_zero_grants_nothing_on_signup()
    {
        await using var db = WalletHarness.SystemContext();
        var user = new User { Email = "broke@example.com", DisplayName = "Nobody", PasswordHash = "hash" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        // No SettingKeys.SignupTrialCreditMinor row — the shipped, untouched state.

        var workspace = await Service(db).EnsurePersonalWorkspaceAsync(user, default);

        (await db.BillingVouchers.AsNoTracking().AnyAsync()).Should().BeFalse();
        (await db.Wallets.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.WorkspaceId == workspace.Id))
            .BalanceMinor.Should().Be(0);
    }

    [Fact]
    public async Task A_workspace_created_with_no_signup_credit_service_wired_still_creates_cleanly()
    {
        // The same optional-dependency shape IQuotaService and IFunctionEventBus already use: tests
        // and first-run setup construct this type directly and must not have to wire a voucher
        // service just to create a workspace.
        await using var db = WalletHarness.SystemContext();
        var user = new User { Email = "no-service@example.com", DisplayName = "Nobody", PasswordHash = "hash" };
        db.Users.Add(user);
        await SetAmount(db, 25_000);

        var service = new WorkspaceAccountService(
            db, new ProjectService(db, WalletHarness.Clock), WalletHarness.Clock,
            Options.Create(new BillingOptions { Currency = "IRR" }));

        var workspace = await service.EnsurePersonalWorkspaceAsync(user, default);

        (await db.BillingVouchers.AsNoTracking().AnyAsync()).Should().BeFalse();
        (await db.Wallets.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.WorkspaceId == workspace.Id))
            .BalanceMinor.Should().Be(0);
    }

    private static WorkspaceAccountService Service(BillingContext db) => new(
        db, new ProjectService(db, WalletHarness.Clock), WalletHarness.Clock,
        Options.Create(new BillingOptions { Currency = "IRR" }),
        signupCredit: new SignupTrialCreditService(
            db, new VoucherService(db, WalletHarness.Wallets(db, through: db), WalletHarness.Clock,
                Options.Create(new BillingOptions()))));

    private static async Task SetAmount(BillingContext db, long amountMinor)
    {
        db.Settings.Add(new Harbora.Domain.Settings.Setting
        {
            Key = Harbora.Domain.Settings.SettingKeys.SignupTrialCreditMinor,
            Value = amountMinor.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        await db.SaveChangesAsync();
    }
}
