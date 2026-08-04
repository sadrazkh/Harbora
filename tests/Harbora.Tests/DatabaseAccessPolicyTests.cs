using System.Net;
using FluentAssertions;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Opening a database to the outside world.
///
/// Every rule here guards a silent failure. An expired grant that still works looks exactly like a
/// working one. A grant extended without limit was never temporary. And an allowlist with a typo
/// either locks the customer out — which they report immediately — or lets everyone in, which
/// nobody reports at all.
/// </summary>
public class DatabaseAccessPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private static DatabaseAccessGrant Grant(
        DatabaseAccessKind kind = DatabaseAccessKind.Temporary,
        DatabaseAccessStatus status = DatabaseAccessStatus.Active,
        TimeSpan? expiresIn = null,
        int extensions = 0,
        DateTimeOffset? createdAt = null) =>
        new()
        {
            Kind = kind,
            Status = status,
            ExpiresAt = kind == DatabaseAccessKind.Persistent ? null : Now + (expiresIn ?? TimeSpan.FromHours(1)),
            ExtensionCount = extensions,
            CreatedAt = createdAt ?? Now
        };

    // ---- duration ----

    [Fact]
    public void The_offered_windows_are_the_ones_asked_for()
    {
        DatabaseAccessPolicy.Presets.Should().Equal(
            TimeSpan.FromMinutes(15), TimeSpan.FromHours(1),
            TimeSpan.FromHours(6), TimeSpan.FromHours(24));
    }

    [Fact]
    public void A_window_too_short_to_use_is_refused()
    {
        // It would expire before anyone finished pasting the connection string.
        DatabaseAccessPolicy.RefuseDuration(TimeSpan.FromSeconds(30)).Should().NotBeNull();
    }

    [Fact]
    public void A_window_long_enough_to_be_permanent_is_refused_as_temporary()
    {
        // Making somebody choose "persistent" deliberately is the point: that path carries a
        // warning, and this one does not.
        var refusal = DatabaseAccessPolicy.RefuseDuration(TimeSpan.FromDays(30));

        refusal.Should().NotBeNull();
        refusal!.Reason.Should().Contain("persistent");
    }

    [Fact]
    public void Every_offered_preset_is_actually_accepted()
    {
        // The guard that stops the bounds and the menu drifting apart — a preset the rule rejects
        // is a button that always fails.
        foreach (var preset in DatabaseAccessPolicy.Presets)
            DatabaseAccessPolicy.RefuseDuration(preset).Should().BeNull($"{preset} is offered in the UI");
    }

    // ---- usability ----

    [Fact]
    public void A_live_temporary_grant_is_usable()
    {
        DatabaseAccessPolicy.IsUsable(Grant(), Now).Should().BeTrue();
    }

    [Fact]
    public void A_grant_past_its_time_is_not_usable_before_the_sweeper_notices()
    {
        // The sweeper runs on a timer. Between two ticks the row still says Active, and this is
        // what stops the credential being honoured in that gap.
        var grant = Grant(expiresIn: TimeSpan.FromMinutes(30));

        DatabaseAccessPolicy.IsUsable(grant, Now.AddHours(1)).Should().BeFalse();
    }

    [Fact]
    public void A_revoked_grant_is_not_usable_even_while_its_window_is_open()
    {
        var grant = Grant();
        grant.RevokedAt = Now;

        DatabaseAccessPolicy.IsUsable(grant, Now).Should().BeFalse();
    }

    [Fact]
    public void A_grant_the_node_has_not_confirmed_yet_is_not_usable()
    {
        // Pending means the login may not exist on the database yet. Honouring it would either fail
        // confusingly or, if a half-made grant left an account behind, work when it should not.
        DatabaseAccessPolicy.IsUsable(Grant(status: DatabaseAccessStatus.Pending), Now).Should().BeFalse();
    }

    [Fact]
    public void A_grant_the_node_failed_to_create_is_not_usable()
    {
        DatabaseAccessPolicy.IsUsable(Grant(status: DatabaseAccessStatus.Failed), Now).Should().BeFalse();
    }

    [Fact]
    public void A_persistent_grant_has_no_expiry_to_outlive()
    {
        DatabaseAccessPolicy.IsUsable(Grant(DatabaseAccessKind.Persistent), Now.AddYears(1))
            .Should().BeTrue();
    }

    [Fact]
    public void A_temporary_grant_with_no_expiry_is_treated_as_over_not_as_eternal()
    {
        // That combination is a bug somewhere upstream. The safe reading of it is "closed".
        var grant = Grant();
        grant.ExpiresAt = null;

        DatabaseAccessPolicy.IsUsable(grant, Now).Should().BeFalse();
        DatabaseAccessPolicy.HasExpired(grant, Now).Should().BeTrue();
    }

    [Fact]
    public void The_sweeper_leaves_persistent_grants_alone()
    {
        DatabaseAccessPolicy.HasExpired(Grant(DatabaseAccessKind.Persistent), Now.AddYears(5))
            .Should().BeFalse();
    }

    [Fact]
    public void The_sweeper_does_not_re_close_something_already_closed()
    {
        var grant = Grant(status: DatabaseAccessStatus.Revoked, expiresIn: TimeSpan.FromMinutes(-5));

        DatabaseAccessPolicy.HasExpired(grant, Now).Should().BeFalse();
    }

    // ---- extension ----

    [Fact]
    public void A_live_grant_can_be_extended()
    {
        DatabaseAccessPolicy.RefuseExtension(Grant(), TimeSpan.FromHours(1), Now).Should().BeNull();
    }

    [Fact]
    public void Extension_is_capped_so_temporary_stays_temporary()
    {
        // Unlimited extension is a permanent grant nobody consciously approved.
        var grant = Grant(extensions: DatabaseAccessPolicy.MaximumExtensions);

        DatabaseAccessPolicy.RefuseExtension(grant, TimeSpan.FromHours(1), Now)!
            .Reason.Should().Contain("extended");
    }

    [Fact]
    public void An_expired_grant_cannot_be_revived_by_extending_it()
    {
        // Reviving it would resurrect a credential that was already handed out and considered gone.
        var grant = Grant(expiresIn: TimeSpan.FromMinutes(-1));

        DatabaseAccessPolicy.RefuseExtension(grant, TimeSpan.FromHours(1), Now)!
            .Reason.Should().Contain("already ended");
    }

    [Fact]
    public void Extensions_cannot_add_up_past_the_maximum()
    {
        // Three extensions of a day each must not outlive the cap on a single grant.
        var old = Grant(createdAt: Now - TimeSpan.FromDays(6), expiresIn: TimeSpan.FromHours(1));

        DatabaseAccessPolicy.RefuseExtension(old, TimeSpan.FromDays(2), Now)!
            .Reason.Should().Contain("total");
    }

    [Fact]
    public void A_persistent_grant_is_not_extended_because_it_never_ends()
    {
        DatabaseAccessPolicy.RefuseExtension(Grant(DatabaseAccessKind.Persistent), TimeSpan.FromHours(1), Now)!
            .Reason.Should().Contain("temporary");
    }

    // ---- allowlist ----

    [Fact]
    public void An_empty_allowlist_means_anywhere()
    {
        // A real choice people make. The interface is where that is spelled out.
        DatabaseAccessPolicy.AllowsAddress(null, IPAddress.Parse("203.0.113.9")).Should().BeTrue();
        DatabaseAccessPolicy.AllowsAddress("  ", IPAddress.Parse("203.0.113.9")).Should().BeTrue();
    }

    [Fact]
    public void A_single_address_matches_only_itself()
    {
        DatabaseAccessPolicy.AllowsAddress("203.0.113.9", IPAddress.Parse("203.0.113.9")).Should().BeTrue();
        DatabaseAccessPolicy.AllowsAddress("203.0.113.9", IPAddress.Parse("203.0.113.10")).Should().BeFalse();
    }

    [Fact]
    public void A_cidr_range_matches_inside_and_refuses_outside()
    {
        DatabaseAccessPolicy.AllowsAddress("203.0.113.0/24", IPAddress.Parse("203.0.113.200")).Should().BeTrue();
        DatabaseAccessPolicy.AllowsAddress("203.0.113.0/24", IPAddress.Parse("203.0.114.1")).Should().BeFalse();
    }

    [Fact]
    public void A_prefix_that_is_not_a_whole_byte_is_handled()
    {
        // /28 is the shape people actually paste in from a hosting panel.
        DatabaseAccessPolicy.AllowsAddress("10.0.0.0/28", IPAddress.Parse("10.0.0.15")).Should().BeTrue();
        DatabaseAccessPolicy.AllowsAddress("10.0.0.0/28", IPAddress.Parse("10.0.0.16")).Should().BeFalse();
    }

    [Fact]
    public void A_typo_in_the_allowlist_blocks_rather_than_opens()
    {
        // The direction that matters. An unparseable entry treated as "allow" turns a typo into an
        // open database, and nobody reports a door that is too open.
        DatabaseAccessPolicy.AllowsAddress("203.0.113.", IPAddress.Parse("203.0.113.9")).Should().BeFalse();
        DatabaseAccessPolicy.AllowsAddress("not-an-ip", IPAddress.Parse("203.0.113.9")).Should().BeFalse();
        DatabaseAccessPolicy.AllowsAddress("10.0.0.0/notanumber", IPAddress.Parse("10.0.0.1")).Should().BeFalse();
    }

    [Fact]
    public void A_good_entry_still_matches_when_a_bad_one_sits_beside_it()
    {
        DatabaseAccessPolicy.AllowsAddress("garbage, 203.0.113.0/24", IPAddress.Parse("203.0.113.5"))
            .Should().BeTrue();
    }

    [Fact]
    public void An_unknown_caller_is_refused_when_a_list_exists()
    {
        DatabaseAccessPolicy.AllowsAddress("203.0.113.0/24", null).Should().BeFalse();
    }

    [Fact]
    public void An_address_family_mismatch_does_not_match_by_accident()
    {
        DatabaseAccessPolicy.AllowsAddress("203.0.113.0/24", IPAddress.Parse("::1")).Should().BeFalse();
    }

    [Fact]
    public void A_v4_range_never_matches_a_v6_caller_whose_leading_bytes_line_up()
    {
        // Without a family check the comparison walks the first bytes of two different-length
        // addresses. An IPv6 caller begins with zero bytes, so a v4 range that also begins with
        // zeros would match it — the one shape where the mismatch is not caught by luck.
        DatabaseAccessPolicy.AllowsAddress("0.0.0.0/24", IPAddress.Parse("::1")).Should().BeFalse();
    }
}
