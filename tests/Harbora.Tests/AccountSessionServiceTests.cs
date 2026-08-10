using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

public sealed class AccountSessionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Create_persists_a_bounded_session_and_client_metadata()
    {
        await using var db = NewDb();
        var userId = Guid.NewGuid();
        var service = new AccountSessionService(db, new Clock());

        var row = await service.CreateAsync(userId, "203.0.113.9", new string('a', 600), default);

        row.ExpiresAt.Should().Be(Now + AccountSessionService.Lifetime);
        row.LastSeenAt.Should().Be(Now);
        row.IpAddress.Should().Be("203.0.113.9");
        row.UserAgent.Should().HaveLength(512);
        (await db.UserSessions.SingleAsync()).Id.Should().Be(row.Id);
    }

    [Fact]
    public async Task Revoke_all_can_keep_only_the_current_session()
    {
        await using var db = NewDb();
        var userId = Guid.NewGuid();
        var keep = new UserSession { UserId = userId, ExpiresAt = Now.AddDays(1), LastSeenAt = Now };
        var revoke = new UserSession { UserId = userId, ExpiresAt = Now.AddDays(1), LastSeenAt = Now };
        var otherUser = new UserSession { UserId = Guid.NewGuid(), ExpiresAt = Now.AddDays(1), LastSeenAt = Now };
        db.UserSessions.AddRange(keep, revoke, otherUser);
        await db.SaveChangesAsync();

        await new AccountSessionService(db, new Clock()).RevokeAllAsync(userId, keep.Id, default);

        keep.RevokedAt.Should().BeNull();
        revoke.RevokedAt.Should().Be(Now);
        otherUser.RevokedAt.Should().BeNull();
    }

    [Fact]
    public void Verification_issues_a_single_use_digest_without_storing_the_plaintext()
    {
        using var db = NewDb();
        var service = new AccountSessionService(db, new Clock());

        var issued = service.IssueVerification(Guid.NewGuid());

        issued.Token.Should().HaveLength(64);
        issued.Row.TokenHash.Should().Be(AccountSessionService.Hash(issued.Token));
        issued.Row.TokenHash.Should().NotBe(issued.Token);
        issued.Row.ExpiresAt.Should().Be(Now + AccountSessionService.VerificationLifetime);
    }

    private static HarboraDbContext NewDb() => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("account-session-" + Guid.NewGuid()).Options);

    private sealed class Clock : ISystemClock { public DateTimeOffset UtcNow => Now; }
}
