using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Notifications;

namespace Harbora.Infrastructure.Notifications;

/// <summary>
/// Queues one transactional email onto the same outbox N1 built for alert deliveries (2026-08-16
/// notification-system spec §7 Q3(b) — fold transactional email into the outbox). Password resets,
/// email verification and both kinds of invite used to be sent inline, with a try/catch around a
/// synchronous SMTP call and no record either way; this is the one call site all four now share, so
/// there is one delivery path, one retry story, and one place a person can ask "was that sent".
///
/// <para>
/// Deliberately a plain <c>Add</c> and nothing more: the caller's own <c>SaveChangesAsync</c> —
/// already happening in every one of these actions, to persist the token/invitation row this email
/// is about — covers the delivery row in the same request. The caller must still enqueue the job
/// itself (<c>IJobQueue.EnqueueAsync(JobKind.NotificationDelivery, delivery.Id, delivery.WorkspaceId, ct)</c>) after that
/// save, the same two-step every other N1 caller follows.
/// </para>
/// </summary>
public static class OutboxMail
{
    public static NotificationDelivery Queue(
        HarboraDbContext db, ISecretProtector protector, NotificationDeliveryPurpose purpose,
        string to, string subject, string body, Guid? workspaceId = null)
    {
        var delivery = new NotificationDelivery
        {
            WorkspaceId = workspaceId,
            Purpose = purpose,
            Channel = AlertChannel.Email,
            RecipientAddress = to,
            Subject = subject,
            EncryptedBody = protector.Protect(body)
        };
        db.NotificationDeliveries.Add(delivery);
        return delivery;
    }
}
