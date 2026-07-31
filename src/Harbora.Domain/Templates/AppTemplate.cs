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

    /// <summary>
    /// The workspace that wrote this one. Null for the templates Harbora ships, which belong to the
    /// platform rather than to any tenant.
    /// </summary>
    public Guid? WorkspaceId { get; set; }

    public TemplateStatus Status { get; set; } = TemplateStatus.Private;

    /// <summary>Why an admin sent it back. Shown to its author — a rejection with no reason is a wall.</summary>
    public string? ReviewNote { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }
}

/// <summary>
/// How far a template has got towards being offered to everyone.
///
/// A template runs someone else's container image inside a tenant's network, so appearing in the
/// shared catalog is a decision a person makes, not a side effect of saving a form.
/// </summary>
public enum TemplateStatus
{
    /// <summary>Usable by the workspace that wrote it, and invisible to everyone else.</summary>
    Private = 0,
    /// <summary>Waiting for an admin to look at it. Still private in the meantime.</summary>
    Submitted = 1,
    /// <summary>In the shared catalog.</summary>
    Approved = 2,
    /// <summary>Sent back with a reason. Still usable by its author.</summary>
    Rejected = 3
}
