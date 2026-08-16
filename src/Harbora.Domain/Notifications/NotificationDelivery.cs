using Harbora.Domain.Common;

namespace Harbora.Domain.Notifications;

/// <summary>
/// One durable row per (message × destination) — N1, 2026-08-16 notification-system spec, "a delivery
/// that survives a refusal".
///
/// <para>
/// Before this, the only trace of an attempt was <c>Alert.LastError</c> — one column on the rule,
/// overwritten by the next attempt to that same rule, whoever it was for. A critical alert failing at
/// 07:00 and a test message succeeding at noon left nothing: the column read <c>null</c>, and the
/// morning's failure was gone. This row is what makes that impossible by construction — it belongs to
/// <i>this</i> message, not to the rule, and nothing about a later, unrelated delivery can touch it.
/// <c>Alert.LastAttemptAt</c>/<c>LastError</c> are still written, best-effort, as a convenience for the
/// alerts list; they are no longer the record.
/// </para>
///
/// <para>
/// Covers two shapes with one table rather than two (§7 Q3(b) — fold transactional email into the
/// outbox): <see cref="Purpose"/> <see cref="NotificationDeliveryPurpose.AlertDispatch"/> and
/// <see cref="NotificationDeliveryPurpose.NoRecipientFallback"/> carry <see cref="AlertId"/> or
/// <see cref="WorkspaceId"/> and reuse the four channel senders in <c>NotificationService</c>; every
/// other purpose is a platform email — password reset, email verification, a workspace or platform
/// invite — that used to be sent inline from a controller action and is now queued exactly the same
/// way, so there is one delivery path, one retry story, and one place a person can ask "was that sent".
/// </para>
/// </summary>
public class NotificationDelivery : BaseEntity
{
    /// <summary>
    /// The workspace this concerns, for <see cref="NotificationDeliveryPurpose.AlertDispatch"/>,
    /// <see cref="NotificationDeliveryPurpose.NoRecipientFallback"/> and
    /// <see cref="NotificationDeliveryPurpose.WorkspaceInvite"/>. Null for the account-level purposes
    /// (a password reset or a platform invite belongs to no workspace) — deliberately unfiltered by
    /// EF, like <c>Job</c>, since a null workspace would otherwise pass any "belongs to nobody" filter
    /// for every tenant at once. Callers that need one workspace's rows filter by it explicitly.
    /// </summary>
    public Guid? WorkspaceId { get; set; }

    public NotificationDeliveryPurpose Purpose { get; set; }

    /// <summary>
    /// The channel this went out on. Always <see cref="AlertChannel.Email"/> for every purpose except
    /// <see cref="NotificationDeliveryPurpose.AlertDispatch"/>, where it is copied from the matched
    /// rule at enqueue time so the row's own history does not change if the rule's channel is edited
    /// afterwards.
    /// </summary>
    public AlertChannel Channel { get; set; }

    /// <summary>The matched rule, for <see cref="NotificationDeliveryPurpose.AlertDispatch"/> only —
    /// its channel and target are read from there when the job runs.</summary>
    public Guid? AlertId { get; set; }

    /// <summary>
    /// Where a purpose with no <see cref="AlertId"/> is sent — a workspace admin's address for
    /// <see cref="NotificationDeliveryPurpose.NoRecipientFallback"/>, or the account's address for
    /// every transactional purpose.
    /// </summary>
    public string? RecipientAddress { get; set; }

    /// <summary>Not secret; shown in the delivery log unencrypted.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// Encrypted at rest with the same <c>ISecretProtector</c> that already protects
    /// <c>Alert.EncryptedTarget</c>. A transactional body carries a one-time link — a password-reset
    /// or invite token in the clear — and this is the one field on the row that can hold one; nothing
    /// that reads the delivery log decrypts it.
    /// </summary>
    public string EncryptedBody { get; set; } = string.Empty;

    public AlertSeverity Severity { get; set; } = AlertSeverity.Info;

    public NotificationDeliveryStatus Status { get; set; } = NotificationDeliveryStatus.Pending;

    /// <summary>How many times a channel attempt has actually been made. Kept in step with the
    /// backing <c>Job.Attempts</c> — both are incremented once per handler execution.</summary>
    public int Attempts { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>
    /// Why the most recent attempt failed, or null. Unlike <c>Alert.LastError</c> this is never
    /// cleared by a later, different delivery succeeding — only by <i>this</i> row eventually
    /// succeeding itself.
    /// </summary>
    public string? LastError { get; set; }
}
