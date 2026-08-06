using FluentAssertions;
using Harbora.Infrastructure.Proxy;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Who may reach a protected route.
///
/// The rule exists because of what an empty list means: <b>everyone</b>. A parser that quietly drops
/// a malformed entry turns "only the office" into "only two of the three addresses you typed"; one
/// that returns nothing for a whole-list typo turns it into "nobody", and the site is down for its
/// owner with no error anywhere. Both failures are silent, which is why the rejected entries come
/// back rather than being swallowed.
/// </summary>
public class AccessListTests
{
    [Fact]
    public void Addresses_and_ranges_both_pass()
    {
        var allowed = AccessList.Parse("203.0.113.5, 10.0.0.0/8, 2001:db8::1, 2001:db8::/32", out var rejected);

        allowed.Should().Equal("203.0.113.5", "10.0.0.0/8", "2001:db8::1", "2001:db8::/32");
        rejected.Should().BeEmpty();
    }

    [Fact]
    public void A_bad_entry_comes_back_by_name_rather_than_being_dropped()
    {
        // Silently keeping the good ones is how an allowlist ends up narrower than the operator
        // believes — and they only find out when somebody legitimate is refused.
        var allowed = AccessList.Parse("203.0.113.5, office, 10.0.0.0/8", out var rejected);

        allowed.Should().Equal("203.0.113.5", "10.0.0.0/8");
        rejected.Should().Equal("office");
    }

    [Theory]
    [InlineData("203.0.113.5/33")]   // no such IPv4 prefix
    [InlineData("2001:db8::/129")]   // no such IPv6 prefix
    [InlineData("203.0.113.5/")]
    [InlineData("203.0.113.5/-1")]
    [InlineData("203.0.113.5/ 8")]
    [InlineData("999.1.1.1")]
    [InlineData("10.0.0.0/8/8")]
    public void A_prefix_traefik_would_reject_is_refused_here_instead(string entry)
    {
        // Rejected at the form, not at apply time — apply happens after the operator has left the
        // page believing it saved.
        AccessList.IsValid(entry).Should().BeFalse();
    }

    [Theory]
    // An IPv6 range longer than IPv4 allows — the case that tells "128 for v6" apart from a
    // single hard-coded 32, and the one an office IPv6 allocation actually uses.
    [InlineData("2001:db8::/64")]
    [InlineData("2001:db8::1/128")]
    public void IPv6_keeps_its_own_prefix_ceiling(string entry)
    {
        AccessList.IsValid(entry).Should().BeTrue();
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("203.0.113.5/32")]
    [InlineData("::/0")]
    public void The_extremes_are_still_valid_entries(string entry)
    {
        // /0 means everyone, which is a strange thing to type but not an invalid one; refusing it
        // would be this rule inventing a policy.
        AccessList.IsValid(entry).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,,")]
    public void Nothing_means_everyone_rather_than_nobody(string? text)
    {
        var allowed = AccessList.Parse(text, out var rejected);

        allowed.Should().BeEmpty("an empty list is 'no restriction', and the caller renders no middleware at all");
        rejected.Should().BeEmpty();
    }

    [Fact]
    public void Duplicates_collapse_and_order_is_kept()
    {
        var allowed = AccessList.Parse("10.0.0.1, 203.0.113.5, 10.0.0.1", out _);

        allowed.Should().Equal("10.0.0.1", "203.0.113.5");
    }

    [Fact]
    public void Newlines_and_semicolons_separate_too()
    {
        // People paste lists out of tickets and spreadsheets.
        AccessList.Parse("10.0.0.1\n203.0.113.5;198.51.100.7", out _)
            .Should().HaveCount(3);
    }

    [Fact]
    public void The_stored_form_round_trips()
    {
        var allowed = AccessList.Parse("10.0.0.1, 203.0.113.5", out _);
        var stored = AccessList.Format(allowed);

        AccessList.Parse(stored, out var rejected).Should().Equal(allowed);
        rejected.Should().BeEmpty();
    }
}
