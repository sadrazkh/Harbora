using Harbora.Domain.Authorization;
using Harbora.Domain.Features;
using Harbora.Domain.Identity;

namespace Harbora.Infrastructure.Navigation;

/// <summary>One destination in the sidebar.</summary>
/// <param name="Key">Stable identifier, also the translation key.</param>
/// <param name="Capability">Null when reading the section needs no action capability.</param>
/// <param name="Advanced">
/// True for a destination that only belongs in Advanced mode. Simple mode hides it; it is never
/// removed, and the route behind it keeps working for anyone who has the link.
/// </param>
/// <param name="Feature">
/// The entitlement this destination belongs to, or null for one every workspace has.
///
/// <para>
/// A capability and a feature are filtered differently on purpose. A capability the caller does not
/// have is <b>hidden</b>, because there is nothing to offer them — that is the rule this map has
/// always followed. An entitlement they do not have is <b>shown locked</b>, because the entire point
/// of selling a tier is that a customer can see what they are not getting and ask for it. Only
/// <c>Hidden</c> takes an entry away, and that is the operator saying they do not sell it at all.
/// </para>
/// </param>
public sealed record NavItem(
    string Key, string Controller, string Action, string Icon,
    string? Capability = null, bool Advanced = false, string? Feature = null);

/// <summary>One destination as it should be drawn: the item, and whether it is reachable.</summary>
public sealed record NavEntry(NavItem Item, bool Locked);

/// <summary>A group whose items carry their locked state.</summary>
public sealed record NavRow(string Key, IReadOnlyList<NavEntry> Entries);

/// <summary>A labelled run of destinations.</summary>
public sealed record NavGroup(string Key, IReadOnlyList<NavItem> Items);

