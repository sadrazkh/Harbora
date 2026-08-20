using Harbora.Domain.Common;

namespace Harbora.Domain.Notifications;

/// <summary>
/// A workspace's subscription to outbound platform events (P6, 2026-08-20 platform-options plan,
/// "Outbound event notifications: HTTP webhooks + Telegram"). Not a new channel — the owner's
/// decision was HTTP webhooks + Telegram, and both transports already exist as workspace-level
/// <c>Alert</c> channels (<c>Enums.cs</c>'s <see cref="AlertChannel"/>, target storage shape and
/// sending code in <c>NotificationService</c>). This row is the new half: which events a target
/// should hear about, and the delivery bookkeeping <see cref="EventDelivery"/> keeps for it.
/// </summary>
public class EventSubscription : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Reuses <see cref="AlertChannel"/> rather than a second channel enum — same reasoning as
    /// <see cref="EncryptedTarget"/> below. Only <see cref="AlertChannel.Webhook"/> and
    /// <see cref="AlertChannel.Telegram"/> are legal here; the owner's decision excluded Discord and
    /// email for event subscriptions even though <c>AlertChannel</c> itself carries both. Enforced by
    /// <c>EventSubscriptionsController</c>, not by the column — the same trust boundary
    /// <c>AlertsController</c> already draws around the same enum for the exact same reason (this
    /// stays one wire vocabulary for "a channel", not two).
    /// </summary>
    public AlertChannel Channel { get; set; }

    /// <summary>
    /// The target, encrypted via <c>ISecretProtector</c> — byte-for-byte the same JSON shapes
    /// <c>AlertsController.BuildTarget</c> already writes for these two channels
    /// (<c>{"url":"..."}</c> for Webhook, <c>{"botToken":"...","chatId":"..."}</c> for Telegram), so
    /// the exact same case-insensitive deserialization in <c>NotificationService</c>/the dispatcher
    /// reads either row's target without caring which table it came from.
    /// </summary>
    public string EncryptedTarget { get; set; } = string.Empty;

    /// <summary>
    /// Bitmask of <see cref="EventKind"/> this subscription hears about. See that type's own doc for
    /// why this is a mask rather than one bool column per event.
    /// </summary>
    public EventKind Events { get; set; } = EventKind.None;

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// The HMAC-SHA256 key every webhook payload to this subscription is signed with, encrypted at
    /// rest via the same <c>ISecretProtector</c> that protects <see cref="EncryptedTarget"/>. Shown to
    /// the caller once, at creation, and never again — the same "shown once" idiom
    /// <c>VolumeDownloadToken</c> and API tokens already use elsewhere in this codebase. Empty for a
    /// Telegram subscription, which has nothing to sign.
    /// </summary>
    public string EncryptedSigningSecret { get; set; } = string.Empty;

    /// <summary>When this subscription's target was last attempted — mirrors <c>Alert.LastAttemptAt</c>.</summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>
    /// Why the most recent delivery failed, or null. Mirrors <c>Alert.LastError</c> — the convenience
    /// copy a list page reads without joining <see cref="EventDelivery"/>, and the field
    /// <c>AttentionService</c> reads to feed a failing subscription into the dashboard's existing
    /// broken-channel path (<c>ChannelKind.EventSubscription</c>) the same way a failing
    /// <c>Alert</c> or backup delivery channel already does.
    /// </summary>
    public string? LastError { get; set; }
}
