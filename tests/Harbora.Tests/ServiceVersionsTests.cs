using FluentAssertions;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Which versions of a database engine a customer gets to choose from.
///
/// The list was two entries per engine written in C#, so PostgreSQL was "16-alpine, 15-alpine" and
/// nothing else until somebody shipped a release — while the ready-made applications beside them
/// had an entire page for this.
/// </summary>
public class ServiceVersionsTests
{
    private static readonly string[] Shipped = ["16-alpine", "15-alpine"];

    [Fact]
    public void With_no_override_the_shipped_list_is_what_is_offered()
    {
        // An operator who never opens the setting must see exactly what they saw before.
        ServiceVersions.Resolve(null, Shipped).Should().Equal(Shipped);
    }

    [Fact]
    public void An_operators_list_replaces_the_shipped_one_entirely()
    {
        // Not merged. Merging would put back the version they deliberately stopped offering.
        ServiceVersions.Resolve("17-alpine,16-alpine", Shipped)
            .Should().Equal("17-alpine", "16-alpine");
    }

    [Fact]
    public void The_order_typed_is_the_order_offered()
    {
        // The first entry is what a new database gets when nobody picks, so this is a decision and
        // not something to sort afterwards.
        ServiceVersions.Resolve("15-alpine,17-alpine,16-alpine", Shipped)
            .Should().Equal("15-alpine", "17-alpine", "16-alpine");
    }

    [Fact]
    public void An_override_that_empties_out_falls_back_rather_than_offering_nothing()
    {
        // A dropdown with nothing in it is a create form nobody can submit, and it would look like
        // the database feature had broken rather than like a setting had been cleared.
        ServiceVersions.Resolve("  ,  , ", Shipped).Should().Equal(Shipped);
    }

    [Fact]
    public void An_override_of_only_unusable_entries_falls_back_too()
    {
        ServiceVersions.Resolve("not a tag,../../etc", Shipped).Should().Equal(Shipped);
    }

    [Theory]
    [InlineData("16 alpine")]
    [InlineData("with/slash")]
    [InlineData(".leading")]
    [InlineData("with:colon")]
    public void An_entry_that_could_not_be_a_tag_is_dropped(string bad)
    {
        // It would sit in the dropdown looking like an option and fail at the image pull, which is
        // several minutes later and reads as a broken platform.
        ServiceVersions.Parse($"17-alpine,{bad}").Should().Equal("17-alpine");
    }

    [Fact]
    public void The_dropped_entries_are_reportable_rather_than_silently_vanishing()
    {
        // Half a list disappearing without explanation is worse than a refusal: the operator sees
        // the save succeed and assumes what they typed is what is stored.
        ServiceVersions.Rejected("17-alpine,16 alpine,bad/tag")
            .Should().Equal("16 alpine", "bad/tag");
    }

    [Fact]
    public void Nothing_typed_is_rejected_by_nothing()
    {
        ServiceVersions.Rejected("  ").Should().BeEmpty();
    }

    [Fact]
    public void The_same_version_twice_is_offered_once()
    {
        ServiceVersions.Parse("17-alpine, 17-alpine ,16-alpine")
            .Should().Equal("17-alpine", "16-alpine");
    }

    [Fact]
    public void Two_tags_differing_only_in_case_are_two_tags()
    {
        // A container tag is case-sensitive. Folding case would let one legitimate tag swallow
        // another, and the operator would watch half of what they typed disappear on save.
        ServiceVersions.Parse("RELEASE.2024-10-13,release.2024-10-13")
            .Should().HaveCount(2);
    }

    [Fact]
    public void What_is_stored_reads_back_as_what_was_chosen()
    {
        ServiceVersions.Parse(ServiceVersions.Format([" 17-alpine ", "16-alpine", "16-alpine"]))
            .Should().Equal("17-alpine", "16-alpine");
    }

    [Fact]
    public void Storing_an_empty_choice_is_storing_nothing()
    {
        // Which is what makes it fall back, rather than storing a blank that resolves to a blank.
        ServiceVersions.Format([]).Should().BeEmpty();
    }
}
