using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Notifications;
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
}
