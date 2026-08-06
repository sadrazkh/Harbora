using FluentAssertions;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Security;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The rules of a password-reset link.
///
/// Every branch is an account-takeover path if it leans the wrong way: a token that outlives its
/// window keeps working from a forwarded email; one that works twice turns a provider's log into a
/// login; one that is stored in the clear turns a database read into every account at once.
/// </summary>
public class PasswordResetTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private static PasswordResetToken Row(TimeSpan untilExpiry, DateTimeOffset? usedAt = null) => new()
    {
        UserId = Guid.NewGuid(),
        TokenHash = "irrelevant-here",
        ExpiresAt = Now + untilExpiry,
        UsedAt = usedAt
    };

    [Fact]
    public void A_fresh_token_inside_its_window_redeems()
    {
        PasswordReset.Check(Row(TimeSpan.FromMinutes(30)), Now).Should().BeNull();
    }

    [Fact]
    public void No_matching_row_is_unknown()
    {
        PasswordReset.Check(null, Now).Should().Be(PasswordResetRefusal.Unknown);
    }

    [Fact]
    public void An_expired_token_is_refused()
    {
        PasswordReset.Check(Row(TimeSpan.FromMinutes(-1)), Now).Should().Be(PasswordResetRefusal.Expired);
    }

    [Fact]
    public void Expiry_is_exclusive_at_the_boundary()
    {
        // At the exact expiry instant the link is dead. Inclusive would be one more moment of
        // validity nobody promised.
        PasswordReset.Check(Row(TimeSpan.Zero), Now).Should().Be(PasswordResetRefusal.Expired);
    }

    [Fact]
    public void A_used_token_never_works_twice()
    {
        PasswordReset.Check(Row(TimeSpan.FromMinutes(30), usedAt: Now.AddMinutes(-5)), Now)
            .Should().Be(PasswordResetRefusal.AlreadyUsed);
    }

    [Fact]
    public void Used_wins_over_expired()
    {
        // "Already used" is the honest answer for a link that was redeemed and has since also
        // expired — telling the person it merely expired invites them to request another and
        // wonder why their password changed.
        PasswordReset.Check(Row(TimeSpan.FromMinutes(-10), usedAt: Now.AddMinutes(-30)), Now)
            .Should().Be(PasswordResetRefusal.AlreadyUsed);
    }

    [Fact]
    public void The_token_is_never_its_own_hash()
    {
        var (token, hash) = PasswordReset.Issue();

        hash.Should().NotBe(token, "storing the token itself makes the table a list of working links");
        PasswordReset.HashOf(token).Should().Be(hash, "or the emailed link can never be matched to its row");
    }

    [Fact]
    public void Tokens_are_unique_and_url_safe()
    {
        var tokens = Enumerable.Range(0, 64).Select(_ => PasswordReset.Issue().Token).ToList();

        tokens.Should().OnlyHaveUniqueItems();
        tokens.Should().OnlyContain(t => !t.Contains('+') && !t.Contains('/') && !t.Contains('='),
            "the token travels inside a URL query string");
        tokens.Should().OnlyContain(t => t.Length >= 40, "256 bits is the point");
    }
}
