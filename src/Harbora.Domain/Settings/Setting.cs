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
}
