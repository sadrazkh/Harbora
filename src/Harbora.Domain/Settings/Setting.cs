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
}
