using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Attaching/detaching a workspace's shared <see cref="ConfigGroup"/>s to this app (Sub-project 9,
/// 2026-08-20 platform-options plan). The groups themselves are managed at
/// <c>ConfigGroupsController</c>; this is only the "which groups does THIS app receive, and in what
/// order" half, the same split <c>DatabasesController</c> draws between a service and the apps wired
/// to it. Separate file, same controller and same <c>/apps/{id}/…</c> route family as
/// <c>AppsController.Addresses.cs</c>/<c>.Backups.cs</c>/<c>.Tabs.cs</c>.
/// </summary>
public sealed partial class AppsController
{
    /// <summary>
    /// Attaches a group at the back of this app's precedence order (current max <c>AttachOrder</c> +
    /// 1 — never reused, so a detach-then-reattach always moves a group to the back, exactly like a
    /// person re-adding it would expect). Starts <c>HasUnpublishedChanges</c> true: the app has never
    /// deployed with this group's entries, so nothing here is live until its own next deploy.
    /// </summary>
    [HttpPost("/apps/{id:guid}/config-groups")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> AttachConfigGroup(Guid id, Guid configGroupId, CancellationToken ct)
    {
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsEnv, ct)) return NotFound();

        var group = await db.ConfigGroups.FirstOrDefaultAsync(g => g.Id == configGroupId && g.WorkspaceId == WorkspaceId, ct);
        if (group is null) return NotFound();

        if (await db.AppConfigGroups.AnyAsync(cg => cg.AppId == id && cg.ConfigGroupId == configGroupId, ct))
        {
            TempData["Error"] = IsFa ? "این گروه از قبل به این اپ متصل است." : "This group is already attached.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var maxOrder = await db.AppConfigGroups
            .Where(cg => cg.AppId == id)
            .Select(cg => (int?)cg.AttachOrder)
            .MaxAsync(ct) ?? 0;

        db.AppConfigGroups.Add(new AppConfigGroup
        {
            AppId = id, ConfigGroupId = configGroupId, AttachOrder = maxOrder + 1, HasUnpublishedChanges = true
        });
        await db.SaveChangesAsync(ct);

        TempData["Message"] = IsFa
            ? $"گروه «{group.Name}» متصل شد. متغیرهایش با استقرار بعدی این اپ اعمال می‌شوند."
            : $"Attached '{group.Name}'. Its variables apply on this app's next deploy.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("/apps/{id:guid}/config-groups/{configGroupId:guid}/detach")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> DetachConfigGroup(Guid id, Guid configGroupId, CancellationToken ct)
    {
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsEnv, ct)) return NotFound();

        var join = await db.AppConfigGroups.FirstOrDefaultAsync(cg => cg.AppId == id && cg.ConfigGroupId == configGroupId, ct);
        if (join is null) return NotFound();

        db.AppConfigGroups.Remove(join);
        await db.SaveChangesAsync(ct);

        TempData["Message"] = IsFa
            ? "گروه جدا شد. تا استقرار بعدی، کانتینر در حال اجرا هنوز متغیرهای آن را دارد."
            : "Detached. Until the next deploy, the running container still has its variables.";
        return RedirectToAction(nameof(Details), new { id });
    }
}
