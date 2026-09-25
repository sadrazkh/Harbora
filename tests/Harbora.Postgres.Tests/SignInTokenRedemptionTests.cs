using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Identity;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Postgres.Tests;

/// <summary>
/// <see cref="SignInTokenService.RedeemAsync"/>'s atomic claim, proven against real Postgres.
///
/// <para>
/// EF InMemory does not implement <c>ExecuteUpdateAsync</c> at all (it throws
/// <c>InvalidOperationException</c> the moment the query is compiled), so nothing about redemption can
/// run in <c>Harbora.Tests</c> — <c>SignInTokenServiceTests</c> there covers every other fact about
/// this service by seeding the row state redemption would have produced, rather than by calling
/// <c>RedeemAsync</c> itself. This lane is the one place the method runs at all, and the only place
/// its central claim — two concurrent callers, one winner — can be checked against a real row lock
/// instead of hoped for.
/// </para>
///
/// <para>
/// Shaped like <c>PartialUniqueIndexTests</c> beside it: <c>DeploymentEngine.QueueDeploymentAsync</c>
/// (HARBORA-0057) settled the same class of bug with a partial unique index instead, because that
/// defect was a race between two <i>inserts</i>. This one is a race between two <i>updates</i> of the
/// same existing row, which is exactly what <c>ExecuteUpdateAsync</c>'s own <c>WHERE</c> clause — not
/// a second index — is what Postgres serialises for.
/// </para>
/// </summary>
[Collection(PostgresLane.Collection)]
public sealed class SignInTokenRedemptionTests(PostgresLane lane)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private static SignInTokenService ServiceOver(HarboraDbContext db, Clock clock) =>
        new(db, clock, new AccountSessionService(db, clock));

    private static async Task<(Guid UserId, Guid WorkspaceId)> SeedOwnerAsync(HarboraDbContext db, string suffix)
    {
        var user = new User
        {
            Email = $"owner-{suffix}@example.com", DisplayName = "Owner",
            PasswordHash = "not-used-by-this-test"
        };
        var workspace = new Workspace { Name = $"acme-{suffix}", Slug = $"acme-{suffix}" };
        db.Users.Add(user);
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(new WorkspaceMember { UserId = user.Id, WorkspaceId = workspace.Id });
        await db.SaveChangesAsync();
        return (user.Id, workspace.Id);
    }

    [PostgresFact]
    public async Task Two_concurrent_redemptions_of_the_same_token_produce_exactly_one_session()
    {
        var connectionString = await lane.FreshlyMigratedAsync("signin_token_race");

        string plaintext;
        await using (var seedDb = PostgresLane.Open(connectionString))
        {
            var (userId, workspaceId) = await SeedOwnerAsync(seedDb, "race");
            var issue = await ServiceOver(seedDb, new Clock(Now)).IssueAsync(userId, workspaceId, "Race", default);
            plaintext = issue.PlaintextToken!;
        }

        // Two independent connections, so the two ExecuteUpdateAsync calls below are two genuinely
        // concurrent statements against the one row Postgres has to arbitrate between — not one
        // context serialising its own sequential calls, which is all EF InMemory could ever show.
        await using var dbA = PostgresLane.Open(connectionString);
        await using var dbB = PostgresLane.Open(connectionString);
        var serviceA = ServiceOver(dbA, new Clock(Now));
        var serviceB = ServiceOver(dbB, new Clock(Now));

        var results = await Task.WhenAll(
            serviceA.RedeemAsync(plaintext, "203.0.113.10", "agent-a", default),
            serviceB.RedeemAsync(plaintext, "203.0.113.11", "agent-b", default));

        results.Count(r => r.Ok).Should().Be(1, "two real concurrent redemptions must produce exactly one winner");
        results.Count(r => !r.Ok).Should().Be(1);
        results.Single(r => !r.Ok).Refusal.Should().Be("This token has already been used.");

        await using var verifyDb = PostgresLane.Open(connectionString);
        (await verifyDb.UserSessions.CountAsync()).Should().Be(1, "one winner, one session — never two");
        (await verifyDb.SignInTokens.SingleAsync()).SessionId.Should().Be(results.Single(r => r.Ok).Session!.Id);
    }

    [PostgresFact]
    public async Task Redeeming_once_opens_a_session_scoped_to_the_right_user_and_workspace()
    {
        var connectionString = await lane.FreshlyMigratedAsync("signin_token_redeem");
        await using var db = PostgresLane.Open(connectionString);
        var (userId, workspaceId) = await SeedOwnerAsync(db, "redeem");
        var service = ServiceOver(db, new Clock(Now));
        var issue = await service.IssueAsync(userId, workspaceId, "Fix the deploy", default);

        var redemption = await service.RedeemAsync(issue.PlaintextToken, "203.0.113.20", "agent-ua", default);

        redemption.Ok.Should().BeTrue();
        redemption.Session!.UserId.Should().Be(userId);
        redemption.Token!.WorkspaceId.Should().Be(workspaceId);
        redemption.Token.SessionId.Should().Be(redemption.Session.Id);
    }

    [PostgresFact]
    public async Task Redeeming_a_second_time_is_refused_and_the_first_session_keeps_working()
    {
        var connectionString = await lane.FreshlyMigratedAsync("signin_token_second");
        await using var db = PostgresLane.Open(connectionString);
        var (userId, workspaceId) = await SeedOwnerAsync(db, "second");
        var service = ServiceOver(db, new Clock(Now));
        var issue = await service.IssueAsync(userId, workspaceId, "Fix the deploy", default);

        var first = await service.RedeemAsync(issue.PlaintextToken, "203.0.113.21", "ua", default);
        var second = await service.RedeemAsync(issue.PlaintextToken, "203.0.113.22", "ua", default);

        first.Ok.Should().BeTrue();
        second.Ok.Should().BeFalse();
        second.Refusal.Should().Be("This token has already been used.");

        var live = await service.LiveAsync(first.Token!.Id, userId, default);
        live.Should().NotBeNull("the winner's session must not be disturbed by a later refused attempt");
    }

    [PostgresFact]
    public async Task An_expired_unredeemed_token_is_refused()
    {
        var connectionString = await lane.FreshlyMigratedAsync("signin_token_expired");
        await using var db = PostgresLane.Open(connectionString);
        var (userId, workspaceId) = await SeedOwnerAsync(db, "expired");
        var issueClock = new Clock(Now);
        var issue = await ServiceOver(db, issueClock).IssueAsync(userId, workspaceId, "Fix the deploy", default);

        var redeemClock = new Clock(Now + SignInTokenAccess.RedemptionLifetime);
        var redemption = await ServiceOver(db, redeemClock)
            .RedeemAsync(issue.PlaintextToken, "203.0.113.23", "ua", default);

        redemption.Ok.Should().BeFalse();
        redemption.Refusal.Should().Be("This token has expired.");
    }

    [PostgresFact]
    public async Task Revoking_before_redemption_refuses_any_later_attempt_to_redeem_it()
    {
        var connectionString = await lane.FreshlyMigratedAsync("signin_token_revoked");
        await using var db = PostgresLane.Open(connectionString);
        var (userId, workspaceId) = await SeedOwnerAsync(db, "revoked");
        var service = ServiceOver(db, new Clock(Now));
        var issue = await service.IssueAsync(userId, workspaceId, "Fix the deploy", default);

        await service.RevokeAsync(userId, issue.Token!.Id, default);
        var redemption = await service.RedeemAsync(issue.PlaintextToken, "203.0.113.24", "ua", default);

        redemption.Ok.Should().BeFalse();
        redemption.Refusal.Should().Be("This token has been revoked.");
        (await db.UserSessions.CountAsync()).Should().Be(0);
    }
}
