using Harbora.Domain.Common;
using Harbora.Domain.Notifications;

namespace Harbora.Web.ViewModels;

/// <summary>Backs <c>/notifications/webhooks</c> (P6, 2026-08-20 platform-options plan).</summary>
public sealed class EventSubscriptionsPageViewModel
{
    public List<EventSubscriptionRow> Subscriptions { get; set; } = [];
    public List<EventDeliveryRow> RecentDeliveries { get; set; } = [];

    /// <summary>The signing secret, shown exactly once — right after the subscription that owns it
    /// was created — and never again. Null on every other request.</summary>
    public string? NewSecret { get; set; }
}

public sealed record EventSubscriptionRow(
    Guid Id, string Name, AlertChannel Channel, EventKind Events, bool IsEnabled,
    DateTimeOffset? LastAttemptAt, string? LastError);

public sealed record EventDeliveryRow(
    Guid Id, string SubscriptionName, EventKind Event, NotificationDeliveryStatus Status,
    int? HttpStatusCode, string? Error, DateTimeOffset? LastAttemptAt, int Attempts);
