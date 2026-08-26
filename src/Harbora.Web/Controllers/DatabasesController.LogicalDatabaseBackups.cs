using Harbora.Domain.Authorization;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Backup and restore for ONE logical database inside an instance (D2, 2026-08-25 shared-databases
/// plan). Same "/databases/{id}/…" prefix as <c>DatabasesController.Tabs.cs</c>,
/// <c>DatabaseAccessActions.cs</c> and <c>DatabasesController.SelfServeBackup.cs</c> — kept as a
/// separate file for the same reason those are.
///
/// <para>
/// <b>What this reuses rather than rebuilds:</b> the exact queue/import/restore machinery sub-project
/// 10 already put in <c>DatabasesController.SelfServeBackup</c> — <c>QueueBackupAsync</c>,
/// <c>ImportAsync</c> and <c>RestoreAsync</c>/<c>RestoreIntoAsync</c> now all accept which logical
/// database a run is of, so no second backup path exists here, only routes that name one.
/// </para>
///
/// <para>
/// <b>The gap this closes that sub-project 10 could not:</b> that Import always confirmed by typing
/// the INSTANCE's name and never said who was attached — fine when an instance held exactly one
/// database, wrong once it can hold several, and silent about the one thing the plan calls out
/// explicitly: "the person restoring may not know" who is attached. <see cref="ConfirmImport"/> and
/// <see cref="ConfirmRestore"/> below are the two-step, named-apps confirm <c>ConfirmRemoveDatabase</c>
/// already established for delete, applied here to overwrite.
/// </para>
/// </summary>
public sealed partial class DatabasesController
{
    // ---- on-demand backup of one logical database -------------------------------------------------

    /// <summary>
    /// Queues an ordinary, retained backup of one logical database — the "back up now" a schedule's
    /// own tick (<c>BackupScheduler</c>) also queues, reusing the exact same
    /// <see cref="Harbora.Application.Abstractions.IBackupEngine.QueueBackupAsync"/> every other
    /// backup on this platform runs through. Not the self-serve, expiring export
    /// <see cref="Export"/> already offers — this is a normal backup, subject to the workspace's
    /// ordinary retention count, downloadable and restorable from the Backups page like any other.
    /// </summary>
    [HttpPost("{id:guid}/logical-databases/{databaseId:guid}/backup")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.BackupsRun)]
    public async Task<IActionResult> BackupDatabase(Guid id, Guid databaseId, CancellationToken ct)
    {
        if (!await access.CanTouchServiceAsync(id, Capabilities.BackupsRun, ct)) return NotFound();

        var logical = await db.ManagedServiceDatabases.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == databaseId && d.ManagedServiceId == id, ct);
        if (logical is null) return NotFound();

        var destination = await DefaultDestinationAsync(ct);
        if (destination is null)
        {
            TempData["Error"] = IsFa
                ? "هیچ مقصد پشتیبانی تنظیم نشده. ابتدا از صفحهٔ «پشتیبان‌ها» یک مقصد بسازید."
                : "No backup destination is configured yet. Set one up on the Backups page first.";
            return RedirectToAction(nameof(Details), new { id });
        }

        await backupEngine.QueueBackupAsync(
            WorkspaceId, BackupType.Database, id.ToString(), destination.Id, scheduled: false, ct, databaseId);

        await audit.LogAsync("database.logical_database_backup_queued", "service", $"{id}:{databaseId}",
            HttpContext.Connection.RemoteIpAddress?.ToString(), ct: ct);

        TempData["Message"] = IsFa
            ? $"پشتیبان‌گیری از «{logical.Name}» صف شد."
            : $"Backing up \"{logical.Name}\" was queued.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // ---- import a dump into one logical database, naming who is attached first --------------------

    /// <summary>
    /// What uploading a dump onto this specific logical database will do — which apps are attached
    /// to it, and that a safety snapshot is taken automatically first. The requirement this exists to
    /// satisfy: "the confirmation must name which apps are attached, because the person restoring may
    /// not know" — <c>DatabasesController.Import</c> (the whole-instance import) never showed this.
    /// </summary>
    [HttpGet("{id:guid}/logical-databases/{databaseId:guid}/import")]
    [Authorize(Policy = Capabilities.BackupsRestore)]
    public async Task<IActionResult> ConfirmImport(Guid id, Guid databaseId, CancellationToken ct)
    {
        if (!await access.CanTouchServiceAsync(id, Capabilities.BackupsRestore, ct)) return NotFound();

        var service = await db.ManagedServices.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (service is null) return NotFound();

        var logical = await db.ManagedServiceDatabases.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == databaseId && d.ManagedServiceId == id, ct);
        if (logical is null) return NotFound();

        ViewBag.Service = service;
        ViewBag.Database = logical;
        ViewBag.AttachedApps = await AttachedAppNamesAsync(databaseId, ct);
        ViewData["Title"] = IsFa ? $"وارد کردن به «{logical.Name}»" : $"Import into {logical.Name}";
        return View("ConfirmImportLogicalDatabase");
    }

    /// <summary>
    /// Uploads a dump and restores it over this ONE logical database's current contents. The typed
    /// name and the file arrive on the same request, the same reason
    /// <see cref="DatabasesController.Import"/> already does it that way: a file input cannot be
    /// repopulated after a redirect.
    /// </summary>
    [HttpPost("{id:guid}/logical-databases/{databaseId:guid}/import")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxImportBytes + 1024 * 1024)]
    [Authorize(Policy = Capabilities.BackupsRestore)]
    public async Task<IActionResult> ImportDatabase(
        Guid id, Guid databaseId, IFormFile? file, string? confirmName, CancellationToken ct)
    {
        if (!await access.CanTouchServiceAsync(id, Capabilities.BackupsRestore, ct)) return NotFound();

        var logical = await db.ManagedServiceDatabases.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == databaseId && d.ManagedServiceId == id, ct);
        if (logical is null) return NotFound();

        if (!DatabaseImportPlan.IsConfirmed(confirmName, logical.Name))
        {
            TempData["Error"] = IsFa
                ? $"وارد کردن تأیید نشد؛ نام را دقیقاً بنویسید: {logical.Name}"
                : $"Import not confirmed. Type the name exactly: {logical.Name}";
            return RedirectToAction(nameof(ConfirmImport), new { id, databaseId });
        }

        if (file is null || file.Length == 0)
        {
            TempData["Error"] = IsFa ? "فایلی انتخاب نشده بود." : "No file was chosen.";
            return RedirectToAction(nameof(ConfirmImport), new { id, databaseId });
        }
        if (file.Length > MaxImportBytes)
        {
            TempData["Error"] = IsFa
                ? $"فایل بزرگ‌تر از {MaxImportBytes / 1024 / 1024 / 1024} گیگابایت است."
                : $"The file is larger than {MaxImportBytes / 1024 / 1024 / 1024} GB.";
            return RedirectToAction(nameof(ConfirmImport), new { id, databaseId });
        }

        var destination = await DefaultDestinationAsync(ct);
        if (destination is null)
        {
            TempData["Error"] = IsFa
                ? "هیچ مقصد پشتیبانی تنظیم نشده. ابتدا از صفحهٔ «پشتیبان‌ها» یک مقصد بسازید."
                : "No backup destination is configured yet. Set one up on the Backups page first.";
            return RedirectToAction(nameof(ConfirmImport), new { id, databaseId });
        }

        Guid importedBackupId;
        await using (var stream = file.OpenReadStream())
        {
            importedBackupId = await backupEngine.ImportAsync(
                WorkspaceId, BackupType.Database, id.ToString(), destination.Id, file.FileName, stream, ct,
                databaseId);
        }

        try
        {
            // No target named: RestoreAsync resolves the imported Backup's own ManagedServiceDatabaseId,
            // which ImportAsync just stamped as databaseId — restoring into exactly the database this
            // was uploaded onto, the same "same place it came from" default every other restore uses.
            await backupEngine.RestoreAsync(importedBackupId, ct);
        }
        catch (InvalidOperationException ex)
        {
            // The engine's own words — never a generic failure, and never a success for a restore that
            // did not happen. The safety snapshot (if the dump got that far) is named inside ex.Message.
            await audit.LogAsync("database.logical_database_import_failed", "service", $"{id}:{databaseId}",
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                metadataJson: System.Text.Json.JsonSerializer.Serialize(new { reason = ex.Message }), ct: ct);
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(ConfirmImport), new { id, databaseId });
        }

        var safety = await LatestSafetySnapshotAsync(id.ToString(), ct);
        await audit.LogAsync("database.logical_database_import_completed", "service", $"{id}:{databaseId}",
            HttpContext.Connection.RemoteIpAddress?.ToString(), ct: ct);

        TempData["Message"] = safety is null
            ? (IsFa ? $"وارد کردن به «{logical.Name}» با موفقیت انجام شد." : $"The import into \"{logical.Name}\" completed.")
            : (IsFa
                ? $"وارد کردن به «{logical.Name}» با موفقیت انجام شد. نسخهٔ ایمنی از داده‌های قبلی به‌عنوان پشتیبان {safety.Id} ذخیره شد."
                : $"The import into \"{logical.Name}\" completed. What it held before is saved as backup {safety.Id}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    // ---- restore an existing backup into the same, a different, or a brand-new database -----------

    /// <summary>
    /// Where a previously-taken backup can be restored: back into <paramref name="databaseId"/>
    /// itself (the default), into a different logical database anywhere in this workspace — another
    /// database on this instance, or one on a different instance entirely — or into a brand-new one,
    /// how a customer clones production into staging. The "different instance" case is also the one
    /// that can name an incompatible engine, which is why the picker lists <em>every</em> other
    /// logical database in the workspace rather than only this instance's own: refusing "restore a
    /// PostgreSQL dump into MySQL" by name only means something if the panel can actually be asked to.
    /// Names which apps are attached to <paramref name="databaseId"/> itself, up front, for the
    /// default "same" choice.
    /// </summary>
    [HttpGet("{id:guid}/logical-databases/{databaseId:guid}/restore/{backupId:guid}")]
    [Authorize(Policy = Capabilities.BackupsRestore)]
    public async Task<IActionResult> ConfirmRestore(Guid id, Guid databaseId, Guid backupId, CancellationToken ct)
    {
        if (!await access.CanTouchServiceAsync(id, Capabilities.BackupsRestore, ct)) return NotFound();

        var service = await db.ManagedServices.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (service is null) return NotFound();

        var logical = await db.ManagedServiceDatabases.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == databaseId && d.ManagedServiceId == id, ct);
        if (logical is null) return NotFound();

        var backup = await db.Backups.AsNoTracking().FirstOrDefaultAsync(b =>
            b.Id == backupId && b.WorkspaceId == WorkspaceId && b.Type == BackupType.Database
            && b.Status == BackupStatus.Completed, ct);
        if (backup is null) return NotFound();

        var otherDatabases = await db.ManagedServiceDatabases.AsNoTracking()
            .Include(d => d.ManagedService)
            .Where(d => d.WorkspaceId == WorkspaceId && d.Id != databaseId)
            .OrderBy(d => d.ManagedService!.Name).ThenBy(d => d.Name)
            .ToListAsync(ct);

        ViewBag.Service = service;
        ViewBag.Database = logical;
        ViewBag.Backup = backup;
        ViewBag.AttachedApps = await AttachedAppNamesAsync(databaseId, ct);
        ViewBag.OtherDatabases = otherDatabases;
        ViewData["Title"] = IsFa ? $"بازیابی به «{logical.Name}»" : $"Restore into {logical.Name}";
        return View("ConfirmRestoreLogicalDatabase");
    }

    /// <summary>
    /// Restores <paramref name="backupId"/> into whichever target <paramref name="mode"/> names.
    /// "new" creates the logical database first (<see cref="LogicalDatabaseService.CreateAsync"/> —
    /// the exact engine-backed creation D1 shipped, refused by name on the same terms
    /// <c>CreateDatabase</c> already is) and is never destructive, so it skips the typed-name gate;
    /// "same" and "existing" both overwrite a logical database that may already have apps attached, so
    /// both require typing that database's own name, and both re-read its attached apps at this exact
    /// moment rather than trusting whatever <see cref="ConfirmRestore"/> showed a moment earlier.
    /// </summary>
    [HttpPost("{id:guid}/logical-databases/{databaseId:guid}/restore/{backupId:guid}")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.BackupsRestore)]
    public async Task<IActionResult> RestoreDatabase(
        Guid id, Guid databaseId, Guid backupId, string? mode, Guid? targetDatabaseId, string? newDatabaseName,
        string? confirmName, CancellationToken ct)
    {
        if (!await access.CanTouchServiceAsync(id, Capabilities.BackupsRestore, ct)) return NotFound();

        var service = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (service is null) return NotFound();

        var backupExists = await db.Backups.AsNoTracking().AnyAsync(b =>
            b.Id == backupId && b.WorkspaceId == WorkspaceId && b.Type == BackupType.Database
            && b.Status == BackupStatus.Completed, ct);
        if (!backupExists) return NotFound();

        if (string.Equals(mode, "new", StringComparison.OrdinalIgnoreCase))
        {
            var (created, error) = await logicalDatabases.CreateAsync(id, newDatabaseName, ct);
            if (error is not null)
            {
                TempData["Error"] = error;
                return RedirectToAction(nameof(ConfirmRestore), new { id, databaseId, backupId });
            }

            try
            {
                await backupEngine.RestoreIntoAsync(backupId, id, created!.Id, ct);
            }
            catch (InvalidOperationException ex)
            {
                TempData["Error"] = ex.Message;
                return RedirectToAction(nameof(ConfirmRestore), new { id, databaseId, backupId });
            }

            await audit.LogAsync("database.logical_database_restore_completed", "service", $"{id}:{created.Id}",
                HttpContext.Connection.RemoteIpAddress?.ToString(), ct: ct);
            TempData["Message"] = IsFa
                ? $"بازیابی در پایگاه‌دادهٔ تازهٔ «{created.Name}» انجام شد."
                : $"Restored into the new database \"{created.Name}\".";
            return RedirectToAction(nameof(Details), new { id });
        }

        // "existing" may name a database on a DIFFERENT instance — see ConfirmRestore's own doc for
        // why the picker is workspace-wide rather than limited to this instance's own siblings.
        var resolvedTargetId = string.Equals(mode, "existing", StringComparison.OrdinalIgnoreCase) && targetDatabaseId is { } chosen
            ? chosen
            : databaseId;

        var target = await db.ManagedServiceDatabases.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == resolvedTargetId && d.WorkspaceId == WorkspaceId, ct);
        if (target is null) return NotFound();

        // A different instance is a different project's worth of access, potentially — the workspace
        // query filter already keeps this to one tenant, but not to what THIS member may touch. Never
        // trust that picking id (whose access was already checked above) implies rights over whatever
        // instance target.ManagedServiceId happens to name.
        if (target.ManagedServiceId != id
            && !await access.CanTouchServiceAsync(target.ManagedServiceId, Capabilities.BackupsRestore, ct))
            return NotFound();

        if (!DatabaseImportPlan.IsConfirmed(confirmName, target.Name))
        {
            TempData["Error"] = IsFa
                ? $"بازیابی تأیید نشد؛ نام را دقیقاً بنویسید: {target.Name}"
                : $"Restore not confirmed. Type the name exactly: {target.Name}";
            return RedirectToAction(nameof(ConfirmRestore), new { id, databaseId, backupId });
        }

        try
        {
            await backupEngine.RestoreIntoAsync(backupId, target.ManagedServiceId, target.Id, ct);
        }
        catch (InvalidOperationException ex)
        {
            // Never a generic failure: an incompatible engine, a workspace mismatch, a failed safety
            // dump, or a failed restore (naming its own safety backup) — the engine's own words.
            await audit.LogAsync("database.logical_database_restore_failed", "service", $"{target.ManagedServiceId}:{target.Id}",
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                metadataJson: System.Text.Json.JsonSerializer.Serialize(new { reason = ex.Message }), ct: ct);
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(ConfirmRestore), new { id, databaseId, backupId });
        }

        await audit.LogAsync("database.logical_database_restore_completed", "service", $"{target.ManagedServiceId}:{target.Id}",
            HttpContext.Connection.RemoteIpAddress?.ToString(), ct: ct);

        var safety = await LatestSafetySnapshotAsync(target.ManagedServiceId.ToString(), ct);
        TempData["Message"] = safety is null
            ? (IsFa ? $"بازیابی در «{target.Name}» انجام شد." : $"Restored into \"{target.Name}\".")
            : (IsFa
                ? $"بازیابی در «{target.Name}» انجام شد. داده‌های قبلی آن به‌عنوان پشتیبان {safety.Id} ذخیره شد."
                : $"Restored into \"{target.Name}\". What it held before is saved as backup {safety.Id}.");
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>"api, worker" for whichever logical database is asked about — the same query
    /// <c>ConfirmRemoveDatabase</c> already runs, reused rather than forked so the two confirm pages
    /// can never drift on who counts as attached.</summary>
    private Task<List<string>> AttachedAppNamesAsync(Guid databaseId, CancellationToken ct) =>
        db.AppManagedServices.AsNoTracking()
            .Where(a => a.ManagedServiceDatabaseId == databaseId)
            .Select(a => a.App!.Name)
            .ToListAsync(ct);
}
