using FluentAssertions;
using Harbora.Domain.Authorization;
using Harbora.Infrastructure.Navigation;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Which doors the sidebar advertises.
///
/// A menu that lists a section the viewer cannot open is not a cosmetic problem: it is the same
/// defect as a disabled button that still posts, seen from the other side. The filter is a rule
/// here rather than a chain of <c>@if (User.IsInRole(...))</c> in the layout, because the layout is
/// where that check silently stops being applied to the item somebody adds next.
/// </summary>
public class NavigationMapTests
{
    private static IReadOnlyList<NavItem> Items(IReadOnlyList<NavGroup> groups) =>
        groups.SelectMany(g => g.Items).ToList();

    [Fact]
    public void Everything_is_visible_to_someone_with_every_capability()
    {
        var visible = NavigationMap.VisibleTo(_ => true);

        Items(visible).Should().HaveCount(Items(NavigationMap.All).Count);
    }

    [Fact]
    public void A_section_needing_a_capability_is_hidden_without_it()
    {
        var visible = NavigationMap.VisibleTo(c => c != Capabilities.TenantsManage);

        Items(visible).Should().NotContain(i => i.Capability == Capabilities.TenantsManage);
    }

    [Fact]
    public void Open_sections_stay_visible_to_everyone()
    {
        // Reading is not an action capability. A viewer who can see the app list must still reach it.
        var visible = NavigationMap.VisibleTo(_ => false);

        Items(visible).Should().NotBeEmpty();
        Items(visible).Should().OnlyContain(i => i.Capability == null);
    }

    [Fact]
    public void A_group_whose_items_are_all_hidden_disappears()
    {
        // An empty group header is a heading over nothing, which reads as a broken page.
        //
        // Tested against a constructed map rather than the real one: every group in the real map
        // happens to contain an item needing no capability, so no amount of filtering empties one
        // and the same assertion there passes without ever reaching the guard. Mutation testing is
        // what caught that — deleting the guard broke nothing.
        IReadOnlyList<NavGroup> map =
        [
            new("gated", [new("only", "Tenants", "Index", "building-2", Capabilities.TenantsManage)]),
            new("open",  [new("always", "Home", "Index", "layout-dashboard")])
        ];

        var visible = NavigationMap.VisibleTo(map, _ => false);

        visible.Should().ContainSingle().Which.Key.Should().Be("open");
    }

    [Fact]
    public void Every_item_names_a_real_capability()
    {
        // A typo in a capability name hides a section forever and looks exactly like a permission
        // problem, which is the most expensive kind of bug to chase.
        Items(NavigationMap.All)
            .Where(i => i.Capability is not null)
            .Should().OnlyContain(i => Capabilities.All.Contains(i.Capability!));
    }

    [Fact]
    public void Keys_are_unique()
    {
        Items(NavigationMap.All).Select(i => i.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_billing_section_leads_to_a_page_that_is_actually_built()
    {
        // This test used to assert the opposite, and its reason was right at the time: the mockups
        // had a Billing entry, Harbora had no billing, and a menu item leading to an empty page is
        // the exact thing that redesign was meant to stop doing. The rule has not changed — only the
        // fact it was applied to. There is a wallet, an hourly charge and a per-resource breakdown
        // now, so the entry is earned; what is asserted is still "the sidebar does not offer a
        // destination that is not there", pointed at the controller that has to keep existing.
        Items(NavigationMap.All).Should().ContainSingle(i => i.Key == "billing")
            .Which.Controller.Should().Be("Billing");
    }

    [Fact]
    public void Reading_your_own_bill_needs_no_capability()
    {
        // Everybody in a workspace can see what it is running, so everybody may see what that costs.
        // Gating it behind an administrator's capability is how "why did my app stop" becomes an
        // email to support instead of a page somebody reads for themselves.
        Items(NavigationMap.All).Single(i => i.Key == "billing").Capability.Should().BeNull();
    }

    [Fact]
    public void Provider_billing_operations_are_first_class_navigation_destinations()
    {
        var operations = Items(NavigationMap.All)
            .Where(i => i.Key is "vouchers" or "billing-runs")
            .ToList();

        operations.Select(i => i.Controller).Should().BeEquivalentTo("Vouchers", "BillingRuns");
        operations.Should().OnlyContain(i => i.Capability == Capabilities.TenantsManage,
            "issuing money and retrying accounting are provider operations");
    }

    [Fact]
    public void Account_pages_do_not_depend_on_the_automatic_charging_switch()
    {
        // Billing:Enabled controls charging and suspension, not account visibility. Manual credit,
        // voucher redemption and ledger history remain useful while the automatic meter is off.
        Items(NavigationMap.All).Should().Contain(i => i.Key == "billing");
        typeof(NavigationMap).Assembly.GetType("Harbora.Infrastructure.Navigation.BillingNavigation")
            .Should().BeNull("there must be no feature-flag filter left to hide account management");
    }
}
