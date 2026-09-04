using Harbora.Domain.Authorization;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// The half of backup management the page did not have: removing a backup, bringing one in from
/// outside, and correcting a destination or a schedule instead of deleting it and starting again.
///
/// <para>
/// A partial of <see cref="BackupsController"/> rather than a controller of its own, the same choice
/// <c>BackupDeliveryActions</c> made: every route here is still under <c>/backups</c>, and a second
/// controller class sends the next reader hunting for which one owns a given path.
/// </para>
/// </summary>
public sealed partial class BackupsController
{
    /// <summary>
    /// Deletes one backup and the artifact behind it.
    ///
    /// <para>
    /// Confirmed by typing, like a restore, and for a stronger reason: a restore overwrites data that
    /// can be taken again, and this removes the copy somebody would take it from. The word is the
    /// same mechanism the restore form uses, so nothing new has to be learned to use it.
    /// </para>
    ///
    /// <para>
    /// The engine deletes the artifact before the row, and a destination that refuses leaves the row
    /// alone — so a failure here is a state that can be retried rather than an artifact nobody can
    /// find any more. That is why the refusal is reported rather than swallowed.
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.BackupsManage)]
    public async Task<IActionResult> Delete(Guid id, string confirm, CancellationToken ct)
    {
        // The same authority a restore asks for. Judged by what the backup is a backup OF: deleting
        // production's last export is production's business whatever list it appears in.
        if (!await MayRestoreAsync(id, ct)) return NotFound();

        if (confirm != "DELETE")
        {
            TempData["Error"] = IsFa
                ? "حذف تأیید نشد؛ هیچ چیزی پاک نشد."
                : "The deletion was not confirmed. Nothing was removed.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            await engine.DeleteAsync(id, ct);
            TempData["Message"] = IsFa
                ? "این نسخهٔ پشتیبان و فایلش پاک شد."
                : "The backup and its stored file were removed.";
        }
        catch (Exception ex)
        {
            // Named rather than reported as a generic failure: the row is still there, which is the
            // useful half of the news, and the reason is what tells somebody whether to retry.
            TempData["Error"] = IsFa
                ? $"فایل پشتیبان پاک نشد، پس ردیف آن هم دست‌نخورده ماند: {ex.Message}"
                : $"The stored file could not be removed, so its record was left alone: {ex.Message}";
        }

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Brings an archive in from outside so it can be restored here.
    ///
    /// <para>
    /// This exists because a backup that has been downloaded and kept somewhere safe was, until now,
    /// unusable: the restore path only reads artifacts this install's own runs produced, so a rebuilt
    /// panel could not restore its own backups and one Harbora could not take over from another.
    /// </para>
    ///
    /// <para>
    /// <b>The upload is not validated as a Harbora artifact, and does not pretend to be.</b> It may
    /// be encrypted, in which case nothing can look inside it without the key. The row is recorded as
    /// never verified and the message points at the dry run, which is the one thing that answers the
    /// question honestly.
    /// </para>
    /// </summary>
    [HttpPost("upload")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.BackupsManage)]
    [RequestSizeLimit(ImportSizeLimitBytes)]
    public async Task<IActionResult> Upload(
        string target, Guid destinationId, IFormFile? file, CancellationToken ct)
    {
        if (!TryParseTarget(target, out var type, out var reference))
        {
            TempData["Error"] = IsFa ? "هدف پشتیبان معتبر نیست." : "Invalid backup target.";
            return RedirectToAction(nameof(Index));
        }

        // The same ownership check a run makes, and it has to be at least as strict: an import creates
        // something the restore action will later read, so a target somebody may not back up is a
        // target they may not smuggle an archive onto either.
        if (!await OwnsTargetAsync(type, reference, Capabilities.BackupsManage, ct)
            || !await db.BackupDestinations.AnyAsync(d => d.Id == destinationId && d.WorkspaceId == WorkspaceId, ct))
        {
            TempData["Error"] = IsFa
                ? "این هدف یا این مقصد به این فضای کاری تعلق ندارد."
                : "The backup target or destination does not belong to this workspace.";
            return RedirectToAction(nameof(Index));
        }

        if (file is null || file.Length == 0)
        {
            // An empty file is its own case. Stored, it would be a backup that looks complete, has a
            // valid checksum, and restores nothing — which is the worst thing on this page.
            TempData["Error"] = IsFa
                ? "فایلی انتخاب نشده بود، یا فایل خالی بود."
                : "No file was chosen, or the file was empty.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            await using var content = file.OpenReadStream();
            await engine.ImportAsync(
                WorkspaceId, type, reference, destinationId, file.FileName, content, ct);

            // Says what was NOT done. Accepting an upload silently would read as Harbora vouching for
            // the archive, and it cannot: it has not looked inside, and may not be able to.
            TempData["Message"] = IsFa
                ? "فایل ذخیره شد. Harbora محتوای آن را بررسی نکرده است — پیش از اتکا به آن، «بررسی بازیابی» را اجرا کنید."
                : "The file was stored. Harbora has not inspected what is in it — run the restore check before relying on it.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = IsFa
                ? $"فایل ذخیره نشد: {ex.Message}"
                : $"The file was not stored: {ex.Message}";
        }

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Corrects a destination.
    ///
    /// <para>
    /// Destinations could only be created, so a rotated key or a moved bucket meant adding a second
    /// destination and leaving the first one on the page failing every night.
    /// </para>
    ///
    /// <para>
    /// <b>A blank secret leaves the stored one alone.</b> The form cannot show what is there — it is
    /// encrypted — so a blank box is somebody who did not want to change it, not somebody asking for
    /// the destination to lose its credentials. That is the same rule the database password field and
    /// the app protection form already follow.
    /// </para>
    /// </summary>
    [HttpPost("destinations/{id:guid}")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.BackupsManage)]
    public async Task<IActionResult> UpdateDestination(
        Guid id, string name, string? localPath,
        string? endpoint, string? bucket, string? region, string? accessKey, string? secretKey,
        string? sftpHost, int sftpPort, string? sftpUsername, string? sftpPassword,
        string? sftpDirectory, string? sftpHostKey, CancellationToken ct)
    {
        var destination = await db.BackupDestinations
            .FirstOrDefaultAsync(d => d.Id == id && d.WorkspaceId == WorkspaceId, ct);
        if (destination is null) return NotFound();

        // The type is deliberately not editable. Every other column means something different per
        // type, so turning a local directory into a bucket would keep a path in LocalPath that the S3
        // code never reads and leave the destination pointing at nothing while looking configured.
        var hostKey = string.IsNullOrWhiteSpace(sftpHostKey) ? destination.SftpHostKey : sftpHostKey.Trim();

        if (destination.Type == BackupDestinationType.Sftp
            && Harbora.Infrastructure.Backups.SftpTransfer.WhyUnusable(
                sftpHost ?? destination.SftpHost, sftpUsername ?? destination.SftpUsername, hostKey) is { } refusal)
        {
            // Checked before anything is written, the rule the plan and size forms follow: a
            // destination saved half-corrected is one the form said it had not saved.
            TempData["Error"] = refusal;
            return RedirectToAction(nameof(Index));
        }

        if (!string.IsNullOrWhiteSpace(name)) destination.Name = name.Trim();

        if (destination.Type == BackupDestinationType.Local)
        {
            destination.LocalPath = localPath;
        }
        else if (destination.Type == BackupDestinationType.S3)
        {
            destination.Endpoint = endpoint;
            destination.Bucket = bucket;
            destination.Region = region;
            destination.AccessKey = accessKey;
            if (!string.IsNullOrWhiteSpace(secretKey)) destination.EncryptedSecretKey = protector.Protect(secretKey);
        }
        else
        {
            destination.SftpHost = sftpHost;
            destination.SftpPort = sftpPort <= 0 ? 22 : sftpPort;
            destination.SftpUsername = sftpUsername;
            destination.SftpDirectory = sftpDirectory;
            destination.SftpHostKey = hostKey;
            if (!string.IsNullOrWhiteSpace(sftpPassword))
                destination.EncryptedSftpPassword = protector.Protect(sftpPassword);
        }

        await db.SaveChangesAsync(ct);

        // Says what a correction does not do. Artifacts already stored keep the reference they were
        // given, so moving a destination does not move what is already in the old one.
        TempData["Message"] = IsFa
            ? $"«{destination.Name}» ذخیره شد. فایل‌هایی که از قبل ذخیره شده‌اند همان‌جا که هستند می‌مانند."
            : $"'{destination.Name}' saved. Artifacts already stored stay where they are.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Stops offering a destination.
    ///
    /// <para>
    /// Refused while any backup or schedule still points at it, for the reason a plan and a resource
    /// tier are: a destination that vanished leaves rows naming a place nobody can reach, so the
    /// download and restore buttons beside them stop working with no explanation. Said with a count,
    /// because "in use" without saying by what leaves somebody hunting.
    /// </para>
    ///
    /// <para>
    /// Confirmed by typing the same word the backup Delete and Restore actions already ask for
    /// (do-not-change list item 19: extend the destructive-confirmation pattern, never downgrade to a
    /// native <c>confirm()</c> — which is what this button used before, the one native dialog left on
    /// a page that had otherwise already moved past it).
    /// </para>
    /// </summary>
    [HttpPost("destinations/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.BackupsManage)]
    public async Task<IActionResult> DeleteDestination(Guid id, string confirm, CancellationToken ct)
    {
        var destination = await db.BackupDestinations
            .FirstOrDefaultAsync(d => d.Id == id && d.WorkspaceId == WorkspaceId, ct);
        if (destination is null) return NotFound();

        if (confirm != "DELETE")
        {
            TempData["Error"] = IsFa
                ? "حذف تأیید نشد؛ هیچ مقصدی برداشته نشد."
                : "The removal was not confirmed. Nothing was removed.";
            return RedirectToAction(nameof(Index));
        }

        var backups = await db.Backups.CountAsync(b => b.DestinationId == id, ct);
        var schedules = await db.BackupSchedules.CountAsync(s => s.DestinationId == id, ct);
        // 3.1 (round-2 market-gaps plan): WalSegment carries a real FK (Restrict) onto this table,
        // unlike Backup/BackupSchedule's loose DestinationId — so without this check a workspace with
        // WAL history but no ordinary backup here would sail past the count above and hit a raw SQL
        // foreign-key error instead of this sentence.
        var walSegments = await db.WalSegments.CountAsync(w => w.DestinationId == id, ct);

        if (backups + schedules + walSegments > 0)
        {
            TempData["Error"] = IsFa
                ? $"{backups} نسخهٔ پشتیبان، {schedules} زمان‌بندی و {walSegments} بخش WAL به «{destination.Name}» اشاره می‌کنند. اول آن‌ها را پاک یا جابه‌جا کنید، وگرنه دکمهٔ دانلود و بازگردانی‌شان بی‌صدا از کار می‌افتد."
                : $"{backups} backup(s), {schedules} schedule(s) and {walSegments} WAL segment(s) point at '{destination.Name}'. "
                  + "Remove or move those first, or their download and restore buttons stop working with no explanation.";
            return RedirectToAction(nameof(Index));
        }

        db.BackupDestinations.Remove(destination);
        await db.SaveChangesAsync(ct);

        TempData["Message"] = IsFa
            ? $"«{destination.Name}» برداشته شد."
            : $"'{destination.Name}' was removed.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Proves a destination works, by writing something to it and deleting it again.
    ///
    /// <para>
    /// A real round trip rather than a settings check: every way a destination fails looks identical
    /// to a correct form until something is actually sent, and finding out at the first real backup
    /// means finding out when the backup was needed.
    /// </para>
    /// </summary>
    [HttpPost("destinations/{id:guid}/test")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.BackupsManage)]
    public async Task<IActionResult> TestDestination(Guid id, CancellationToken ct)
    {
        var destination = await db.BackupDestinations.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && d.WorkspaceId == WorkspaceId, ct);
        if (destination is null) return NotFound();

        var refusal = await engine.TestDestinationAsync(id, ct);

        if (refusal is null)
            TempData["Message"] = IsFa
                ? $"«{destination.Name}» جواب داد: نوشتن و پاک کردن هر دو کار کرد."
                : $"'{destination.Name}' answered: writing and deleting both worked.";
        else
            TempData["Error"] = IsFa
                ? $"«{destination.Name}» کار نکرد: {refusal}"
                : $"'{destination.Name}' did not work: {refusal}";

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Corrects a schedule, or pauses it.
    ///
    /// <para>
    /// Schedules could only be created and deleted, so changing an interval meant losing the row —
    /// and pausing one for a maintenance window was not possible at all, which is what
    /// <c>IsEnabled</c> was on the entity for.
    /// </para>
    ///
    /// <para>
    /// The retention figure goes through the plan's own check, exactly as it does on creation. A
    /// schedule created inside the plan's limit and then edited past it would otherwise keep more
    /// copies than the tenant is entitled to, on the one screen where nothing would say so.
    /// </para>
    /// </summary>
    [HttpPost("schedules/{id:guid}")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.BackupsManage)]
    public async Task<IActionResult> UpdateSchedule(
        Guid id, int intervalHours, int retentionCount, bool enabled, CancellationToken ct)
    {
        var schedule = await db.BackupSchedules
            .FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (schedule is null) return NotFound();

        retentionCount = Math.Max(1, retentionCount);

        var retentionCheck = await quota.CanUseBackupRetentionAsync(WorkspaceId, retentionCount, ct);
        if (!retentionCheck.Allowed)
        {
            TempData["Error"] = (IsFa ? retentionCheck.ReasonFa : null) ?? retentionCheck.Reason;
            return RedirectToAction(nameof(Index));
        }

        var wasEnabled = schedule.IsEnabled;

        schedule.IntervalHours = Math.Max(1, intervalHours);
        schedule.RetentionCount = retentionCount;
        schedule.IsEnabled = enabled;

        // Cleared when a paused schedule is switched back on, so the scheduler works out the next run
        // from now. Left as it was, a schedule paused for a week would come back with a NextRunAt in
        // the past and fire immediately — during whatever the pause was protecting.
        if (enabled && !wasEnabled) schedule.NextRunAt = null;

        await db.SaveChangesAsync(ct);

        TempData["Message"] = enabled
            ? IsFa
                ? $"زمان‌بندی ذخیره شد: هر {schedule.IntervalHours} ساعت، نگهداری {schedule.RetentionCount} نسخه."
                : $"Schedule saved: every {schedule.IntervalHours} hours, keeping {schedule.RetentionCount}."
            : IsFa
                ? "زمان‌بندی موقتاً متوقف شد. نسخه‌های موجود پاک نمی‌شوند."
                : "Schedule paused. The backups it has already taken are not removed.";

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// How large an uploaded archive may be.
    ///
    /// <para>
    /// Deliberately generous — a database export is not small — and deliberately bounded: the file is
    /// streamed to the staging directory, and a request with no ceiling is a way to fill the disk the
    /// backups themselves live on.
    /// </para>
    /// </summary>
    private const long ImportSizeLimitBytes = 8L * 1024 * 1024 * 1024;
}
