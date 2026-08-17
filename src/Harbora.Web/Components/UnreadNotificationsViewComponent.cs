using Harbora.Application.Abstractions;
using Harbora.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Components;

/// <summary>
/// What the bell counts now (N3, 2026-08-16 notification-system spec): unread notifications for the
/// <i>signed-in person</i>, not open incidents for the workspace. Rendered on every page from
/// <c>_Topbar</c>, replacing <c>OpenIncidentsViewComponent</c> there.
///
/// <para>
/// "Conditions open in this workspace" and "things I have not read" are different numbers with
/// different lifecycles — one colleague acknowledging an incident used to clear this badge for
/// everyone, which answered nobody's actual question. The open-incident count did not go away: it
/// moved to where incidents live, the timeline on <c>/monitoring</c> (that page's own
/// <c>data-open-incident-count</c> badge, unchanged by N3), rather than sharing a badge with a fact
/// this one no longer is.
/// </para>
/// </summary>
public sealed class UnreadNotificationsViewComponent(
    HarboraDbContext db,
    ICurrentUser currentUser) : ViewComponent
{
    public sealed record Model(int UnreadCount);

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var workspaceId = currentUser.WorkspaceId ?? Guid.Empty;
        var userId = currentUser.UserId ?? Guid.Empty;

        // UserNotification carries no workspace query filter of its own (unfiltered-but-user-keyed,
        // like ApiToken) — both columns are explicit here for the same reason every other tenant- and
        // user-scoped read in this codebase spells them out rather than leaning on an ambient scope.
        var unread = await db.UserNotifications.AsNoTracking()
            .CountAsync(n => n.UserId == userId && n.WorkspaceId == workspaceId && n.ReadAt == null,
                HttpContext.RequestAborted);

        return View(new Model(unread));
    }
}
