using Harbora.Domain.Common;

namespace Harbora.Domain.Settings;

/// <summary>
/// A single platform setting (key/value). Kept as rows rather than a config file so the
/// UI settings screen and the first-run wizard can persist without redeploying.
/// </summary>
public class Setting : BaseEntity
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsSecret { get; set; }
}

/// <summary>Well-known setting keys used across the platform.</summary>
public static class SettingKeys
{
    public const string SetupCompleted = "setup.completed";
    public const string PlatformName = "platform.name";

    // Platform SMTP — one account the panel itself sends from: password resets, invitations, and
    // the fallback for alert channels that name no server of their own. The password is stored
    // through ISecretProtector like every other credential.
    public const string SmtpHost = "smtp.host";
    public const string SmtpPort = "smtp.port";
    public const string SmtpUser = "smtp.user";
    public const string SmtpPassword = "smtp.password";
    public const string SmtpFrom = "smtp.from";
    public const string SmtpUseSsl = "smtp.use_ssl";
    public const string PlatformRootDomain = "platform.root_domain";
    public const string AcmeEmail = "acme.email";
    public const string DefaultCulture = "ui.default_culture";
    public const string TelemetryEnabled = "telemetry.enabled";

    /// <summary>
    /// Whether Harbora may ask public container registries about newer versions of the ready-made
    /// apps. Off unless set: it means outbound requests to a third party from this server, spending
    /// their anonymous rate limit, and that is an operator's decision rather than a default.
    /// </summary>
    public const string RegistryDiscoveryEnabled = "templates.registry_discovery";

    /// <summary>
    /// Which ready-made apps to put in front of people, in order. Comma-separated template keys.
    ///
    /// Empty means "the first few alphabetically", which is what the dashboard did with no way to
    /// change it — so the apps an operator most wants installed were wherever the alphabet put them.
    /// </summary>
    public const string FeaturedTemplates = "templates.featured";

    /// <summary>
    /// The resource plan preselected on every create form. Empty means no ceiling is preselected,
    /// which is what happened before — and an unlimited default is the one nobody chooses on
    /// purpose.
    /// </summary>
    public const string DefaultInstanceSize = "resources.default_size";

    /// <summary>Whether branch previews start switched on for a new Git-backed application.</summary>
    public const string PreviewsDefault = "apps.previews_default";

    /// <summary>
    /// Whether the ready-made apps shelf starts open, for people who have not chosen. Empty means
    /// the shipped answer, which is closed — it is a shelf of things to install, and it is in the
    /// way once somebody has installed them.
    /// </summary>
    public const string QuickStartDefault = "panel.quickstart_default";

    /// <summary>Whether the counts panel starts open. Empty means the shipped answer, which is open.</summary>
    public const string OverviewDefault = "panel.overview_default";

    /// <summary>
    /// Which versions of one database engine are offered, comma-separated and in the order they
    /// appear. Empty means the list Harbora ships with.
    ///
    /// The shipped list is two entries per engine, written in C#, so offering PostgreSQL 17 — or
    /// keeping 14 for an application that needs it — took a release. The applications had a version
    /// admin page and the databases beside them had nothing.
    /// </summary>
    public static string ServiceVersions(ManagedServiceType type) =>
        $"services.versions.{type.ToString().ToLowerInvariant()}";
}
