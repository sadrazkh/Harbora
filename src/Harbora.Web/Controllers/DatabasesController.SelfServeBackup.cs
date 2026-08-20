using Harbora.Application.Abstractions;
using Harbora.Domain.Authorization;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Backups;
using Harbora.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Self-serve database export &amp; import — sub-project 10. Same "/databases/{id}/…" prefix as
/// <c>DatabasesController.Tabs.cs</c> and <c>DatabaseAccessActions.cs</c>, kept as a separate file for
/// the same reason those are: every route below still belongs to this one controller.
///
/// <para>
/// <b>What this reuses rather than rebuilds:</b> the pg_dump/pg_restore machinery in
/// <c>BackupEngine</c>/<c>DatabaseDumpPlan</c> (export is <c>QueueSelfServeExportAsync</c>, which
/// shares <c>QueueBackupAsync</c>'s body and only adds an expiry; import is the existing
/// <c>ImportAsync</c> + <c>RestoreAsync</c>, whose safety-dump-before-restore ordering already existed
/// for every database restore — see <c>DatabaseRestoreSafetySnapshotTests</c>), the
/// <c>VolumeDownloadToken</c> shape for the download link (<see cref="BackupDownloadTokens"/>), and the
/// <c>ServiceRemovalPlan</c> typed-name idiom (<see cref="DatabaseImportPlan"/>).
/// </para>
///
/// <para>
/// <b>Capability split</b> (do-not-change item 19's neighbour, the existing Owner/Admin/Operator
/// matrix): export asks for <see cref="Capabilities.BackupsRun"/>, the same capability "back up now"
/// already uses. Import asks for <see cref="Capabilities.BackupsRestore"/> — the heavier, destructive
/// capability the existing admin restore button already requires, since an import overwrites the
/// database's current contents exactly the way a restore does. An Operator (BackupsRun only, in both
/// <c>RolePermissions</c> and <c>WorkspaceRolePermissions</c>) can therefore export but not import.
/// </para>
/// </summary>
public sealed partial class DatabasesController
{
    /// <summary>
    /// Generous but bounded — a database dump can legitimately be large, but an unbounded upload is
    /// disk nobody asked to spend. Matches the shape of <c>VolumeFileService.MaxFileBytes</c>'s own
    /// refusal without borrowing its (much smaller) limit, which exists for arbitrary volume files.
    /// </summary>
    private const long MaxImportBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Queues a self-serve export of this database's current contents.</summary>
    [HttpPost("{id:guid}/export")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.BackupsRun)]
    public async Task<IActionResult> Export(Guid id, CancellationToken ct)
    {
        if (!await access.CanTouchServiceAsync(id, Capabilities.BackupsRun, ct)) return NotFound();
        var svc = await db.ManagedServices.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (svc is null) return NotFound();

        var destination = await DefaultDestinationAsync(ct);
        if (destination is null)
        {
            TempData["Error"] = IsFa
                ? "هیچ مقصد پشتیبانی تنظیم نشده. ابتدا از صفحهٔ «پشتیبان‌ها» یک مقصد بسازید."
                : "No backup destination is configured yet. Set one up on the Backups page first.";
            return RedirectToAction(nameof(Details), new { id });
        }

        await backupEngine.QueueSelfServeExportAsync(
            WorkspaceId, id.ToString(), destination.Id, DatabaseExportPlan.ArtifactLifetime, ct);

        await audit.LogAsync("database.export_queued", "service", id.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString(), ct: ct);

        TempData["Message"] = IsFa
            ? "خروجی گرفتن از دیتابیس صف شد. وقتی تمام شد، لینک دانلود از همین صفحه قابل ساختن است."
            : "The export was queued. Once it finishes, a download link can be minted right here.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Mints a time-limited link to a completed self-serve export — the same idiom
    /// <c>AppDataController.DownloadLink</c> established for D4, retargeted at a backup artifact. The
    /// int-unix TempData value is deliberate: <c>CookieTempDataProvider</c> re-infers a date-shaped
    /// string as a boxed <c>DateTime</c> across the redirect, which reads null through <c>as string</c>
    /// — the exact trap D4 hit once already.
    /// </summary>
    [HttpPost("{id:guid}/export/{backupId:guid}/link")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.BackupsRun)]
    public async Task<IActionResult> ExportDownloadLink(Guid id, Guid backupId, CancellationToken ct)
    {
        if (!await access.CanTouchServiceAsync(id, Capabilities.BackupsRun, ct)) return NotFound();

        var backup = await db.Backups.FirstOrDefaultAsync(b =>
            b.Id == backupId && b.WorkspaceId == WorkspaceId && b.TargetRef == id.ToString()
            && b.Type == BackupType.Database, ct);
        if (backup is null || backup.Status != BackupStatus.Completed || backup.ArtifactPath is null)
            return NotFound();

        var mint = await downloadTokens.MintAsync(backup, ct);
        var link = $"{Request.Scheme}://{Request.Host}/backups/download/{mint.Token}";

        await audit.LogAsync("database.export_link_minted", "service", id.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString(), ct: ct);

        TempData["ExportDownloadLink"] = link;
        TempData["ExportDownloadLinkExpiresAtUnix"] = checked((int)mint.ExpiresAt.ToUnixTimeSeconds());
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Uploads a dump and restores it over this database's current contents.
    ///
    /// <para>
    /// The typed-name confirmation and the file both arrive on one request rather than the two-step
    /// "confirm page" <c>ConfirmRemove</c> uses for delete: a file input cannot be repopulated after a
    /// redirect, so the do-not-change item 19 idiom is applied here as one form that states the
    /// database's name and that a safety snapshot will be taken, asks the operator to type the name,
    /// and refuses — reading nothing from the upload — the moment that does not match.
    /// </para>
    /// <para>
    /// Import is destructive, so the safety snapshot always comes first: <c>BackupEngine.RestoreAsync</c>
    /// takes it before this database's contents are touched (proved in
    /// <c>DatabaseRestoreSafetySnapshotTests</c>), and a restore that then fails names the safety
    /// backup's id rather than leaving "Import failed" with no way back.
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/import")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxImportBytes + 1024 * 1024)]
    [Authorize(Policy = Capabilities.BackupsRestore)]
    public async Task<IActionResult> Import(Guid id, IFormFile? file, string? confirmName, CancellationToken ct)
    {
        if (!await access.CanTouchServiceAsync(id, Capabilities.BackupsRestore, ct)) return NotFound();
        var svc = await db.ManagedServices.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (svc is null) return NotFound();

        if (!DatabaseImportPlan.IsConfirmed(confirmName, svc.Name))
        {
            TempData["Error"] = IsFa
                ? $"وارد کردن تأیید نشد؛ نام دیتابیس را دقیقاً بنویسید: {svc.Name}"
                : $"Import not confirmed. Type the database's name exactly: {svc.Name}";
            return RedirectToAction(nameof(Details), new { id });
        }

        if (file is null || file.Length == 0)
        {
            TempData["Error"] = IsFa ? "فایلی انتخاب نشده بود." : "No file was chosen.";
            return RedirectToAction(nameof(Details), new { id });
        }
        if (file.Length > MaxImportBytes)
        {
            TempData["Error"] = IsFa
                ? $"فایل بزرگ‌تر از {MaxImportBytes / 1024 / 1024 / 1024} گیگابایت است."
                : $"The file is larger than {MaxImportBytes / 1024 / 1024 / 1024} GB.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var destination = await DefaultDestinationAsync(ct);
        if (destination is null)
        {
            TempData["Error"] = IsFa
                ? "هیچ مقصد پشتیبانی تنظیم نشده. ابتدا از صفحهٔ «پشتیبان‌ها» یک مقصد بسازید."
                : "No backup destination is configured yet. Set one up on the Backups page first.";
            return RedirectToAction(nameof(Details), new { id });
        }

        Guid importedBackupId;
        await using (var stream = file.OpenReadStream())
        {
            importedBackupId = await backupEngine.ImportAsync(
                WorkspaceId, BackupType.Database, id.ToString(), destination.Id, file.FileName, stream, ct);
        }

        try
        {
            await backupEngine.RestoreAsync(importedBackupId, ct);
        }
        catch (InvalidOperationException ex)
        {
            // The message already names the safety backup a customer can restore from — see
            // BackupEngine.RestoreDatabaseAsync — so "Import failed" is never the whole sentence.
            await audit.LogAsync("database.import_failed", "service", id.ToString(),
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                metadataJson: System.Text.Json.JsonSerializer.Serialize(new { reason = ex.Message }), ct: ct);
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }

        var safety = await LatestSafetySnapshotAsync(id.ToString(), ct);
        await audit.LogAsync("database.import_completed", "service", id.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString(), ct: ct);

        TempData["Message"] = safety is null
            ? (IsFa ? "وارد کردن با موفقیت انجام شد." : "The import completed.")
            : (IsFa
                ? $"وارد کردن با موفقیت انجام شد. نسخهٔ ایمنی از داده‌های قبلی به‌عنوان پشتیبان {safety.Id} ذخیره شد."
                : $"The import completed. What the database held before is saved as backup {safety.Id}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>The workspace's default local destination, or its first, for a self-serve act that
    /// asks nobody to pick one — the same fallback <c>BackupsController.EnsureDefaultDestinationAsync</c>
    /// guarantees exists for every workspace that has ever opened the Backups page.</summary>
    private Task<BackupDestination?> DefaultDestinationAsync(CancellationToken ct) =>
        db.BackupDestinations.Where(d => d.WorkspaceId == WorkspaceId)
            .OrderByDescending(d => d.IsDefault).FirstOrDefaultAsync(ct)!;

    /// <summary>
    /// The most recent automatic safety snapshot recorded for this target — read back purely to name
    /// it in the success message; <c>RestoreAsync</c> itself is void on success, by design, the same
    /// as every other caller of it.
    /// </summary>
    private Task<Backup?> LatestSafetySnapshotAsync(string targetRef, CancellationToken ct) =>
        db.Backups.Where(b => b.WorkspaceId == WorkspaceId && b.TargetRef == targetRef
                               && b.VerificationNote != null && b.VerificationNote.Contains("safety snapshot"))
            .OrderByDescending(b => b.CreatedAt).FirstOrDefaultAsync(ct)!;
}
