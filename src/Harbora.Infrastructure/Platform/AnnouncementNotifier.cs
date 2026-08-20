using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Notifications;
using Harbora.Domain.Platform;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Platform;

/// <summary>
/// Fans a Warning-severity <see cref="Announcement"/> out to every workspace's own
/// <c>UserNotification</c> rows, through <see cref="INotificationService.NotifyInAppOnlyAsync"/> — the
/// existing N3 in-app path, reused rather than forked (Sub-project 4, 2026-08-20 platform-options
/// plan).
///
/// <para>
/// <see cref="INotificationService"/> is inherently one workspace at a time — every method on it takes
/// a <c>workspaceId</c> — while an announcement is platform-wide, so the only new code here is the loop
/// over every workspace still open for business; the fan-out itself (preferences, quiet hours, the
/// critical-coverage belt-and-suspenders check) is entirely <c>NotificationService.FanOutToMembersAsync</c>,
/// unchanged.
/// </para>
///
/// <para>
/// Info-severity announcements never reach this class — <c>AnnouncementsController.Create</c> only
/// calls <see cref="NotifyAsync"/> when <see cref="Announcement.Severity"/> is
/// <see cref="AlertSeverity.Warning"/>, the same branch the plan itself draws ("info-level stays
/// banner-only").
/// </para>
/// </summary>
public sealed class AnnouncementNotifier(HarboraDbContext db, INotificationService notifications)
{
    public async Task NotifyAsync(Announcement announcement, CancellationToken ct)
    {
        var evt = NotificationEventData.Create(AlertEvent.PlatformAnnouncement,
            ("Title", announcement.Title), ("Body", announcement.Body),
            ("TitleFa", announcement.TitleFa), ("BodyFa", announcement.BodyFa));

        // Archived/deleted workspaces have nobody actively reading the panel — the same "still open
        // for business" filter WorkspaceSwitcherViewComponent applies when it lists a person's own
        // workspaces.
        var workspaceIds = await db.Workspaces.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.ArchivedAt == null && w.DeletedAt == null)
            .Select(w => w.Id)
            .ToListAsync(ct);

        foreach (var workspaceId in workspaceIds)
            await notifications.NotifyInAppOnlyAsync(workspaceId, evt, announcement.Severity, ct);
    }
}
