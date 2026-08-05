using FluentAssertions;
using Harbora.Infrastructure.Tenancy;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The key a resource tier is known by.
///
/// Tiers could only be seeded, so no key was ever typed by a person. Now one can be, and this key
/// is stored on every app and database that uses the tier, matched case-insensitively in several
/// places, and split out of a comma-separated allow list on the plan. Each of those is somewhere a
/// bad key fails quietly — as a tier that reads "no limit".
/// </summary>
public class InstanceSizeKeyTests
{
    [Fact]
    public void An_ordinary_key_is_kept()
    {
        InstanceSizeKey.Normalise("medium").Should().Be("medium");
    }

    [Theory]
    [InlineData("  Medium  ", "medium")]
    [InlineData("Extra Large", "extra-large")]
    [InlineData("XL_2", "xl-2")]
    public void What_was_plainly_meant_is_normalised_rather_than_refused(string typed, string expected)
    {
        InstanceSizeKey.Normalise(typed).Should().Be(expected);
    }

    [Fact]
    public void A_comma_can_never_survive()
    {
        // Plan.AllowedSizeKeys is a comma-separated list. A key with a comma in it silently becomes
        // two entries, neither of which matches the tier it came from.
        InstanceSizeKey.Normalise("big,huge").Should().NotContain(",");
    }

    [Fact]
    public void Runs_of_separators_collapse()
    {
        // "extra   large" and "extra---large" are one name typed two ways. Storing both would put
        // two tiers in the picker that read identically.
        InstanceSizeKey.Normalise("extra   large").Should().Be("extra-large");
        InstanceSizeKey.Normalise("extra---large").Should().Be("extra-large");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    [InlineData("!!!")]
    public void Something_that_cannot_become_a_key_is_refused(string? typed)
    {
        // Not stored as an empty string: an empty key would match every resource that has no size
        // set at all, which is the opposite of a limit.
        InstanceSizeKey.Normalise(typed).Should().BeNull();
    }

    [Fact]
    public void A_key_never_starts_or_ends_with_a_separator()
    {
        InstanceSizeKey.Normalise(" -big- ").Should().Be("big");
    }

    [Fact]
    public void An_overlong_key_is_cut_to_something_a_dropdown_can_show()
    {
        var key = InstanceSizeKey.Normalise(new string('a', 100));

        key.Should().HaveLength(InstanceSizeKey.MaxLength);
    }

    [Fact]
    public void Cutting_an_overlong_key_does_not_leave_a_trailing_separator()
    {
        // The cut lands exactly on the separator here, which is the only case that produces one —
        // and a key ending in a dash is not the key anybody meant.
        var typed = new string('a', InstanceSizeKey.MaxLength - 1) + " large";

        InstanceSizeKey.Normalise(typed).Should().NotEndWith("-");
    }
}
