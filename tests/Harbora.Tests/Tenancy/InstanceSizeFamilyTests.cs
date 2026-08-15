using FluentAssertions;
using Harbora.Infrastructure.Tenancy;
using Xunit;

namespace Harbora.Tests.Tenancy;

public class InstanceSizeFamilyTests
{
    [Theory]
    [InlineData(InstanceSizeFamily.General)]
    [InlineData(InstanceSizeFamily.Cpu)]
    [InlineData(InstanceSizeFamily.Memory)]
    [InlineData(InstanceSizeFamily.Storage)]
    public void Every_family_this_code_ships_reads_as_words_in_both_languages(string family)
    {
        // The tab strip is built from these, so a family with no label would draw a blank tab that
        // still filters — a control nobody can name but everybody can press.
        InstanceSizeFamily.Label(family, isFa: false).Should().NotBeNullOrWhiteSpace();
        InstanceSizeFamily.Label(family, isFa: true).Should().NotBeNullOrWhiteSpace();
        InstanceSizeFamily.Label(family, isFa: true)
            .Should().NotBe(InstanceSizeFamily.Label(family, isFa: false));
    }

    [Fact]
    public void A_family_this_code_has_never_heard_of_reads_under_its_own_key_rather_than_vanishing()
    {
        // The provider can add sizes, so they can invent a family. Hiding it would take a PRICED
        // tier off the chooser: capacity the customer cannot buy and the operator cannot see they
        // are not selling. Falling back to "General" would be worse still — it would file the tier
        // under a family it is not in.
        InstanceSizeFamily.Label("gpu", isFa: false).Should().Be("gpu");
        InstanceSizeFamily.Label("gpu", isFa: true).Should().Be("gpu");
    }

    [Fact]
    public void A_size_with_no_family_is_general_rather_than_a_family_of_its_own()
    {
        // Every tier that predates the column is blank, and blank is not a fifth family with an
        // empty name — it is the ordinary one. This is what the seeder's backfill relies on, and
        // what stops an install upgrading into a chooser with a nameless tab on it.
        InstanceSizeFamily.Normalise(null).Should().Be(InstanceSizeFamily.General);
        InstanceSizeFamily.Normalise("").Should().Be(InstanceSizeFamily.General);
        InstanceSizeFamily.Normalise("   ").Should().Be(InstanceSizeFamily.General);
    }

    [Fact]
    public void A_family_typed_with_capitals_or_spaces_is_the_same_family()
    {
        // Stored on one size as "Memory" and on the next as "memory" would split one tab into two,
        // each holding half the tiers. Normalised on the way in, exactly as a size's key is.
        InstanceSizeFamily.Normalise("Memory").Should().Be(InstanceSizeFamily.Memory);
        InstanceSizeFamily.Normalise("  CPU  ").Should().Be(InstanceSizeFamily.Cpu);
        InstanceSizeFamily.Normalise("High Memory").Should().Be("high-memory");
    }

    [Fact]
    public void The_tab_strip_lists_the_families_this_code_knows_in_its_own_order()
    {
        // Not alphabetical: general comes first because it is what most people want, and a chooser
        // that opened on "cpu" because c sorts before g would put the specialist tier in front.
        var present = InstanceSizeFamily.Present(
            [InstanceSizeFamily.Storage, InstanceSizeFamily.General, InstanceSizeFamily.Memory]);

        present.Should().Equal(
            InstanceSizeFamily.General, InstanceSizeFamily.Memory, InstanceSizeFamily.Storage);
    }

    [Fact]
    public void The_tab_strip_puts_families_it_does_not_know_after_the_ones_it_does()
    {
        // Ordered among themselves so the strip does not reshuffle between requests — a tab that
        // moves is a tab somebody clicks by mistake.
        var present = InstanceSizeFamily.Present(
            ["tpu", InstanceSizeFamily.Cpu, "gpu", InstanceSizeFamily.General]);

        present.Should().Equal(InstanceSizeFamily.General, InstanceSizeFamily.Cpu, "gpu", "tpu");
    }

    [Fact]
    public void The_tab_strip_shows_one_tab_per_family_however_many_sizes_are_in_it()
    {
        InstanceSizeFamily.Present(
            [InstanceSizeFamily.General, InstanceSizeFamily.General, InstanceSizeFamily.Memory])
            .Should().Equal(InstanceSizeFamily.General, InstanceSizeFamily.Memory);
    }

    [Fact]
    public void The_tab_strip_normalises_what_it_is_given_rather_than_trusting_it()
    {
        // The families arrive off rows that may predate the column or have been written by an older
        // build. A blank one is general; it must not become a nameless tab.
        InstanceSizeFamily.Present(["", "Memory", null!])
            .Should().Equal(InstanceSizeFamily.General, InstanceSizeFamily.Memory);
    }
}
