using Harbora.Domain.Common;

namespace Harbora.Domain.Auditing;

/// <summary>An append-only record of a security-relevant action.</summary>
public class AuditLog : BaseEntity
{
    public Guid? UserId { get; set; }
    public string ActorEmail { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;      // "app.deploy", "user.login", "route.apply"
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? IpAddress { get; set; }

    /// <summary>Extra context as JSON. Secret values must be redacted by the caller.</summary>
    public string? MetadataJson { get; set; }
}
