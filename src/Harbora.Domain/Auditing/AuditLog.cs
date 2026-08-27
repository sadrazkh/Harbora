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

    /// <summary>
    /// The workspace this action happened in, or null when it genuinely has none (doc 10 §2.13,
    /// HARBORA-0056). Written once, at the time of the action, by whichever caller of
    /// <c>IAuditLogger.LogAsync</c> knows it — never guessed here and never defaulted from the
    /// current request's workspace at the sink, because a background job or a platform-admin action
    /// has no workspace to default to, and stamping one on anyway would misattribute it.
    ///
    /// <para>
    /// Null covers two different things on purpose, and both are honest: a row written before this
    /// column existed (no backfill was attempted — a guessed workspace is worse than an admitted
    /// gap), and a row for an action that is platform-level by nature (a platform setting, a node
    /// enrollment, a sign-in before any workspace is chosen). Neither is hidden and neither is
    /// attributed to a workspace it does not belong to; the workspace-scoped reader
    /// (<c>WorkspacesController.AuditLog</c>) simply cannot show what was never stamped, and says so.
    /// </para>
    ///
    /// <para>
    /// Deliberately carries no global query filter (<c>HarboraDbContext</c>'s <c>HasQueryFilter</c>
    /// calls never mention it) — the same choice made for <c>Job</c> and <c>NotificationDelivery</c>,
    /// whose own remarks explain why: an "own it or nothing" filter over a column that is null for
    /// most platform rows would pass every one of those rows to whichever workspace happened to be
    /// ambient, which is worse than no filter at all. Every reader — the platform-wide
    /// <c>AuditController</c> and the workspace-scoped one — filters explicitly instead.
    /// </para>
    /// </summary>
    public Guid? WorkspaceId { get; set; }
}
