using FluentAssertions;
using Harbora.Domain.Identity;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The rules a sign-in token is measured against, read without a request — the token analogue of
/// <see cref="SupportAccessTests"/>.
/// </summary>
public class SignInTokenAccessTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_session_a_token_opens_borrows_the_lifetime_the_platform_already_uses_for_borrowed_access()
    {
        // Deliberately the same constant as SupportAccess.Lifetime, not a second one independently
        // chosen — an agent let loose in the panel is the same shape of temporary access as a support
        // engineer, and a test pins the two together so they cannot silently drift apart.
        SignInTokenAccess.SessionLifetime.Should().Be(SupportAccess.Lifetime);
        SignInTokenAccess.SessionLifetime.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void The_token_itself_is_redeemable_for_much_less_than_its_session_lives()
    {
        // The whole point of two lifetimes: a bearer secret in transit is not the same risk as the
        // session it can open, and the first must not be sized like the second.
        SignInTokenAccess.RedemptionLifetime.Should().BeLessThan(SignInTokenAccess.SessionLifetime);
    }

    [Fact]
    public void A_token_one_second_short_of_its_window_can_still_be_redeemed()
    {
        SignInTokenAccess.Expired(Start, Start + SignInTokenAccess.RedemptionLifetime - TimeSpan.FromSeconds(1))
            .Should().BeFalse();
    }

    [Fact]
    public void A_token_exactly_on_its_window_is_over()
    {
        SignInTokenAccess.Expired(Start, Start + SignInTokenAccess.RedemptionLifetime).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Minting_without_a_purpose_is_refused(string? purpose)
    {
        var refusal = SignInTokenAccess.RefuseCreate(creatorIsWorkspaceMember: true, purpose);

        refusal.Should().NotBeNull();
        refusal.Should().Contain("for", "the owner is shown this sentence and an empty one tells them nothing");
    }

    [Fact]
    public void Minting_for_a_workspace_the_creator_does_not_belong_to_is_refused()
    {
        SignInTokenAccess.RefuseCreate(creatorIsWorkspaceMember: false, "Fix the deploy")
            .Should().NotBeNull();
    }

    [Fact]
    public void A_purpose_longer_than_a_banner_can_carry_is_refused()
    {
        SignInTokenAccess.RefuseCreate(creatorIsWorkspaceMember: true, new string('x', SignInTokenAccess.MaxPurposeLength + 1))
            .Should().NotBeNull();
    }

    [Fact]
    public void Minting_with_a_short_purpose_and_membership_is_allowed()
    {
        SignInTokenAccess.RefuseCreate(creatorIsWorkspaceMember: true, "Fix the deploy on shop")
            .Should().BeNull();
    }

    [Fact]
    public void Redeeming_is_allowed_when_nothing_is_wrong()
    {
        SignInTokenAccess.RefuseRedeem(alreadyRedeemed: false, isRevoked: false, isExpired: false)
            .Should().BeNull();
    }

    [Fact]
    public void A_revoked_token_refuses_redemption_even_if_it_was_never_used()
    {
        SignInTokenAccess.RefuseRedeem(alreadyRedeemed: false, isRevoked: true, isExpired: false)
            .Should().NotBeNull();
    }

    [Fact]
    public void An_already_redeemed_token_and_the_loser_of_a_race_read_the_same_sentence()
    {
        // The point of folding both into one boolean: from outside there is no way, and no need, to
        // tell "somebody else already used this" apart from "somebody else's concurrent redemption
        // just won the race this one lost".
        SignInTokenAccess.RefuseRedeem(alreadyRedeemed: true, isRevoked: false, isExpired: false)
            .Should().Be(SignInTokenAccess.RefuseRedeem(alreadyRedeemed: true, isRevoked: false, isExpired: false));
    }

    [Fact]
    public void An_expired_unredeemed_token_is_refused()
    {
        SignInTokenAccess.RefuseRedeem(alreadyRedeemed: false, isRevoked: false, isExpired: true)
            .Should().NotBeNull();
    }

    [Fact]
    public void Status_is_unredeemed_before_the_window_and_expired_after_it()
    {
        var token = new SignInToken { CreatedAt = Start, ExpiresAt = Start + SignInTokenAccess.RedemptionLifetime };

        token.StatusAt(Start).Should().Be(SignInTokenStatus.Unredeemed);
        token.StatusAt(Start + SignInTokenAccess.RedemptionLifetime).Should().Be(SignInTokenStatus.Expired);
    }

    [Fact]
    public void Status_is_redeemed_live_inside_the_session_window_and_spent_after_it()
    {
        var redeemedAt = Start + TimeSpan.FromMinutes(1);
        var token = new SignInToken
        {
            CreatedAt = Start,
            ExpiresAt = Start + SignInTokenAccess.RedemptionLifetime,
            RedeemedAt = redeemedAt,
            SessionExpiresAt = redeemedAt + SignInTokenAccess.SessionLifetime
        };

        token.StatusAt(redeemedAt + TimeSpan.FromMinutes(1)).Should().Be(SignInTokenStatus.RedeemedLive);
        token.StatusAt(redeemedAt + SignInTokenAccess.SessionLifetime).Should().Be(SignInTokenStatus.Spent);
    }

    [Fact]
    public void Status_is_revoked_no_matter_what_else_is_true_about_the_row()
    {
        var redeemedAt = Start + TimeSpan.FromMinutes(1);
        var token = new SignInToken
        {
            CreatedAt = Start,
            ExpiresAt = Start + SignInTokenAccess.RedemptionLifetime,
            RedeemedAt = redeemedAt,
            SessionExpiresAt = redeemedAt + SignInTokenAccess.SessionLifetime,
            RevokedAt = redeemedAt + TimeSpan.FromMinutes(2)
        };

        token.StatusAt(redeemedAt + TimeSpan.FromMinutes(3)).Should().Be(SignInTokenStatus.Revoked);
    }

    [Fact]
    public void A_never_redeemed_token_is_never_a_live_session()
    {
        var token = new SignInToken { CreatedAt = Start, ExpiresAt = Start + SignInTokenAccess.RedemptionLifetime };

        token.IsSessionLiveAt(Start).Should().BeFalse();
    }
}
