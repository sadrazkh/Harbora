namespace Harbora.Infrastructure.Navigation;

/// <summary>
/// Takes the Billing entry out of the sidebar on an install that never switched billing on.
///
/// <para>
/// The entry is contributed unconditionally by <see cref="NavigationMap"/>, because the platform
/// has billing and the map is a static list of what the platform has. Whether a particular provider
/// <i>sells</i> by the hour is a different question, answered by <c>Billing:Enabled</c>, and it ships
/// false — so without this every tenant on every upgraded install is offered a Billing tab by a
/// provider who has not decided to charge anybody. That is the dead destination the sidebar's own
/// rule already forbids, arriving through a setting rather than through a missing page.
/// </para>
///
/// <para>
/// <b>Only the entry folds; the route does not close.</b> <c>BillingController</c> is deliberately
/// not gated — its remarks say why, and it is the same asymmetry the suspension draws between
/// stopping somebody and letting them back in: a workspace that was billed before the switch was
/// turned off keeps its history, and anybody holding a link to <c>/billing</c> can still read it.
/// This is item 23 of the do-not-change list applied to a feature flag instead of to a panel mode.
/// </para>
///
/// <para>
/// A filter rather than the <c>Augment</c> shape the backup and sync modules use, and the difference
/// is the difference between the things. A module is code that may not be installed, so its entry is
/// added when it is; billing is always here, and what varies is whether this provider has said yes.
/// </para>
/// </summary>
public static class BillingNavigation
{
    /// <summary>The sidebar key <see cref="NavigationMap"/> files the bill under.</summary>
    public const string ItemKey = "billing";

    /// <summary>
    /// The map as one install should see it. Empty groups are left for
    /// <see cref="NavigationMap.VisibleTo(IReadOnlyList{NavGroup}, Func{string, bool})"/> to drop,
    /// which every caller of this runs afterwards — a second copy of that rule here would be one
    /// nothing could reach, and the overview group holds the dashboard besides.
    /// </summary>
    public static IReadOnlyList<NavGroup> Fold(IReadOnlyList<NavGroup> groups, bool billingEnabled)
    {
        ArgumentNullException.ThrowIfNull(groups);

        if (billingEnabled) return groups;

        return groups
            .Select(group => group with { Items = group.Items.Where(i => i.Key != ItemKey).ToList() })
            .ToList();
    }
}
