using FluentAssertions;
using Harbora.Infrastructure.Navigation;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Whether a side panel is open.
///
/// Both panels were always drawn, so the shelf of ready-made apps sat beside the list of apps
/// somebody had already made, on every visit, forever. The rule has the shape the panel mode rule
/// has, and the trap is the same one nullable booleans always carry: "closed" and "never asked" are
/// different answers, and collapsing them reopens a panel the person deliberately shut.
/// </summary>
public class RailVisibilityTests
{
    [Fact]
    public void Quick_start_starts_out_of_the_way()
    {
        // It is a shelf of things to install, and it is in the way once somebody has installed them.
        RailVisibility.Resolve(null, null, RailPanel.QuickStart).Should().BeFalse();
    }

    [Fact]
    public void Overview_starts_shown()
    {
        // The count of what they already have is the reason the page exists.
        RailVisibility.Resolve(null, null, RailPanel.Overview).Should().BeTrue();
    }

    [Fact]
    public void A_persons_choice_to_close_survives_a_default_that_says_open()
    {
        // The bug this exists to prevent: `userChoice == true` treats a deliberate "closed" as no
        // answer, so the panel reopens on every visit against a default nobody can see.
        RailVisibility.Resolve(false, true, RailPanel.Overview).Should().BeFalse();
    }

    [Fact]
    public void A_persons_choice_to_open_survives_a_default_that_says_closed()
    {
        RailVisibility.Resolve(true, false, RailPanel.QuickStart).Should().BeTrue();
    }

    [Fact]
    public void With_no_choice_the_operators_default_decides()
    {
        RailVisibility.Resolve(null, true, RailPanel.QuickStart).Should().BeTrue();
        RailVisibility.Resolve(null, false, RailPanel.Overview).Should().BeFalse();
    }

    [Fact]
    public void An_operator_who_set_nothing_does_not_override_the_shipped_answer()
    {
        RailVisibility.Resolve(null, null, RailPanel.QuickStart)
            .Should().Be(RailVisibility.ShippedDefault(RailPanel.QuickStart));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData(" TRUE ", true)]
    public void A_stored_setting_reads_back_as_what_was_set(string stored, bool expected)
    {
        RailVisibility.ParseSetting(stored).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("yes")]
    public void A_setting_that_says_nothing_is_no_answer_rather_than_a_closed_panel(string? stored)
    {
        // A cleared setting means "follow the shipped default". Reading it as false would hide a
        // panel nobody chose to hide, and the operator would see their clearing take effect as a
        // change rather than as a reset.
        RailVisibility.ParseSetting(stored).Should().BeNull();
    }

    [Fact]
    public void What_is_stored_reads_back_as_what_was_chosen()
    {
        RailVisibility.ParseSetting(RailVisibility.Format(true)).Should().BeTrue();
        RailVisibility.ParseSetting(RailVisibility.Format(false)).Should().BeFalse();
    }

    [Fact]
    public void Clearing_a_choice_stores_nothing_rather_than_false()
    {
        // The difference between "this person wants it closed" and "this person has no opinion",
        // which is the whole reason the preference is nullable.
        RailVisibility.Format(null).Should().BeEmpty();
        RailVisibility.ParseSetting(RailVisibility.Format(null)).Should().BeNull();
    }
}
