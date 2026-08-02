using Harbora.Application.Abstractions;
using Harbora.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Components;

/// <summary>
/// The environment picker in the topbar.
///
/// Reads the real environments rather than showing a fixed "Production" label: a switcher that
/// always says the same word is a decoration, and this one is how somebody tells which environment
/// the numbers on the page belong to.
/// </summary>
public sealed class EnvironmentSwitcherViewComponent(HarboraDbContext db, ICurrentUser currentUser) : ViewComponent
{
    public sealed record EnvironmentOption(Guid Id, string Name, bool IsDefault);

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var workspaceId = currentUser.WorkspaceId ?? Guid.Empty;

        var environments = await db.Environments
            .Where(e => e.WorkspaceId == workspaceId)
            .OrderByDescending(e => e.IsDefault).ThenBy(e => e.Name)
            .Select(e => new EnvironmentOption(e.Id, e.Name, e.IsDefault))
            .ToListAsync();

        return View(environments);
    }
}
