using FluentAssertions;
using Harbora.Domain.Authorization;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The rules a support session is measured against, read without a request.
///
/// The expiry boundary is the whole point of the feature: a cookie that outlives its row must be
/// inert, and "inert" is decided here rather than by whatever the cookie happens to say.
/// </summary>
public class SupportAccessTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_support_session_borrows_the_lifetime_the_platform_already_uses_for_borrowed_access()
    {
        // AdminerSession is this platform's existing vocabulary for temporary borrowed access. A
        // second, separately-chosen hour would drift the moment either is reconsidered, and nothing
        // else in the codebase would notice.
        SupportAccess.Lifetime.Should().Be(AdminerSession.Lifetime);
        SupportAccess.Lifetime.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void A_session_one_second_short_of_the_hour_is_still_live()
    {
        SupportAccess.Expired(Start, Start.AddHours(1).AddSeconds(-1)).Should().BeFalse();
    }

    [Fact]
    public void A_session_exactly_on_the_hour_is_over()
    {
        // The same closed boundary AdminerSession.Expired uses, checked here so the two agree at the
        // one instant a disagreement would matter.
        SupportAccess.Expired(Start, Start.AddHours(1)).Should().BeTrue();
        AdminerSession.Expired(Start, Start.AddHours(1)).Should().BeTrue();
    }

    [Fact]
    public void A_row_that_was_ended_by_hand_is_not_live_even_inside_its_hour()
    {
        var row = new SupportSession
        {
            StartedAt = Start,
            ExpiresAt = Start + SupportAccess.Lifetime,
            EndedAt = Start.AddMinutes(3),
            EndedBy = SupportSessionEnding.EndedByOperator
        };

        row.IsLiveAt(Start.AddMinutes(5)).Should().BeFalse();
    }

    [Fact]
    public void A_row_inside_its_hour_and_never_ended_is_live()
    {
        var row = new SupportSession { StartedAt = Start, ExpiresAt = Start + SupportAccess.Lifetime };

        row.IsLiveAt(Start.AddMinutes(59)).Should().BeTrue();
        row.IsLiveAt(Start.AddMinutes(61)).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Starting_without_a_reason_is_refused(string? reason)
    {
        var refusal = SupportAccess.RefuseStart(
            Guid.NewGuid(), Guid.NewGuid(), targetIsActive: true, targetIsMember: true, reason);

        refusal.Should().NotBeNull();
        refusal.Should().Contain("why", "the customer is shown this sentence and an empty one tells them nothing");
    }

    [Fact]
    public void Starting_with_a_reason_is_allowed()
    {
        SupportAccess.RefuseStart(
                Guid.NewGuid(), Guid.NewGuid(), targetIsActive: true, targetIsMember: true,
                "Reproducing the failing deploy the customer reported on shop.")
            .Should().BeNull();
    }

    [Fact]
    public void Signing_in_as_yourself_is_refused()
    {
        var me = Guid.NewGuid();

        SupportAccess.RefuseStart(me, me, targetIsActive: true, targetIsMember: true, "because")
            .Should().NotBeNull();
    }

    [Fact]
    public void Signing_in_as_somebody_who_is_not_in_the_workspace_is_refused()
    {
        SupportAccess.RefuseStart(Guid.NewGuid(), Guid.NewGuid(),
                targetIsActive: true, targetIsMember: false, "because")
            .Should().NotBeNull();
    }

    [Fact]
    public void Signing_in_as_a_suspended_account_is_refused()
    {
        SupportAccess.RefuseStart(Guid.NewGuid(), Guid.NewGuid(),
                targetIsActive: false, targetIsMember: true, "because")
            .Should().NotBeNull();
    }

    [Fact]
    public void A_reason_longer_than_a_banner_can_carry_is_refused()
    {
        SupportAccess.RefuseStart(Guid.NewGuid(), Guid.NewGuid(),
                targetIsActive: true, targetIsMember: true,
                new string('x', SupportAccess.MaxReasonLength + 1))
            .Should().NotBeNull();
    }

    [Fact]
    public void Every_restricted_act_refuses_in_both_languages()
    {
        // A refusal only rendered in English is a refusal most of this panel's users cannot read.
        foreach (var act in Enum.GetValues<SupportRestrictedAct>())
        {
            SupportRestrictions.Refusal(act, isFa: false).Should().NotBeNullOrWhiteSpace();
            SupportRestrictions.Refusal(act, isFa: true).Should().NotBeNullOrWhiteSpace();
            SupportRestrictions.Refusal(act, isFa: true).Should().NotBe(
                SupportRestrictions.Refusal(act, isFa: false),
                $"{act} must say something different in Persian, not repeat the English");
        }
    }
}
