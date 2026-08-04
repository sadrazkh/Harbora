using Harbora.Infrastructure.Navigation;

namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>
/// Adds the module's sidebar entry — and only when the module is switched on.
///
/// <para>
/// A navigation entry leading to a page that says "this feature is disabled" is the dead button the
/// brief rules out. So the item is not filtered at render time; it is not in the map at all until
/// <c>Features:Backup</c> is true.
/// </para>
/// <para>
/// Written as a transform over the existing map rather than as an edit to <c>NavigationMap.All</c>,
/// so the platform's navigation stays a static, reviewable list and the module's presence in it is
/// visibly conditional.
/// </para>
/// </summary>
public static class BackupNavigation
{
    /// <summary>Sidebar key, translation key, and the group it belongs beside the existing backups in.</summary>
    public const string ItemKey = "backup-center";
    private const string GroupKey = "data";

    public static IReadOnlyList<NavGroup> Augment(IReadOnlyList<NavGroup> groups, bool backupEnabled)
    {
        ArgumentNullException.ThrowIfNull(groups);

        if (!backupEnabled) return groups;

        var item = new NavItem(ItemKey, "BackupCenter", "Index", "shield-check");

        var augmented = groups.Select(group => group.Key == GroupKey
            ? group with { Items = [.. group.Items, item] }
            : group).ToList();

        // The group is expected to exist; if a future refactor renames it, the entry is appended as
        // its own group rather than silently vanishing.
        return augmented.Any(g => g.Key == GroupKey)
            ? augmented
            : [.. augmented, new NavGroup(GroupKey, [item])];
    }
}
