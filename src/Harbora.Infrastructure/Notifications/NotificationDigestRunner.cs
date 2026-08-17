using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Jobs;
using Harbora.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Notifications;

/// <summary>
/// Folds pending <see cref="NotificationDigestEntry"/> rows into one email per person, and sends the
/// opt-in weekly summary — N5, 2026-08-16 notification-system spec, "noise control".
///
/// <para>
/// <b>A digest window that passes without this running loses nothing.</b> The whole class does one
/// thing: move an entry from "waiting" (<see cref="NotificationDigestEntry.DeliveryId"/> null) to
/// "inside a durable <see cref="NotificationDelivery"/>" (N1). There is no notion of "too late" here —
/// <see cref="RunDigestAsync"/> simply folds in whatever is still pending, however long it has been
/// waiting, so a scheduler that missed three ticks in a row produces one larger digest on the fourth
/// rather than three silent gaps. Once folded, N1's own retry/backoff/delivery-log machinery owns
/// what happens next, exactly like every other delivery.
/// </para>
///
/// <para>
/// <b>One save per person, not two.</b> <see cref="NotificationDelivery.Id"/> is assigned client-side
/// the moment the object is constructed (<c>BaseEntity.Id</c>'s own initializer), so the delivery row
/// and every entry's <see cref="NotificationDigestEntry.DeliveryId"/> it belongs to are set before
/// either is written, and <c>SaveChangesAsync</c> commits both together. A crash between constructing
/// the delivery and saving loses the whole batch atomically — the entries stay pending and are folded
/// into a fresh delivery next run — rather than a two-step save leaving a delivery created with no
/// entry ever pointed at it, or an entry marked flushed into a delivery that was never actually saved.
/// </para>
/// </summary>
public sealed class NotificationDigestRunner(
    HarboraDbContext db,
    ISecretProtector protector,
    IJobQueue jobQueue,
    ISystemClock clock,
    INotificationTemplateCatalog catalog)
{
    /// <summary>How long a weekly report opt-in waits between reports.</summary>
    private static readonly TimeSpan WeeklyReportPeriod = TimeSpan.FromDays(7);

    /// <summary>
    /// One pass: every still-pending digest entry, grouped by recipient, folded into one
    /// <see cref="NotificationDeliveryPurpose.PersonalDigest"/> delivery each.
    /// </summary>
    public async Task RunDigestAsync(CancellationToken ct)
    {
        var pending = await db.NotificationDigestEntries
            .Where(e => e.DeliveryId == null)
            .ToListAsync(ct);

        if (pending.Count == 0) return;

        var userIds = pending.Select(e => e.UserId).Distinct().ToList();
        var users = await db.Users
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Email, u.PreferredCulture })
            .ToDictionaryAsync(u => u.Id, ct);

        foreach (var group in pending.GroupBy(e => e.UserId))
        {
            // The user was deleted between the entry being queued and this run — nothing to fold
            // into, and nobody to send to. The entries stay pending forever in that case, the same
            // orphan a deleted user's other rows already become; not this class's concern to clean up.
            if (!users.TryGetValue(group.Key, out var user)) continue;

            var entries = group.ToList();
            var lines = entries.Select(e => new DigestLine(e.Title, e.Body, e.Severity)).ToList();
            var rendered = catalog.RenderDigest(lines, user.PreferredCulture);

            var delivery = new NotificationDelivery
            {
                Purpose = NotificationDeliveryPurpose.PersonalDigest,
                Channel = AlertChannel.Email,
                RecipientAddress = user.Email,
                Severity = lines.Max(l => l.Severity),
                Subject = rendered.Subject,
                EncryptedBody = protector.Protect(ChannelBody.Encode(rendered.TextBody, rendered.HtmlBody))
            };
            db.NotificationDeliveries.Add(delivery);

            foreach (var entry in entries) entry.DeliveryId = delivery.Id;

            await db.SaveChangesAsync(ct);
            await jobQueue.EnqueueAsync(JobKind.NotificationDelivery, delivery.Id, ct);
        }
    }

    /// <summary>
    /// One pass: every opted-in user whose last report is missing or at least
    /// <see cref="WeeklyReportPeriod"/> old gets a fresh one, counting their own
    /// <c>UserNotification</c> rows over the period just closed.
    ///
    /// <para>
    /// Sent even when the count is all zeroes — "nothing happened this week" is itself the report, and
    /// a rule that skips empty weeks would make <c>LastWeeklyReportAt</c> mean two different things
    /// depending on whether anything fired, which is a harder contract to reason about than "every
    /// opted-in person hears from this every seven days, unconditionally".
    /// </para>
    /// </summary>
    public async Task RunWeeklyReportAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var cutoff = now - WeeklyReportPeriod;

        var due = await db.Users
            .Where(u => u.WeeklyReportOptIn && (u.LastWeeklyReportAt == null || u.LastWeeklyReportAt <= cutoff))
            .ToListAsync(ct);

        foreach (var user in due)
        {
            var periodStart = user.LastWeeklyReportAt ?? now - WeeklyReportPeriod;

            var counts = await db.UserNotifications
                .Where(n => n.UserId == user.Id && n.CreatedAt >= periodStart && n.CreatedAt <= now)
                .GroupBy(n => n.Severity)
                .Select(g => new { Severity = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            int CountOf(AlertSeverity s) => counts.FirstOrDefault(c => c.Severity == s)?.Count ?? 0;

            var summary = new WeeklyReportSummary(
                CountOf(AlertSeverity.Critical), CountOf(AlertSeverity.Warning), CountOf(AlertSeverity.Info),
                periodStart, now);
            var rendered = catalog.RenderWeeklyReport(summary, user.PreferredCulture);

            var delivery = new NotificationDelivery
            {
                Purpose = NotificationDeliveryPurpose.WeeklyReport,
                Channel = AlertChannel.Email,
                RecipientAddress = user.Email,
                Severity = AlertSeverity.Info,
                Subject = rendered.Subject,
                EncryptedBody = protector.Protect(ChannelBody.Encode(rendered.TextBody, rendered.HtmlBody))
            };
            db.NotificationDeliveries.Add(delivery);
            user.LastWeeklyReportAt = now;

            await db.SaveChangesAsync(ct);
            await jobQueue.EnqueueAsync(JobKind.NotificationDelivery, delivery.Id, ct);
        }
    }
}
