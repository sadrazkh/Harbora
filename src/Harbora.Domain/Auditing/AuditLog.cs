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

    /// <summary>
    /// The <c>SupportSession</c> this row was written under, when a platform administrator was
    /// signed in as the customer at the time. Null for every ordinary action.
    ///
    /// <para>
    /// A column rather than a line of metadata JSON because the customer's own support-access page
    /// reads it: "what did support do while they were me" is an exact question, and answering it
    /// with a LIKE over a JSON blob is how it eventually answers the wrong one.
    /// </para>
    /// </summary>
    public Guid? SupportSessionId { get; set; }

    /// <summary>
    /// The administrator behind a support session's action. <see cref="UserId"/> keeps naming the
    /// customer's account the request actually ran as — both ids, because either one alone is a
    /// half-true sentence about who did this.
    /// </summary>
    public Guid? SupportAdminUserId { get; set; }
}
