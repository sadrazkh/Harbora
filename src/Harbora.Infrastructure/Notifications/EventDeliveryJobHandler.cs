using Harbora.Application.Abstractions;
using Harbora.Domain.Jobs;

namespace Harbora.Infrastructure.Notifications;

/// <summary>
/// Runs one queued <c>EventDelivery</c> row (P6, 2026-08-20 platform-options plan). A thin
/// <see cref="IJobHandler"/> adapter — the same shape as <c>NotificationDeliveryJobHandler</c> — over
/// <see cref="IEventPublisher.ExecuteQueuedDeliveryAsync"/>, which is where the actual work and the
/// unit tests both live.
/// </summary>
public sealed class EventDeliveryJobHandler(IEventPublisher events) : IJobHandler
{
    public JobKind Kind => JobKind.EventDelivery;

    public Task ExecuteAsync(Guid targetId, CancellationToken ct) =>
        events.ExecuteQueuedDeliveryAsync(targetId, ct);
}