/// <summary>
/// The sidebar, as data.
///
/// Grouped the way the mockups group things, and containing every section Harbora actually has —
/// nothing functional was dropped to match a picture, and nothing was invented to fill one. Billing
/// was left out of this list for exactly that reason for as long as there was no billing; it is here
/// now because there is one, and the page behind it renders a balance and a per-resource breakdown
/// out of real ledger rows.
/// </summary>
public static class NavigationMap
{
    public static IReadOnlyList<NavGroup> All { get; } =
    [
        new("overview", [
            new("dashboard", "Home", "Index", "layout-dashboard"),
            // N3 (2026-08-16 notification-system spec): everyone's own inbox, reachable from the
            // bell on every page and from here — no capability, since it shows only the caller's own
            // rows regardless of role.
            new("notifications", "Notifications", "Index", "bell"),
            new("workspaces", "Workspaces", "Index", "building-2"),
            // No capability, and that is the decision rather than an oversight: this is the page
            // somebody opens when their app has stopped and they want to know why. A bill only a
            // Workspace Admin can read is a bill that gets asked for by email instead, and it shows
            // nothing anybody in the workspace could not infer from what they are already running.
            new("billing", "Billing", "Index", "wallet")
        ]),
        new("deploy", [
            new("applications", "Apps", "Index", "boxes"),
            new("services", "Databases", "Index", "layers"),
            new("deployments", "Deployments", "Index", "rocket")
        ]),
        new("connect", [
            // Not Advanced any more. It is the page that draws the private network each environment
            // runs on and the internal address every service answers at — the one fact somebody
            // needs when wiring two services together, and the one they otherwise guess. Hiding it
            // in Advanced meant the people most confused by the boundary were the ones who could
            // not see it, and the move that resolves a cross-network attach lived there too.
            new("networks", "Networks", "Index", "network"),
            new("domains", "Domains", "Index", "globe"),
            new("routing", "Routes", "Index", "route", Capabilities.RoutesManage, Advanced: true)
        ]),
        new("data", [
            new("mail", "Mail", "Index", "mail"),
            new("backups", "Backups", "Index", "archive"),
            // Object storage sits beside backups rather than under Deploy: it is where data lives,
            // and the page says so itself when no S3 server is configured — which is why it is a
            // permanent entry rather than one contributed by a feature flag like sync's.
            new("storage", "Storage", "Index", "hard-drive"),
            // Sub-project 9 (2026-08-20 platform-options plan): workspace-level env-var groups an app
            // attaches to — shared config, so it sits beside the other data-shaped destinations rather
            // than under Deploy where a single app's own settings live.
            new("config-groups", "ConfigGroups", "Index", "layers-3", Capabilities.AppsEnv)
        ]),
        // AI sits in its own group rather than under Insight: it is a service somebody uses, not a
        // report they read, and burying it under monitoring is how a feature goes unnoticed.
        new("intelligence", [
            // Advanced-only, and this is the one entry here where that is a statement about
            // confidence rather than about specialism.
            //
            // Everything around the gateway is built and covered — plans, keys, routing, circuit
            // breaking, sliding-window rate limits, metering, the SSRF guard on a stored base URL —
            // and not one request has ever been made from this codebase to a real model provider.
            // Nothing proves the last hop. That is a fine thing to offer somebody who went looking
            // for it; it is not a fine thing to put in front of a person who chose the simple panel
            // precisely because they want the parts that just work.
            //
            // Folded, not removed: /ai still answers in both modes, so a link in a runbook or a
            // support message works either way. Take the Advanced flag off once one live
            // round-trip has been made and recorded (HARBORA-0054).
            new("ai", "Ai", "Index", "sparkles", Advanced: true),
            // Administering the AI service is a different job from using it: providers, tokens,
            // pricing and plans. It needs the platform capability and belongs in Advanced.
            new("ai-admin", "AiAdmin", "Index", "sliders-horizontal", Capabilities.PlatformManage, Advanced: true)
        ]),
        new("insight", [
            new("monitoring", "Monitoring", "Index", "activity"),
            // P5 (2026-08-17 app-environment-management design): every durable job this workspace
            // owns. No capability, like /notifications — it shows only the caller's own workspace's
            // rows (Job carries no query filter; ActivityController filters by hand), so there is
            // nothing a role could additionally restrict.
            new("activity-jobs", "Activity", "Index", "list-checks"),
            new("audit", "Audit", "Index", "scroll-text", Advanced: true)
        ]),
        new("build", [
            // Sits under Build rather than Deploy: writing the code is the act, and the deploy that
            // follows is Harbora's business rather than the author's.
            new("functions", "Functions", "Index", "code", Feature: Harbora.Domain.Features.PlatformFeatures.Functions),
            new("templates", "Templates", "Index", "shapes"),
            // Deciding which versions customers are offered is a different job from installing one.
            new("template-versions", "TemplateVersions", "Index", "layers", Capabilities.PlatformManage, Advanced: true),
            new("git", "Git", "Index", "git-branch", Capabilities.GitManage, Advanced: true)
        ]),
        new("platform", [
            new("users", "Users", "Index", "users", Capabilities.TenantsManage),
            new("vouchers", "Vouchers", "Index", "coins", Capabilities.TenantsManage),
            new("billing-runs", "BillingRuns", "Index", "history", Capabilities.TenantsManage),
            // What the platform earns, who burns most, whose wallet dies next — a cross-tenant read
            // of the same ledger the billing runs above write, so it lives beside them.
            new("revenue", "AdminRevenue", "Index", "trending-up", Capabilities.TenantsManage),
            new("cloudflare", "Cloudflare", "Index", "shield-check", Capabilities.PlatformManage),
            new("servers", "Servers", "Index", "server", Capabilities.ServersManage),
            new("nodes", "Nodes", "Index", "cpu", Capabilities.ServersManage),
            new("plans", "Plans", "Index", "credit-card", Capabilities.PlansManage),
            // Who each feature is for. Beside Plans because that is where the decision is usually
            // being made, and it is the plan grid that this page edits.
            new("feature-admin", "Features", "Admin", "toggle-right", Capabilities.PlatformManage),
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

    /// <summary>
    /// The same filter, plus entitlements — and this is the one the sidebar draws.
    ///
    /// <para>
    /// The difference from every other filter here is that it does not only remove. An entitlement
    /// the workspace lacks comes back <b>present and locked</b>, because a customer who cannot see
    /// what they are not buying cannot ask for it. Only <see cref="FeatureState.Hidden"/> removes an
    /// entry, and that is the operator saying they do not sell it at all.
    /// </para>
    /// </summary>
    /// <param name="featureState">
    /// The verdict for one feature key. Anything that is not <see cref="FeatureState.Enabled"/> or
    /// <see cref="FeatureState.Hidden"/> — including a key nothing knows about — draws as locked,
    /// which fails towards showing a door that does not open rather than hiding one that does.
    /// </param>
    public static IReadOnlyList<NavRow> Draw(
        IReadOnlyList<NavGroup> groups,
        Func<string, bool> hasCapability,
        PanelMode mode,
        Func<string, FeatureState> featureState) =>
        VisibleTo(groups, hasCapability, mode)
            .Select(group => new NavRow(group.Key, group.Items
                .Select(item => new
                {
                    Item = item,
                    State = item.Feature is null ? FeatureState.Enabled : featureState(item.Feature)
                })
                .Where(x => x.State != FeatureState.Hidden)
                .Select(x => new NavEntry(x.Item, Locked: x.State != FeatureState.Enabled))
                .ToList()))
            .Where(row => row.Entries.Count > 0)
            .ToList();
}
