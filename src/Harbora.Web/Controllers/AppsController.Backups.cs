using Harbora.Domain.Authorization;
using Harbora.Modules.Backup.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Sub-project E, Task 2: "Back up now" on the app's own Overview tab.
///
/// <para>
/// Reuses the backup module's existing creation path — <c>BackupSnapshotService.QueueAsync</c>, the
/// same call <c>BackupCenterController.RunBackup</c> makes for <see cref="BackupTargetType.Application"/>
/// — rather than a second way to make a backup. Separate file, same controller: the route lives under
/// /apps/{id}/… like every other action here, following <c>AppsController.Addresses.cs</c> and
/// <c>AppsController.Tabs.cs</c>.
/// </para>
/// </summary>
public sealed partial class AppsController
{
    /// <summary>
    /// Queues an application-target snapshot of this app into the workspace's own backup repository.
    ///
    /// <para>
    /// Guarded by <see cref="Capabilities.BackupsRun"/> — the same policy <c>BackupsController.Run</c>
    /// already requires for an app-config backup — both at the attribute level (does this workspace
    /// member hold the capability at all) and through <c>access.CanTouchAppAsync</c> (does it reach
    /// this particular app's project), the same two-part check every other mutating action in this
    /// controller applies.
    /// </para>
    /// <para>
    /// Which repository it queues into is not a choice this control offers — asking would undo the
    /// point of "instant". The workspace's own enabled repository is used; with none configured, the
    /// card on Overview never renders the button in the first place (see
    /// <see cref="Harbora.Web.ViewModels.AppOverviewViewModel.HasBackupRepository"/>), but a stale
    /// page or a replayed request is still met with an explanation rather than a queued failure.
    /// </para>
    /// </summary>
    [HttpPost("/apps/{id:guid}/backup")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.BackupsRun)]
    public async Task<IActionResult> BackupNow(Guid id, CancellationToken ct)
    {
        // Not theirs, or not there — both answer the same way (see ProjectAccessService.CanTouchAppAsync),
        // so an app in another workspace 404s rather than confirming it exists by refusing differently.
        if (!await access.CanTouchAppAsync(id, Capabilities.BackupsRun, ct)) return NotFound();

        var repository = await db.BackupRepositories.AsNoTracking()
            .Where(r => r.WorkspaceId == WorkspaceId && r.IsEnabled)
            .OrderBy(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (repository is null)
        {
            TempData["Error"] = IsFa
                ? "هنوز مخزن پشتیبانی تنظیم نشده است. ابتدا در مرکز پشتیبان یکی بسازید."
                : "No backup repository is set up yet. Create one in the Backup Center first.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var result = await backupSnapshots.QueueAsync(
            WorkspaceId, repository.Id, BackupTargetType.Application, id.ToString(),
            policyId: null, BackupTrigger.Manual, ct);

        TempData[result.Succeeded ? "Message" : "Error"] = result.Succeeded
            ? (IsFa
                ? "پشتیبان‌گیری در صف قرار گرفت و در پس‌زمینه اجرا می‌شود."
                : "Backup queued. It runs in the background.")
            : result.Error;

        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Queues a Docker-volume snapshot of one of this app's volumes — sub-project D1, the row-level
    /// sibling of <see cref="BackupNow"/> above. Same module, same queueing call, same repository
    /// choice and the same guard; only the target type, the target ref and the tab it lands back on
    /// differ.
    ///
    /// <para>
    /// <paramref name="volumeId"/> is resolved through <em>this</em> app's own <c>Volumes</c>
    /// collection, loaded from the same tenant-filtered <c>App</c> query <c>RemoveVolume</c> already
    /// uses — never through a volume id or name read bare off the route. A volume belongs to an app
    /// and an app belongs to a workspace; skipping that chain and trusting the route directly is the
    /// exact shape of the cross-tenant defect fixed in 6b0f91a.
    /// </para>
    /// </summary>
    [HttpPost("/apps/{id:guid}/volumes/{volumeId:guid}/backup")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.BackupsRun)]
    public async Task<IActionResult> BackupVolumeNow(Guid id, Guid volumeId, CancellationToken ct)
    {
        // Not theirs, or not there — same answer, same reason as BackupNow above.
        if (!await access.CanTouchAppAsync(id, Capabilities.BackupsRun, ct)) return NotFound();

        var app = await db.Apps.Include(a => a.Volumes)
            .FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        var volume = app.Volumes.FirstOrDefault(v => v.Id == volumeId);
        if (volume is null) return NotFound();

        var repository = await db.BackupRepositories.AsNoTracking()
            .Where(r => r.WorkspaceId == WorkspaceId && r.IsEnabled)
            .OrderBy(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (repository is null)
        {
            TempData["Error"] = IsFa
                ? "هنوز مخزن پشتیبانی تنظیم نشده است. ابتدا در مرکز پشتیبان یکی بسازید."
                : "No backup repository is set up yet. Create one in the Backup Center first.";
            return RedirectToAction(nameof(Volumes), new { id });
        }

        // TargetRef is the volume's own Docker name, exactly what BackupTargetResolver.StageVolumeAsync
        // mounts — the module was never told a volume id, only a name it already validates and stages.
        var result = await backupSnapshots.QueueAsync(
            WorkspaceId, repository.Id, BackupTargetType.DockerVolume, volume.Name,
            policyId: null, BackupTrigger.Manual, ct);

        TempData[result.Succeeded ? "Message" : "Error"] = result.Succeeded
            ? (IsFa
                ? "پشتیبان‌گیری در صف قرار گرفت و در پس‌زمینه اجرا می‌شود."
                : "Backup queued. It runs in the background.")
            : result.Error;

        return RedirectToAction(nameof(Volumes), new { id });
    }
}
