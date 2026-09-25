using System.Linq;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Identity;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="SignInTokenService"/> against EF InMemory — everything about it provable on a machine
/// with no Postgres.
///
/// <para>
/// <b>What is missing here on purpose: <see cref="SignInTokenService.RedeemAsync"/> itself.</b> EF
/// InMemory does not implement <c>ExecuteUpdateAsync</c> at all — it throws
/// <c>InvalidOperationException</c> the moment the query is compiled, not merely "behaves as if
/// unraced" — so no call to <c>RedeemAsync</c> can run under this provider, not even a single
/// uncontested one. <c>SignInTokenRedemptionTests</c> in the Postgres lane is where redemption itself,
/// including the two-concurrent-callers guarantee, is proven; it is unverified on this machine (no
/// Docker). What follows instead exercises everything else this service does, including what happens
/// to a row <i>after</i> a redemption — by seeding the row state a successful redemption would have
/// left rather than by producing it through the method this provider cannot run.
/// </para>
/// </summary>
public sealed class SignInTokenServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private static HarboraDbContext OpenDb(string name) => new(
        new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(name).Options);

    private static (HarboraDbContext Db, SignInTokenService Service, Clock Clock) NewService(string dbName)
    {
        var db = OpenDb(dbName);
        var clock = new Clock(Now);
        var service = new SignInTokenService(db, clock, new AccountSessionService(db, clock));
        return (db, service, clock);
    }

    private static (Guid UserId, Guid WorkspaceId) SeedOwner(HarboraDbContext db)
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        db.WorkspaceMembers.Add(new WorkspaceMember { UserId = userId, WorkspaceId = workspaceId });
        db.SaveChanges();
        return (userId, workspaceId);
    }

    /// <summary>
    /// The row state a successful <c>RedeemAsync</c> would have left, written directly instead —
    /// exactly the fields that method sets, and nothing it does not: a browser <see cref="UserSession"/>
    /// plus a <see cref="SignInToken"/> naming it, both already "redeemed". Everything downstream of
    /// redemption (<see cref="SignInTokenService.LiveAsync"/>, <see cref="SignInTokenService.RevokeAsync"/>)
    /// is exercised against this seeded state, so their own facts do not depend on the one method EF
    /// InMemory cannot run at all.
    /// </summary>
    private static SignInToken SeedRedeemedToken(
        HarboraDbContext db, Guid userId, Guid workspaceId, DateTimeOffset now, string purpose = "Fix the deploy")
    {
        var session = new UserSession
        {
            UserId = userId, ExpiresAt = now + AccountSessionService.Lifetime, LastSeenAt = now,
            IpAddress = "203.0.113.5", UserAgent = "agent-ua"
        };
        var token = new SignInToken
        {
            UserId = userId, WorkspaceId = workspaceId, CreatedByUserId = userId,
            TokenHash = TokenService.Sha256("seeded-" + Guid.NewGuid()),
            Purpose = purpose,
            CreatedAt = now,
            ExpiresAt = now + SignInTokenAccess.RedemptionLifetime,
            RedeemedAt = now, RedeemedIp = "203.0.113.5",
            SessionId = session.Id, SessionExpiresAt = now + SignInTokenAccess.SessionLifetime
        };
        db.UserSessions.Add(session);
        db.SignInTokens.Add(token);
        db.SaveChanges();
        return token;
    }

    [Fact]
    public async Task Issuing_refuses_a_workspace_the_creator_does_not_belong_to()
    {
        var (db, service, _) = NewService(Guid.NewGuid().ToString());

        var issue = await service.IssueAsync(Guid.NewGuid(), Guid.NewGuid(), "Fix the deploy", default);

        issue.Ok.Should().BeFalse();
        (await db.SignInTokens.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_token_is_never_stored_in_plaintext_and_the_rows_hash_matches_what_redemption_checks()
    {
        var (db, service, _) = NewService(Guid.NewGuid().ToString());
        var (userId, workspaceId) = SeedOwner(db);

        var issue = await service.IssueAsync(userId, workspaceId, "Fix the deploy", default);

        issue.Ok.Should().BeTrue();
        var row = await db.SignInTokens.SingleAsync();
        // The same hashing TokenService already uses for API/CLI tokens — see IssueAsync's own
        // remark for why this is not a second scheme.
        row.TokenHash.Should().Be(TokenService.Sha256(issue.PlaintextToken!));
        row.TokenHash.Should().NotBe(issue.PlaintextToken);
    }

    [Fact]
    public async Task Issuing_writes_CreatedAt_and_ExpiresAt_off_the_injected_clock_not_the_wall_clock()
    {
        var (db, service, _) = NewService(Guid.NewGuid().ToString());
        var (userId, workspaceId) = SeedOwner(db);

        await service.IssueAsync(userId, workspaceId, "Fix the deploy", default);

        var row = await db.SignInTokens.SingleAsync();
        row.CreatedAt.Should().Be(Now);
        row.ExpiresAt.Should().Be(Now + SignInTokenAccess.RedemptionLifetime);
    }

    [Fact]
    public async Task An_unknown_token_is_refused_without_revealing_whether_it_ever_existed()
    {
        var (_, service, _) = NewService(Guid.NewGuid().ToString());

        var redemption = await service.RedeemAsync("not-a-real-token", "203.0.113.5", "ua", default);

        redemption.Ok.Should().BeFalse();
        redemption.Refusal.Should().Be(SignInTokenAccess.UnknownTokenMessage);
    }

    [Fact]
    public async Task A_live_redeemed_sessions_row_is_returned_by_LiveAsync()
    {
        var (db, service, _) = NewService(Guid.NewGuid().ToString());
        var (userId, workspaceId) = SeedOwner(db);
        var token = SeedRedeemedToken(db, userId, workspaceId, Now);

        (await service.LiveAsync(token.Id, userId, default)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_revoked_tokens_live_session_stops_at_the_next_request()
    {
        var (db, service, _) = NewService(Guid.NewGuid().ToString());
        var (userId, workspaceId) = SeedOwner(db);
        var token = SeedRedeemedToken(db, userId, workspaceId, Now);

        (await service.LiveAsync(token.Id, userId, default)).Should().NotBeNull();

        await service.RevokeAsync(userId, token.Id, default);

        (await service.LiveAsync(token.Id, userId, default)).Should().BeNull(
            "the very next check against the row must stop, not whenever the ordinary session would otherwise expire");
    }

    [Fact]
    public async Task Revoking_after_redemption_ends_the_session_it_created()
    {
        var (db, service, _) = NewService(Guid.NewGuid().ToString());
        var (userId, workspaceId) = SeedOwner(db);
        var token = SeedRedeemedToken(db, userId, workspaceId, Now);

        await service.RevokeAsync(userId, token.Id, default);

        var session = await db.UserSessions.SingleAsync(s => s.Id == token.SessionId);
        session.RevokedAt.Should().NotBeNull("revoking the token must end the browser session it opened too");
    }

    [Fact]
    public async Task Revoking_before_redemption_marks_the_row_revoked_with_no_session_to_touch()
    {
        var (db, service, _) = NewService(Guid.NewGuid().ToString());
        var (userId, workspaceId) = SeedOwner(db);
        var issue = await service.IssueAsync(userId, workspaceId, "Fix the deploy", default);

        var revoked = await service.RevokeAsync(userId, issue.Token!.Id, default);

        revoked.Should().NotBeNull();
        revoked!.RevokedAt.Should().NotBeNull();
        revoked.EndedBy.Should().Be(SignInTokenEnding.Revoked);
        (await db.UserSessions.CountAsync()).Should().Be(0, "nothing was ever redeemed, so there is no session to end");
    }

    [Fact]
    public async Task Revoking_is_idempotent_and_someone_elses_token_cannot_be_revoked()
    {
        var (db, service, clock) = NewService(Guid.NewGuid().ToString());
        var (userId, workspaceId) = SeedOwner(db);
        var issue = await service.IssueAsync(userId, workspaceId, "Fix the deploy", default);

        var strangerAttempt = await service.RevokeAsync(Guid.NewGuid(), issue.Token!.Id, default);
        strangerAttempt.Should().BeNull();

        var first = await service.RevokeAsync(userId, issue.Token.Id, default);
        clock.UtcNow = Now + TimeSpan.FromMinutes(5);
        var second = await service.RevokeAsync(userId, issue.Token.Id, default);

        first.Should().NotBeNull();
        second!.RevokedAt.Should().Be(first!.RevokedAt, "a second press must not move the timestamp");
    }

    [Fact]
    public async Task A_claim_naming_a_row_that_does_not_exist_is_refused_rather_than_treated_as_live()
    {
        var (_, service, _) = NewService(Guid.NewGuid().ToString());

        (await service.LiveAsync(Guid.NewGuid(), Guid.NewGuid(), default)).Should().BeNull();
    }

    [Fact]
    public async Task A_claim_naming_somebody_elses_token_is_refused()
    {
        var (db, service, _) = NewService(Guid.NewGuid().ToString());
        var (userId, workspaceId) = SeedOwner(db);
        var token = SeedRedeemedToken(db, userId, workspaceId, Now);

        (await service.LiveAsync(token.Id, Guid.NewGuid(), default)).Should().BeNull();
    }

    [Fact]
    public async Task A_session_that_ran_its_own_course_stops_being_live_and_is_recorded_as_such()
    {
        var (db, service, clock) = NewService(Guid.NewGuid().ToString());
        var (userId, workspaceId) = SeedOwner(db);
        var token = SeedRedeemedToken(db, userId, workspaceId, Now);

        clock.UtcNow = Now + SignInTokenAccess.SessionLifetime;

        (await service.LiveAsync(token.Id, userId, default)).Should().BeNull();

        var row = await db.SignInTokens.SingleAsync();
        row.EndedBy.Should().Be(SignInTokenEnding.SessionEnded);
    }

    [Fact]
    public async Task A_session_one_second_short_of_its_hour_is_still_live()
    {
        var (db, service, clock) = NewService(Guid.NewGuid().ToString());
        var (userId, workspaceId) = SeedOwner(db);
        var token = SeedRedeemedToken(db, userId, workspaceId, Now);

        clock.UtcNow = Now + SignInTokenAccess.SessionLifetime - TimeSpan.FromSeconds(1);

        (await service.LiveAsync(token.Id, userId, default)).Should().NotBeNull();
    }

    [Fact]
    public async Task ForOwnerAsync_lists_only_this_owners_tokens_newest_first()
    {
        var (db, service, clock) = NewService(Guid.NewGuid().ToString());
        var (userId, workspaceId) = SeedOwner(db);
        var (otherUserId, otherWorkspaceId) = SeedOwner(db);

        await service.IssueAsync(userId, workspaceId, "Older", default);
        clock.UtcNow = Now + TimeSpan.FromMinutes(1);
        await service.IssueAsync(userId, workspaceId, "Newer", default);
        await service.IssueAsync(otherUserId, otherWorkspaceId, "Not mine", default);

        var list = await service.ForOwnerAsync(userId, take: 10, default);

        list.Select(t => t.Purpose).Should().Equal("Newer", "Older");
    }
}
