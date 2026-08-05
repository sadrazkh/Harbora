using Harbora.Infrastructure.Navigation;

namespace Harbora.Modules.Sync.Infrastructure;

/// <summary>
/// Adds the sync entry to the sidebar, and only when the module is on.
///
/// <para>
/// Same rule as the backup module's: an entry leading to a page that says "disabled" is the dead
/// button the brief rules out, so the item is absent from the map rather than filtered at render.
/// </para>
/// </summary>
public static class SyncNavigation
{
    public const string ItemKey = "sync";
    private const string GroupKey = "data";

    public static IReadOnlyList<NavGroup> Augment(IReadOnlyList<NavGroup> groups, bool syncEnabled)
    {
        ArgumentNullException.ThrowIfNull(groups);

        if (!syncEnabled) return groups;

        var item = new NavItem(ItemKey, "SyncCenter", "Index", "refresh-cw");

        var augmented = groups.Select(group => group.Key == GroupKey
            ? group with { Items = [.. group.Items, item] }
            : group).ToList();

        return augmented.Any(g => g.Key == GroupKey)
            ? augmented
            : [.. augmented, new NavGroup(GroupKey, [item])];
    }
}
