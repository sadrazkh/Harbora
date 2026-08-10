using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Projects;
using Harbora.Infrastructure.Security;
using Harbora.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

public class WorkspaceAccountServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Every_account_gets_exactly_one_personal_workspace_with_wallet_and_environment()
    {
        await using var db = Db();
        var user = new User { Email = "person@example.com", DisplayName = "Person", PasswordHash = "hash" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var service = Service(db);

        var first = await service.EnsurePersonalWorkspaceAsync(user, default);
        var second = await service.EnsurePersonalWorkspaceAsync(user, default);

        second.Id.Should().Be(first.Id);
        db.Workspaces.Should().ContainSingle(w => w.OwnerUserId == user.Id && w.IsPersonal);
        db.WorkspaceMembers.Should().ContainSingle(m => m.WorkspaceId == first.Id && m.UserId == user.Id && m.Role == WorkspaceRole.Admin);
        db.Wallets.Should().ContainSingle(w => w.WorkspaceId == first.Id && w.BalanceMinor == 0 && w.Currency == "IRR");
        db.Environments.Should().ContainSingle(e => e.WorkspaceId == first.Id && e.IsDefault);
    }

    [Fact]
    public async Task Invitation_is_single_use_email_bound_and_never_stores_plaintext_token()
    {
        await using var db = Db();
        var owner = new User { Email = "owner@example.com", DisplayName = "Owner", PasswordHash = "hash" };
        var member = new User { Email = "member@example.com", DisplayName = "Member", PasswordHash = "hash" };
        db.Users.AddRange(owner, member);
        await db.SaveChangesAsync();
        var service = Service(db);
        var workspace = await service.CreateTeamWorkspaceAsync(owner.Id, "Shared Team", default);

        var issued = await service.InviteAsync(workspace.Id, owner.Id, member.Email, WorkspaceRole.Member, default);

        issued.Invitation.TokenHash.Should().NotContain(issued.Token);
        db.WorkspaceInvitations.Single().TokenHash.Should().NotBe(issued.Token);
        var accepted = await service.AcceptInvitationAsync(issued.Token, member, default);
        accepted.Id.Should().Be(workspace.Id);
        db.WorkspaceMembers.Should().Contain(m => m.WorkspaceId == workspace.Id && m.UserId == member.Id);
        var replay = () => service.AcceptInvitationAsync(issued.Token, member, default);
        await replay.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already used*");
    }

    [Fact]
    public async Task Invitation_cannot_be_spent_by_a_different_signed_in_email()
    {
        await using var db = Db();
        var owner = new User { Email = "owner2@example.com", DisplayName = "Owner", PasswordHash = "hash" };
        var invited = new User { Email = "right@example.com", DisplayName = "Right", PasswordHash = "hash" };
        var attacker = new User { Email = "wrong@example.com", DisplayName = "Wrong", PasswordHash = "hash" };
        db.Users.AddRange(owner, invited, attacker);
        await db.SaveChangesAsync();
        var service = Service(db);
        var workspace = await service.CreateTeamWorkspaceAsync(owner.Id, "Safe Team", default);
        var issued = await service.InviteAsync(workspace.Id, owner.Id, invited.Email, WorkspaceRole.Admin, default);

        var spend = () => service.AcceptInvitationAsync(issued.Token, attacker, default);

        await spend.Should().ThrowAsync<InvalidOperationException>().WithMessage("*different email*");
        db.WorkspaceInvitations.Single().AcceptedAt.Should().BeNull();
    }

    [Fact]
    public async Task Invitation_is_not_issued_when_the_plan_has_no_seat_left()
    {
        await using var db = Db();
        var plan = new Harbora.Domain.Tenancy.Plan
        {
            Name = "One seat", MaxMembers = 1, IsEnabled = true, MonthlyPrice = 1
        };
        var owner = new User { Email = "seat-owner@example.com", DisplayName = "Owner", PasswordHash = "hash" };
        db.AddRange(plan, owner);
        await db.SaveChangesAsync();
        var quota = new QuotaService(db, Options.Create(new BillingOptions { Enabled = true }));
        var service = new WorkspaceAccountService(
            db, new ProjectService(db, new Clock(), quota), new Clock(),
            Options.Create(new BillingOptions { Currency = "IRR", Enabled = true }), quota);
        var workspace = await service.CreateTeamWorkspaceAsync(owner.Id, "Full team", default);

        var invite = () => service.InviteAsync(
            workspace.Id, owner.Id, "next@example.com", WorkspaceRole.Member, default);

        await invite.Should().ThrowAsync<InvalidOperationException>().WithMessage("*member*");
        db.WorkspaceInvitations.Should().BeEmpty();
    }

    private static HarboraDbContext Db() => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static WorkspaceAccountService Service(HarboraDbContext db) => new(
        db, new ProjectService(db, new Clock()), new Clock(),
        Options.Create(new BillingOptions { Currency = "IRR" }));

    private sealed class Clock : ISystemClock { public DateTimeOffset UtcNow => Now; }
}
