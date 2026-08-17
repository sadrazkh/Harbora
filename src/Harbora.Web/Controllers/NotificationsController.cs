using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Notifications;
using Harbora.Infrastructure.Notifications;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// This signed-in person's own inbox (N3, 2026-08-16 notification-system spec): the page the bell
/// links to. No capability policy, unlike most controllers here — every row this reads is already
/// filtered to the caller's own <see cref="UserId"/>, so there is nothing a role could additionally
/// restrict; an Admin and a Viewer read exactly the same query, just with a different <c>UserId</c>
/// bound into it.
///
/// <para>
/// Follows the filter-and-pagination idioms <c>AuditController</c> already established rather than
/// inventing a third list style: a query-string filter, a page size, a total count and a two-button
/// pager.
/// </para>
/// </summary>
[Authorize]
[Route("notifications")]
public sealed class NotificationsController(
    HarboraDbContext db, ICurrentUser currentUser, ISystemClock clock) : Controller
{
    private const int PageSize = 30;

    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private Guid UserId => currentUser.UserId ?? Guid.Empty;

    [HttpGet("")]
    public async Task<IActionResult> Index(
        [FromQuery] bool unreadOnly = false, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "Notifications";
        page = Math.Max(1, page);

        var query = Filter(unreadOnly);
        var total = await query.CountAsync(ct);

        var entries = await query
            .OrderByDescending(n => n.CreatedAt)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .AsNoTracking().ToListAsync(ct);

        return View(new NotificationsPageViewModel
        {
            Entries = entries,
            Page = page,
            PageSize = PageSize,
            TotalCount = total,
            UnreadOnly = unreadOnly
        });
    }

    /// <summary>
    /// Marks one row read — this person's own, and only this person's: the lookup below is by
    /// <see cref="UserId"/> as well as by id, so posting somebody else's notification id (a
    /// neighbouring member's, or another workspace's entirely) finds nothing and changes nothing,
    /// rather than trusting the id alone.
    /// </summary>
    [HttpPost("{id:guid}/read")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        var row = await db.UserNotifications
            .FirstOrDefaultAsync(n => n.Id == id && n.UserId == UserId && n.WorkspaceId == WorkspaceId, ct);

        if (row is { ReadAt: null })
        {
            row.ReadAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Every currently-unread row this person has in this workspace — never another member's, since
    /// each row is that member's own (see <see cref="UserNotification.ReadAt"/>).
    /// </summary>
    [HttpPost("read-all")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
    {
        var unread = await db.UserNotifications
            .Where(n => n.UserId == UserId && n.WorkspaceId == WorkspaceId && n.ReadAt == null)
            .ToListAsync(ct);

        if (unread.Count > 0)
        {
            var now = clock.UtcNow;
            foreach (var row in unread) row.ReadAt = now;
            await db.SaveChangesAsync(ct);
        }

        return RedirectToAction(nameof(Index));
    }

    private IQueryable<UserNotification> Filter(bool unreadOnly)
    {
        var query = db.UserNotifications.Where(n => n.UserId == UserId && n.WorkspaceId == WorkspaceId);
        if (unreadOnly) query = query.Where(n => n.ReadAt == null);
        return query;
    }

    // ---- N5 (2026-08-16 notification-system spec, "noise control") -----------------------------

    /// <summary>Every event a person can actually set a preference for — <c>Test</c> is excluded: it
    /// never runs through <c>NotifyAsync</c>'s preference-aware fan-out at all (the panel's own Test
    /// button dispatches synchronously, see <c>NotificationService.DispatchSafe</c>), so a row for it
    /// here would offer a control that does nothing.</summary>
    private static readonly AlertEvent[] PreferenceEvents =
        Enum.GetValues<AlertEvent>().Where(e => e != AlertEvent.Test).ToArray();

    /// <summary>The matrix, quiet hours, the time zone they hang off, and the weekly opt-in — one
    /// screen, since none of it is workspace-scoped the way the inbox above is.</summary>
    [HttpGet("preferences")]
    public async Task<IActionResult> Preferences(
        [FromQuery] NotificationPreferenceRejection? rejected, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == UserId, ct);
        if (user is null) return NotFound();

        var rows = await db.NotificationPreferences.Where(p => p.UserId == UserId).ToListAsync(ct);

        NotificationPreferenceMode Resolve(AlertEvent evt, NotificationChannel channel) =>
            rows.FirstOrDefault(p => p.EventType == evt && p.Channel == channel)?.Mode
            ?? NotificationPreferenceDefaults.DefaultFor(evt, channel);

        ViewData["Title"] = "Notification preferences";
        return View(new NotificationPreferencesPageViewModel
        {
            Rows = PreferenceEvents.Select(e => new NotificationPreferenceRow(
                e, NotificationEventClass.IsCritical(e),
                Resolve(e, NotificationChannel.InApp), Resolve(e, NotificationChannel.Email))).ToList(),
            TimeZoneId = user.TimeZoneId,
            QuietHoursStartHour = user.QuietHoursStartHour,
            QuietHoursEndHour = user.QuietHoursEndHour,
            WeeklyReportOptIn = user.WeeklyReportOptIn,
            Rejection = rejected is { } r && r != NotificationPreferenceRejection.None ? r : null
        });
    }

    /// <summary>One (event, channel) cell. Goes through <see cref="NotificationPreferenceService"/>
    /// rather than writing the row directly — that is where the critical-coverage invariant lives, so
    /// a refusal here is the same refusal the routing decision itself would honour, not a UI-only
    /// check a direct row write could bypass.</summary>
    [HttpPost("preferences/event")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetEventPreference(
        AlertEvent eventType, NotificationChannel channel, NotificationPreferenceMode mode,
        [FromServices] NotificationPreferenceService preferences, CancellationToken ct)
    {
        var result = await preferences.SetAsync(UserId, eventType, channel, mode, ct);
        return RedirectToAction(nameof(Preferences), result.Ok ? null : new { rejected = result.Rejection });
    }

    /// <summary>Quiet hours, the time zone they are measured in, and the weekly opt-in — one form,
    /// since a person sets all three together or not at all.</summary>
    [HttpPost("preferences/quiet-hours")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetQuietHours(
        string? timeZoneId, int? quietHoursStartHour, int? quietHoursEndHour, bool weeklyReportOptIn,
        CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == UserId, ct);
        if (user is null) return NotFound();

        if (!string.IsNullOrWhiteSpace(timeZoneId)) user.TimeZoneId = timeZoneId;
        user.QuietHoursStartHour = ClampHour(quietHoursStartHour);
        user.QuietHoursEndHour = ClampHour(quietHoursEndHour);
        user.WeeklyReportOptIn = weeklyReportOptIn;
        await db.SaveChangesAsync(ct);

        return RedirectToAction(nameof(Preferences));
    }

    private static int? ClampHour(int? hour) => hour is null ? null : Math.Clamp(hour.Value, 0, 23);
}
