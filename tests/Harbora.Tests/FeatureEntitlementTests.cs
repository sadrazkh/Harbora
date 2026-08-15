using FluentAssertions;
using Harbora.Domain.Features;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Navigation;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Who a feature is for.
///
/// <para>
/// The rule these defend: <b>an entitlement somebody lacks is shown locked, and a capability they
/// lack is hidden</b>. Those two look alike and behave oppositely, and getting them the wrong way
/// round has a cost in each direction — a hidden entitlement is a tier nobody can ask to buy, and a
/// visible capability is a locked door on a menu that is supposed to be trustworthy.
/// </para>
/// </summary>
public class FeatureEntitlementTests
{
    private const string Key = PlatformFeatures.Functions;

    [Fact]
    public void With_nothing_decided_the_shipped_default_answers()
    {
        var verdict = FeatureAccess.Resolve(Key, plan: null, workspace: null);

        verdict.State.Should().Be(PlatformFeatures.DefaultFor(Key));
        verdict.DecidedBy.Should().Be(FeatureDecision.ShippedDefault);
    }

    [Fact]
    public void A_plan_grant_beats_the_shipped_default()
    {
        var verdict = FeatureAccess.Resolve(Key, plan: FeatureState.Enabled, workspace: null);

        verdict.IsEnabled.Should().BeTrue();
        verdict.DecidedBy.Should().Be(FeatureDecision.Plan);
    }

    [Fact]
    public void A_workspace_override_beats_its_plan()
    {
        // The whole reason overrides exist: "turn it on for this one customer" without inventing a
        // plan for them.
        var verdict = FeatureAccess.Resolve(Key, plan: FeatureState.Locked, workspace: FeatureState.Enabled);

        verdict.IsEnabled.Should().BeTrue();
        verdict.DecidedBy.Should().Be(FeatureDecision.Workspace);
    }

    [Fact]
    public void A_workspace_override_can_also_take_it_away()
    {
        var verdict = FeatureAccess.Resolve(Key, plan: FeatureState.Enabled, workspace: FeatureState.Locked);

        verdict.IsEnabled.Should().BeFalse();
        verdict.IsLocked.Should().BeTrue();
        verdict.DecidedBy.Should().Be(FeatureDecision.Workspace);
    }

    [Fact]
    public void Inherit_at_a_level_means_no_decision_there()
    {
        // Inherit is what makes a stored row removable without deleting it, so it must not read as
        // a decision — otherwise clearing an override would lock everybody it was clearing for.
        var verdict = FeatureAccess.Resolve(Key, plan: FeatureState.Enabled, workspace: FeatureState.Inherit);

        verdict.IsEnabled.Should().BeTrue();
        verdict.DecidedBy.Should().Be(FeatureDecision.Plan);
    }

    [Fact]
    public void An_unknown_key_is_hidden_rather_than_enabled()
    {
        // A typo in a [RequireFeature] attribute, or a grant left behind by a removed feature, must
        // fail closed. The opposite default would open a page nobody meant to sell.
        var verdict = FeatureAccess.Resolve("nothing.reads.this", plan: null, workspace: null);

        verdict.State.Should().Be(FeatureState.Hidden);
        verdict.IsVisible.Should().BeFalse();
    }

    [Fact]
    public void Locked_is_visible_and_hidden_is_not()
    {
        FeatureAccess.Resolve(Key, null, FeatureState.Locked).IsVisible.Should().BeTrue();
        FeatureAccess.Resolve(Key, null, FeatureState.Hidden).IsVisible.Should().BeFalse();
    }

    [Fact]
    public void Nothing_in_the_catalogue_defaults_to_inherit()
    {
        // Inherit is not an answer. A catalogue entry defaulting to it would resolve to itself and
        // leave callers with a state they cannot render or enforce.
        PlatformFeatures.All.Should().OnlyContain(f => f.Default != FeatureState.Inherit);
    }

    [Fact]
    public void Every_catalogue_entry_is_described_in_both_languages()
    {
        // The panel is bilingual, and a feature nobody translated shows an English pitch to a
        // Persian customer on the one page whose whole job is persuading them.
        PlatformFeatures.All.Should().OnlyContain(f =>
            f.NameEn.Length > 0 && f.NameFa.Length > 0 && f.PitchEn.Length > 0 && f.PitchFa.Length > 0);
    }

    [Fact]
    public void Feature_keys_are_unique()
    {
        PlatformFeatures.All.Select(f => f.Key).Should().OnlyHaveUniqueItems();
    }

    // ------------------------------------------------------------- the sidebar

    private static IReadOnlyList<NavGroup> OneGatedItem() =>
    [
        new("build", [
            new("functions", "Functions", "Index", "code", Feature: Key),
            new("templates", "Templates", "Index", "shapes")
        ])
    ];

    [Fact]
    public void A_locked_feature_stays_in_the_sidebar()
    {
        var rows = NavigationMap.Draw(OneGatedItem(), _ => true, PanelMode.Advanced, _ => FeatureState.Locked);

        var entry = rows.Single().Entries.Should().ContainSingle(e => e.Item.Key == "functions").Subject;
        entry.Locked.Should().BeTrue("a customer who cannot see what they are not buying cannot ask for it");
    }

    [Fact]
    public void A_hidden_feature_leaves_the_sidebar_entirely()
    {
        var rows = NavigationMap.Draw(OneGatedItem(), _ => true, PanelMode.Advanced, _ => FeatureState.Hidden);

        rows.Single().Entries.Should().NotContain(e => e.Item.Key == "functions");
        rows.Single().Entries.Should().Contain(e => e.Item.Key == "templates");
    }

    [Fact]
    public void An_enabled_feature_is_an_ordinary_entry()
    {
        var rows = NavigationMap.Draw(OneGatedItem(), _ => true, PanelMode.Advanced, _ => FeatureState.Enabled);

        rows.Single().Entries.Should().ContainSingle(e => e.Item.Key == "functions")
            .Which.Locked.Should().BeFalse();
    }

    [Fact]
    public void A_missing_capability_still_hides_rather_than_locks()
    {
        // The older rule, unchanged. Entitlements added a second axis; they did not redefine the
        // first one, and a capability the caller lacks has nothing to sell them.
        var rows = NavigationMap.Draw(
            [new NavGroup("platform", [new NavItem("plans", "Plans", "Index", "credit-card", "plans.manage")])],
            _ => false, PanelMode.Advanced, _ => FeatureState.Enabled);

        rows.Should().BeEmpty();
    }

    [Fact]
    public void A_group_left_with_nothing_visible_disappears()
    {
        var rows = NavigationMap.Draw(
            [new NavGroup("build", [new NavItem("functions", "Functions", "Index", "code", Feature: Key)])],
            _ => true, PanelMode.Advanced, _ => FeatureState.Hidden);

        rows.Should().BeEmpty("an empty group heading is a label over nothing");
    }
}
