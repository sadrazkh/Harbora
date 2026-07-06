using Harbora.Domain.Common;

namespace Harbora.Domain.Templates;

/// <summary>
/// A one-click app definition. The manifest is data (JSON), not code, so new templates
/// can be added without recompiling. Built-in templates are seeded; users may add custom.
/// </summary>
public class AppTemplate : BaseEntity
{
    public string Key { get; set; } = string.Empty;       // "wordpress", "aspnet", "redis"
    public string Name { get; set; } = string.Empty;
    public string NameFa { get; set; } = string.Empty;    // localized display name
    public string Description { get; set; } = string.Empty;
    public string DescriptionFa { get; set; } = string.Empty;
    public string Category { get; set; } = "app";         // app | database | service | static
    public string? IconUrl { get; set; }

    /// <summary>
    /// JSON manifest describing image/compose, ports, volumes, env schema (with defaults and
    /// which fields are secret) and required managed services. Consumed by the deployment engine.
    /// </summary>
    public string ManifestJson { get; set; } = "{}";

    public bool IsBuiltIn { get; set; }
    public bool IsEnabled { get; set; } = true;
}
