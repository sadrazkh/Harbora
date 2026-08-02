using Harbora.Domain.Authorization;

namespace Harbora.Infrastructure.Navigation;

/// <summary>One destination in the sidebar.</summary>
/// <param name="Key">Stable identifier, also the translation key.</param>
/// <param name="Capability">Null when reading the section needs no action capability.</param>
public sealed record NavItem(string Key, string Controller, string Action, string Icon, string? Capability = null);

/// <summary>A labelled run of destinations.</summary>
public sealed record NavGroup(string Key, IReadOnlyList<NavItem> Items);

/// <summary>
/// The sidebar, as data.
///
/// Grouped the way the mockups group things, and containing every section Harbora actually has —
/// nothing functional was dropped to match a picture, and nothing was invented to fill one. There
/// is deliberately no Billing item: there is no billing.
/// </summary>
public static class NavigationMap
{
    public static IReadOnlyList<NavGroup> All { get; } =
    [
        new("overview", [
            new("dashboard", "Home", "Index", "layout-dashboard")
        ]),
        new("deploy", [
            new("applications", "Apps", "Index", "boxes"),
            new("services", "Databases", "Index", "layers"),
            new("deployments", "Deployments", "Index", "rocket")
        ]),
        new("connect", [
            new("networks", "Networks", "Index", "network"),
            new("domains", "Domains", "Index", "globe"),
            new("routing", "Routes", "Index", "route", Capabilities.RoutesManage)
        ]),
        new("data", [
            new("backups", "Backups", "Index", "archive")
        ]),
        new("insight", [
            new("monitoring", "Monitoring", "Index", "activity"),
            new("audit", "Audit", "Index", "scroll-text")
        ]),
        new("build", [
            new("templates", "Templates", "Index", "shapes"),
            new("git", "Git", "Index", "git-branch", Capabilities.GitManage)
        ]),
        new("platform", [
            new("users", "Users", "Index", "users", Capabilities.TenantsManage),
            new("servers", "Servers", "Index", "server", Capabilities.ServersManage),
            new("plans", "Plans", "Index", "credit-card", Capabilities.PlansManage),
            new("tenants", "Tenants", "Index", "building-2", Capabilities.TenantsManage),
            new("settings", "Settings", "Index", "settings")
        ])
    ];

    /// <summary>
    /// The map as one caller may see it. Items are hidden rather than disabled: a sidebar that
    /// lists a locked door is a sidebar people learn to distrust.
    /// </summary>
    public static IReadOnlyList<NavGroup> VisibleTo(Func<string, bool> hasCapability) =>
        VisibleTo(All, hasCapability);

    /// <summary>
    /// The same filter over any map. Public because the empty-group guard below cannot be reached
    /// through <see cref="All"/> — every group there happens to hold one item needing no
    /// capability — and a guard no test can reach is a guard nobody knows is broken.
    /// </summary>
    public static IReadOnlyList<NavGroup> VisibleTo(
        IReadOnlyList<NavGroup> groups, Func<string, bool> hasCapability) =>
        groups
            .Select(group => group with
            {
                Items = group.Items.Where(i => i.Capability is null || hasCapability(i.Capability)).ToList()
            })
            .Where(group => group.Items.Count > 0)
            .ToList();
}
