using Harbora.Domain.Authorization;
using Harbora.Domain.Identity;

namespace Harbora.Infrastructure.Navigation;

/// <summary>One destination in the sidebar.</summary>
/// <param name="Key">Stable identifier, also the translation key.</param>
/// <param name="Capability">Null when reading the section needs no action capability.</param>
/// <param name="Advanced">
/// True for a destination that only belongs in Advanced mode. Simple mode hides it; it is never
/// removed, and the route behind it keeps working for anyone who has the link.
/// </param>
public sealed record NavItem(
    string Key, string Controller, string Action, string Icon,
    string? Capability = null, bool Advanced = false);

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
            new("networks", "Networks", "Index", "network", Advanced: true),
            new("domains", "Domains", "Index", "globe"),
            new("routing", "Routes", "Index", "route", Capabilities.RoutesManage, Advanced: true)
        ]),
        new("data", [
            new("backups", "Backups", "Index", "archive"),
            // Object storage sits beside backups rather than under Deploy: it is where data lives,
            // and the page says so itself when no S3 server is configured — which is why it is a
            // permanent entry rather than one contributed by a feature flag like sync's.
            new("storage", "Storage", "Index", "hard-drive")
        ]),
        // AI sits in its own group rather than under Insight: it is a service somebody uses, not a
        // report they read, and burying it under monitoring is how a feature goes unnoticed.
        new("intelligence", [
            new("ai", "Ai", "Index", "sparkles"),
            // Administering the AI service is a different job from using it: providers, tokens,
            // pricing and plans. It needs the platform capability and belongs in Advanced.
            new("ai-admin", "AiAdmin", "Index", "sliders-horizontal", Capabilities.PlatformManage, Advanced: true)
        ]),
        new("insight", [
            new("monitoring", "Monitoring", "Index", "activity"),
            new("audit", "Audit", "Index", "scroll-text", Advanced: true)
        ]),
        new("build", [
            new("templates", "Templates", "Index", "shapes"),
            // Deciding which versions customers are offered is a different job from installing one.
            new("template-versions", "TemplateVersions", "Index", "layers", Capabilities.PlatformManage, Advanced: true),
            new("git", "Git", "Index", "git-branch", Capabilities.GitManage, Advanced: true)
        ]),
        new("platform", [
            new("users", "Users", "Index", "users", Capabilities.TenantsManage),
            new("servers", "Servers", "Index", "server", Capabilities.ServersManage),
            new("nodes", "Nodes", "Index", "cpu", Capabilities.ServersManage),
            new("plans", "Plans", "Index", "credit-card", Capabilities.PlansManage),
            new("tenants", "Tenants", "Index", "building-2", Capabilities.TenantsManage),
            new("settings", "Settings", "Index", "settings"),
            // How the platform behaves for everybody, as opposed to the preferences on /settings.
            new("platform-settings", "AdminSettings", "Index", "settings-2", Capabilities.PlatformManage, Advanced: true)
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
        VisibleTo(groups, hasCapability, PanelMode.Advanced);

    /// <summary>
    /// The map for one caller in one mode.
    ///
    /// Simple hides the specialist destinations; it does not take them away. The routes stay live,
    /// so a bookmark, a link in a runbook or a support instruction all still work — and the person
    /// can switch to Advanced without having lost anything in the meantime.
    /// </summary>
    public static IReadOnlyList<NavGroup> VisibleTo(
        IReadOnlyList<NavGroup> groups, Func<string, bool> hasCapability, PanelMode mode) =>
        groups
            .Select(group => group with
            {
                Items = group.Items
                    .Where(i => i.Capability is null || hasCapability(i.Capability))
                    .Where(i => mode == PanelMode.Advanced || !i.Advanced)
                    .ToList()
            })
            .Where(group => group.Items.Count > 0)
            .ToList();
}
