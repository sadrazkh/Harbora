using Harbora.Domain.Common;

namespace Harbora.Domain.Notifications;

/// <summary>
/// One durable row per (event × subscription) attempt (P6, 2026-08-20 platform-options plan) — the
/// same idea <c>NotificationDelivery</c> already established for alert dispatch: a delivery belongs
/// to the message it carried, not to the subscription, so an old failure is never overwritten by a
/// later, unrelated attempt the way <c>Alert.LastError</c>/<c>EventSubscription.LastError</c> (kept
/// only as a convenience copy) would be on their own.
///
/// <para>
/// <b>Every attempt writes a row with its real outcome</b> — the plan's own words, and the reason
/// <see cref="Status"/> reuses <see cref="NotificationDeliveryStatus"/> rather than inventing a
/// parallel three-state enum: <see cref="NotificationDeliveryStatus.Pending"/> before the job runs
/// (or while it is waiting out a retry backoff), <see cref="NotificationDeliveryStatus.Sent"/> once
/// the target accepted it, <see cref="NotificationDeliveryStatus.Failed"/> once every retry this kind
/// of work is allowed has been spent. <see cref="NotificationDeliveryStatus.Suppressed"/> is legal
/// too — a subscription deleted between enqueue and the job running has nowhere left to send to,
/// which is the same "never attempted, no channel to try" fact <c>NotificationDeliveryPurpose</c>'s
/// own Suppressed members already describe, not a Failed one.
/// </para>
/// </summary>
public class EventDelivery : BaseEntity
{
    /// <summary>Denormalised from the subscription for the same reason <c>Deployment.WorkspaceId</c>
    /// is: a background dispatcher scopes explicitly by this column
    /// (<c>IgnoreQueryFilters</c> + <c>WorkspaceId ==</c>) rather than joining through a filtered
    /// parent, so a subscription momentarily missing (or another tenant's id guessed) cannot surface
    /// this row.</summary>
    public Guid WorkspaceId { get; set; }

    public Guid SubscriptionId { get; set; }

    /// <summary>Exactly one bit — the single event this delivery is for, never a mask.</summary>
    public EventKind Event { get; set; }

    /// <summary>
    /// The stable id carried in the JSON payload's own <c>id</c> field, so a consumer that received
    /// this delivery twice (a retried job, a redelivered webhook) can dedupe on it. Minted once, at
    /// enqueue, and never changes across retries of the same delivery.
    /// </summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>
    /// The exact JSON body sent (unsigned, unencrypted) — kept for the delivery log and for a
    /// consumer's own debugging ("what did you actually send me"). Not secret: it carries workspace
    /// and resource names/ids, the same facts already visible elsewhere in the panel, never a
    /// credential — the signing secret itself is on <see cref="EventSubscription.EncryptedSigningSecret"/>,
    /// encrypted, and never written here.
    /// </summary>
    public string PayloadJson { get; set; } = string.Empty;

    public NotificationDeliveryStatus Status { get; set; } = NotificationDeliveryStatus.Pending;

    /// <summary>How many times a send has actually been attempted.</summary>
    public int Attempts { get; set; }

    /// <summary>The target's HTTP response code, when the channel is a webhook and a response was
    /// received. Null for Telegram (a channel judged elsewhere) and for a send that never got an
    /// answer at all (timeout, DNS failure, connection refused).</summary>
    public int? HttpStatusCode { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>Why the most recent attempt failed, or null once it succeeded. Never cleared by a
    /// different delivery's outcome — only by this row's own next attempt.</summary>
    public string? Error { get; set; }
}
