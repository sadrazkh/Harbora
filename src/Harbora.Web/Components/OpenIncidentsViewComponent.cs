using Harbora.Application.Abstractions;
using Harbora.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Components;

/// <summary>
/// What the bell badge counts: incidents open right now for the caller's workspace. Rendered on
/// every page from <c>_Topbar</c>, the same way <c>AccountBalance</c> is — the bell used to be a bare
/// link with no count at all (2026-08-16 monitoring-alerting spec §M4).
/// </summary>
public sealed class OpenIncidentsViewComponent(
    HarboraDbContext db,
    ICurrentUser currentUser) : ViewComponent
{
    public sealed record Model(int OpenCount);

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var workspaceId = currentUser.WorkspaceId ?? Guid.Empty;
        var openCount = await db.AlertIncidents.AsNoTracking()
            .CountAsync(i => i.WorkspaceId == workspaceId && i.ClosedAt == null, HttpContext.RequestAborted);

        return View(new Model(openCount));
    }
}
