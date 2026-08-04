using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Navigation;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Which panel a person gets.
///
/// The requirement that shapes all of this is about people who already use Harbora. They were never
/// asked, and moving them into a reduced interface on an upgrade would read as "features were
/// removed" — by the people least likely to go hunting for a toggle to get them back.
/// </summary>
public class PanelModeResolverTests
{
    [Fact]
    public void A_persons_own_choice_beats_every_default()
    {
        // Otherwise somebody turns Advanced on, returns tomorrow, and finds a platform default they
        // cannot see has quietly overruled them.
        PanelModeResolver.Resolve(PanelMode.Advanced, SystemRole.Member, PanelMode.Simple)
            .Should().Be(PanelMode.Advanced);

        PanelModeResolver.Resolve(PanelMode.Simple, SystemRole.Owner, PanelMode.Advanced)
            .Should().Be(PanelMode.Simple);
    }

    [Fact]
    public void An_operator_who_has_never_chosen_gets_the_full_panel()
    {
        // The specialist controls are an operator's everyday tools; starting them in Simple hides
        // the things they signed in to use.
        PanelModeResolver.Resolve(null, SystemRole.Owner, null).Should().Be(PanelMode.Advanced);
        PanelModeResolver.Resolve(null, SystemRole.Admin, null).Should().Be(PanelMode.Advanced);
    }

    [Fact]
    public void A_new_ordinary_account_starts_simple()
    {
        PanelModeResolver.Resolve(null, SystemRole.Member, null).Should().Be(PanelMode.Simple);
        PanelModeResolver.Resolve(null, SystemRole.Viewer, null).Should().Be(PanelMode.Simple);
    }

    [Fact]
    public void An_administrator_can_change_the_default_for_new_accounts()
    {
        PanelModeResolver.Resolve(null, SystemRole.Member, PanelMode.Advanced)
            .Should().Be(PanelMode.Advanced);
    }

    [Fact]
    public void The_platform_default_does_not_override_an_operators_full_panel()
    {
        // An admin setting Simple as the default for their customers must not demote themselves.
        PanelModeResolver.Resolve(null, SystemRole.Admin, PanelMode.Simple)
            .Should().Be(PanelMode.Advanced);
    }

    [Fact]
    public void An_account_that_predates_the_feature_keeps_the_panel_it_had()
    {
        PanelModeResolver.ForExistingAccount().Should().Be(PanelMode.Advanced);
    }
}

/// <summary>
/// What each mode shows.
///
/// Simple hides specialist destinations; it never removes them. The routes stay live so a bookmark,
/// a runbook link or a support instruction all still work.
/// </summary>
public class NavigationModeTests
{
    private static readonly Func<string, bool> Everything = _ => true;

    [Fact]
    public void Advanced_shows_everything_it_always_did()
    {
        // The load-bearing assertion of this phase: the existing panel is unchanged.
        var advanced = NavigationMap.VisibleTo(NavigationMap.All, Everything, PanelMode.Advanced);

        advanced.SelectMany(g => g.Items).Should().HaveCount(
            NavigationMap.All.SelectMany(g => g.Items).Count());
    }

    [Fact]
    public void Simple_hides_the_specialist_destinations()
    {
        var simple = NavigationMap.VisibleTo(NavigationMap.All, Everything, PanelMode.Simple)
            .SelectMany(g => g.Items).Select(i => i.Key).ToList();

        simple.Should().NotContain(["networks", "routing", "audit", "git"]);
    }

    [Fact]
    public void Simple_keeps_every_everyday_flow()
    {
        // The brief lists these as things a simple panel must still do. Losing one of them would
        // make Simple useless rather than simpler.
        var simple = NavigationMap.VisibleTo(NavigationMap.All, Everything, PanelMode.Simple)
            .SelectMany(g => g.Items).Select(i => i.Key).ToList();

        simple.Should().Contain(["dashboard", "applications", "services", "deployments",
                                 "domains", "backups", "monitoring", "templates", "settings"]);
    }

    [Fact]
    public void Nothing_is_offered_in_simple_that_advanced_does_not_also_offer()
    {
        // Simple is a smaller view of one panel, not a second panel with its own pages.
        var simple = NavigationMap.VisibleTo(NavigationMap.All, Everything, PanelMode.Simple)
            .SelectMany(g => g.Items).Select(i => i.Key).ToList();
        var advanced = NavigationMap.VisibleTo(NavigationMap.All, Everything, PanelMode.Advanced)
            .SelectMany(g => g.Items).Select(i => i.Key).ToList();

        simple.Should().BeSubsetOf(advanced);
    }

    [Fact]
    public void Capability_filtering_still_applies_in_simple_mode()
    {
        // Simplifying the view must not become a way around permissions.
        var simple = NavigationMap.VisibleTo(NavigationMap.All, _ => false, PanelMode.Simple)
            .SelectMany(g => g.Items).ToList();

        simple.Should().OnlyContain(i => i.Capability == null);
    }

    [Fact]
    public void A_group_left_empty_by_the_mode_filter_disappears()
    {
        // A heading with nothing under it reads as a section that failed to load.
        var map = new[]
        {
            new NavGroup("specialist", [new NavItem("routing", "Routes", "Index", "route", Advanced: true)]),
            new NavGroup("everyday", [new NavItem("dashboard", "Home", "Index", "layout-dashboard")])
        };

        var simple = NavigationMap.VisibleTo(map, Everything, PanelMode.Simple);

        simple.Should().ContainSingle().Which.Key.Should().Be("everyday");
    }

    [Fact]
    public void The_default_overload_still_shows_the_full_panel()
    {
        // Existing callers that never mention a mode must keep behaving exactly as before.
        NavigationMap.VisibleTo(Everything).SelectMany(g => g.Items).Select(i => i.Key)
            .Should().Contain("networks");
    }
}
