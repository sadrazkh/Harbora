using Harbora.Application.Abstractions;
using Harbora.Domain.Jobs;

namespace Harbora.Infrastructure.Notifications;

/// <summary>
/// Runs one queued <c>NotificationDelivery</c> row (N1, 2026-08-16 notification-system spec). A thin
/// <see cref="IJobHandler"/> adapter — the same shape as <c>FunctionInvokeJobHandler</c> — over
/// <see cref="INotificationService.ExecuteQueuedDeliveryAsync"/>, which is where the actual work and
/// the unit tests both live.
/// </summary>
public sealed class NotificationDeliveryJobHandler(INotificationService notifications) : IJobHandler
{
    public JobKind Kind => JobKind.NotificationDelivery;

    public Task ExecuteAsync(Guid targetId, CancellationToken ct) =>
        notifications.ExecuteQueuedDeliveryAsync(targetId, ct);
}
