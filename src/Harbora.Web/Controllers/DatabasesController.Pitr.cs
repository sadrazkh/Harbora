using Harbora.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Point-in-time recovery for PostgreSQL (3.1, round-2 market-gaps plan) — the archiving toggle and
/// the restore-to-timestamp form, on this same <c>/databases/{id}/…</c> route family. Separate file,
/// same controller, same reasoning <c>DatabasesController.Tabs.cs</c> already gives for its own split.
/// </summary>
public sealed partial class DatabasesController
{
    /// <summary>
    /// Turns WAL archiving on or off for this PostgreSQL instance. Only ever stores the request and
    /// marks <c>ManagedService.HasUnpublishedChanges</c> — <c>ManagedServiceEngine.ProvisionAsync</c>
    /// is what actually bakes <c>archive_command</c> into the container's command line on a rebuild,
    /// the same "applies on next deploy" idiom <see cref="PgVector"/> already uses one setting up.
    /// </summary>
    [HttpPost("{id:guid}/pitr")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Pitr(Guid id, bool enable, CancellationToken ct)
    {
        await Guard(id, ct);
        var exists = await db.ManagedServices.AsNoTracking().AnyAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (!exists) return NotFound();

        var error = await walArchiving.SetEnabledAsync(id, enable, ct);
        if (error is not null)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Details), new { id });
        }

        TempData["Message"] = enable
            ? (IsFa
                ? "ذخیره شد. با بازسازی کانتینر، آرشیو WAL شروع می‌شود."
                : "Saved. Rebuild the container to start WAL archiving.")
            : (IsFa
                ? "ذخیره شد. با بازسازی بعدی، آرشیو WAL متوقف می‌شود. آرشیوهای قبلی همچنان قابل بازیابی‌اند."
                : "Saved. The next rebuild stops WAL archiving. Existing archives stay exactly as restorable.");

        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Restores this instance to a moment in time. Lands in a brand-new logical database by default —
    /// nothing existing is touched — unless <paramref name="overwriteDatabaseId"/> names an existing
    /// one, in which case <paramref name="confirmName"/> must match that database's own name exactly
    /// (<c>PitrRestoreService</c> names which apps are attached in its own refusal when it does not).
    ///
    /// <paramref name="targetUnixSeconds"/> rather than a date string: TempData re-types a GUID- or
    /// date-shaped string across the redirect this action ends with, so the moment carries as a plain
    /// integer the same way every other timestamp round-tripped through this panel's forms already
    /// does.
    /// </summary>
    [HttpPost("{id:guid}/pitr/restore")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> PitrRestore(
        Guid id, long targetUnixSeconds, Guid? overwriteDatabaseId, string? confirmName, CancellationToken ct)
    {
        await Guard(id, ct);
        var exists = await db.ManagedServices.AsNoTracking().AnyAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (!exists) return NotFound();

        var target = DateTimeOffset.FromUnixTimeSeconds(targetUnixSeconds);

        var (ok, error, databaseId) = await pitrRestore.RestoreToTimestampAsync(
            id, target, overwriteDatabaseId, confirmName, ct);

        if (!ok)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Details), new { id });
        }

        await audit.LogAsync("database.pitr_restored", "service", $"{id}:{databaseId}",
            HttpContext.Connection.RemoteIpAddress?.ToString(), workspaceId: WorkspaceId, ct: ct);

        TempData["Message"] = IsFa
            ? "بازیابی انجام شد."
            : "Restore completed.";
        return RedirectToAction(nameof(Details), new { id });
    }
}
