using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Components;

public sealed class WorkspaceSwitcherViewComponent(HarboraDbContext db, ICurrentUser currentUser) : ViewComponent
{
    public sealed record Option(Guid Id, string Name, bool IsPersonal, WorkspaceRole Role, bool IsCurrent);

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var userId = currentUser.UserId ?? Guid.Empty;
        var current = currentUser.WorkspaceId ?? Guid.Empty;
        var options = await db.WorkspaceMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.UserId == userId
                && m.Workspace!.ArchivedAt == null && m.Workspace.DeletedAt == null)
            .OrderByDescending(m => m.Workspace!.IsPersonal).ThenBy(m => m.Workspace!.Name)
            .Select(m => new Option(m.WorkspaceId, m.Workspace!.Name, m.Workspace.IsPersonal, m.Role, m.WorkspaceId == current))
            .ToListAsync();
        return View(options);
    }
}
