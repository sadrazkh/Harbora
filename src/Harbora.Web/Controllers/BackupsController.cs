using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Backups: manual + scheduled runs against local or S3 destinations, download, and restore
/// (guarded by an explicit confirm since restore overwrites data).
/// </summary>
[Authorize]
[Route("backups")]
public sealed partial class BackupsController(
    HarboraDbContext db,
    IBackupEngine engine,
    ISecretProtector protector,
    Harbora.Infrastructure.Backups.BackupDeliveryService delivery,
    IHttpClientFactory httpFactory,
    Harbora.Infrastructure.Security.ProjectAccessService access,
    IQuotaService quota,
    ICurrentUser currentUser) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Backups";
        await EnsureDefaultDestinationAsync(ct);

        var vm = new BackupsPageViewModel
        {
            Backups = await db.Backups.Include(b => b.Destination)
                .Where(b => b.WorkspaceId == WorkspaceId)
                .OrderByDescending(b => b.CreatedAt).Take(50).ToListAsync(ct),
            Destinations = await db.BackupDestinations.Where(d => d.WorkspaceId == WorkspaceId).ToListAsync(ct),
            Schedules = await db.BackupSchedules.Where(s => s.WorkspaceId == WorkspaceId).ToListAsync(ct),
            Deliveries = await db.BackupDeliveries.Where(d => d.WorkspaceId == WorkspaceId).ToListAsync(ct),
        };

        vm.Targets.Add(($"{BackupType.FullPlatform}|platform", "🌐 Full platform"));
        // The full-workspace export includes platform settings and therefore belongs only to the
        // provider workspace. Customer workspaces still get app and database targets below.
        if (!await db.Workspaces.AnyAsync(w => w.Id == WorkspaceId && w.IsDefault, ct))
            vm.Targets.Clear();
        foreach (var app in await db.Apps.Where(a => a.WorkspaceId == WorkspaceId).ToListAsync(ct))
            vm.Targets.Add(($"{BackupType.AppConfig}|{app.Id}", $"📦 {app.Name} (config)"));
        foreach (var svc in await db.ManagedServices.Where(s => s.WorkspaceId == WorkspaceId).ToListAsync(ct))
            vm.Targets.Add(($"{BackupType.Database}|{svc.Id}", $"🗄 {svc.Name} (data)"));

        return View(vm);
    }

    [HttpPost("run")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.BackupsRun)]
    public async Task<IActionResult> Run(string target, Guid destinationId, CancellationToken ct)
    {
        if (!TryParseTarget(target, out var type, out var reference))
        {
            TempData["Error"] = "Invalid backup target.";
            return RedirectToAction(nameof(Index));
        }
        if (!await OwnsTargetAsync(type, reference, Capabilities.BackupsRun, ct)
            || !await db.BackupDestinations.AnyAsync(d => d.Id == destinationId && d.WorkspaceId == WorkspaceId, ct))
        {
            TempData["Error"] = "The backup target or destination does not belong to this workspace.";
            return RedirectToAction(nameof(Index));
        }
        await engine.QueueBackupAsync(WorkspaceId, type, reference, destinationId, scheduled: false, ct);
        TempData["Message"] = "Backup queued.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        if (!await OwnsAsync(id, ct)) return NotFound();
        var (stream, fileName) = await engine.OpenArtifactAsync(id, ct);
        return File(stream, "application/octet-stream", fileName);
    }

    /// <summary>
    /// Dry run: prove the artifact is intact and readable without touching live data. A backup
    /// nobody has ever verified is a promise, not a safety net.
    /// </summary>
    [HttpPost("{id:guid}/verify")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.BackupsRun)]
    public async Task<IActionResult> Verify(Guid id, CancellationToken ct)
    {
        if (!await OwnsAsync(id, ct)) return NotFound();

        var result = await engine.VerifyAsync(id, ct);
        if (result.IsRestorable)
            TempData["Message"] = $"Backup verified — restorable ({result.Checks.Count(c => c.Passed)} checks passed).";
        else
            TempData["Error"] = $"Backup is NOT restorable: {result.Reason}";

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/restore")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.BackupsRestore)]
    public async Task<IActionResult> Restore(Guid id, string confirm, CancellationToken ct)
    {
        if (!await MayRestoreAsync(id, ct)) return NotFound();
        if (confirm != "RESTORE")
        {
            TempData["Error"] = "Restore not confirmed.";
            return RedirectToAction(nameof(Index));
        }
        try
        {
            await engine.RestoreAsync(id, ct);
        }
        catch (InvalidOperationException ex)
        {
            // Integrity gate rejected the artifact — say so plainly; live data was not touched.
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Index));
        }
        TempData["Message"] = "Restore completed.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("destinations")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.BackupsManage)]
    public async Task<IActionResult> CreateDestination(
        string name, BackupDestinationType type, string? localPath,
        string? endpoint, string? bucket, string? region, string? accessKey, string? secretKey,
        string? sftpHost, int sftpPort, string? sftpUsername, string? sftpPassword,
        string? sftpDirectory, string? sftpHostKey, CancellationToken ct)
    {
        // Refused at the point of creation rather than at the first backup: a destination Harbora
        // cannot verify the identity of would be handed the backup and the password to reach it.
        if (type == BackupDestinationType.Sftp
            && Harbora.Infrastructure.Backups.SftpTransfer.WhyUnusable(sftpHost, sftpUsername, sftpHostKey) is { } refusal)
        {
            TempData["Error"] = refusal;
            return RedirectToAction(nameof(Index));
        }

        db.BackupDestinations.Add(new BackupDestination
        {
            WorkspaceId = WorkspaceId,
            Name = name,
            Type = type,
            LocalPath = type == BackupDestinationType.Local ? localPath : null,
            Endpoint = endpoint,
            Bucket = bucket,
            Region = region,
            AccessKey = accessKey,
            EncryptedSecretKey = string.IsNullOrWhiteSpace(secretKey) ? null : protector.Protect(secretKey),
            SftpHost = sftpHost,
            SftpPort = sftpPort <= 0 ? 22 : sftpPort,
            SftpUsername = sftpUsername,
            EncryptedSftpPassword = string.IsNullOrWhiteSpace(sftpPassword) ? null : protector.Protect(sftpPassword),
            SftpDirectory = sftpDirectory,
            SftpHostKey = sftpHostKey?.Trim()
        });
        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("schedules")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.BackupsManage)]
    public async Task<IActionResult> CreateSchedule(string target, Guid destinationId, int intervalHours, int retentionCount, CancellationToken ct)
    {
        if (!TryParseTarget(target, out var type, out var reference))
        {
            TempData["Error"] = "Invalid schedule target.";
            return RedirectToAction(nameof(Index));
        }
        if (!await OwnsTargetAsync(type, reference, Capabilities.BackupsManage, ct)
            || !await db.BackupDestinations.AnyAsync(d => d.Id == destinationId && d.WorkspaceId == WorkspaceId, ct))
        {
            TempData["Error"] = "The backup target or destination does not belong to this workspace.";
            return RedirectToAction(nameof(Index));
        }
        var quotaCheck = await quota.CanAddGovernedResourcesAsync(WorkspaceId,
            new GovernanceQuotaDelta(BackupSchedules: 1), ct);
        if (!quotaCheck.Allowed)
        {
            TempData["Error"] = quotaCheck.Reason;
            return RedirectToAction(nameof(Index));
        }
        db.BackupSchedules.Add(new BackupSchedule
        {
            WorkspaceId = WorkspaceId, DestinationId = destinationId, Type = type, TargetRef = reference,
            IntervalHours = Math.Max(1, intervalHours), RetentionCount = Math.Max(1, retentionCount), IsEnabled = true
        });
        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("schedules/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.BackupsManage)]
    public async Task<IActionResult> DeleteSchedule(Guid id, CancellationToken ct)
    {
        await db.BackupSchedules.Where(s => s.Id == id && s.WorkspaceId == WorkspaceId).ExecuteDeleteAsync(ct);
        return RedirectToAction(nameof(Index));
    }

    // --- helpers ---

    /// <summary>
    /// Judged by what the backup is a backup <i>of</i>: an export of production's database is
    /// production's data whatever list it appears in.
    /// </summary>
    private Task<bool> OwnsAsync(Guid backupId, CancellationToken ct) =>
        access.CanTouchBackupAsync(backupId, Capabilities.BackupsRun, ct);

    /// <summary>The same question for the destructive action.</summary>
    private Task<bool> MayRestoreAsync(Guid backupId, CancellationToken ct) =>
        access.CanTouchBackupAsync(backupId, Capabilities.BackupsRestore, ct);

    private async Task<bool> OwnsTargetAsync(
        BackupType type, string reference, string capability, CancellationToken ct)
    {
        if (type == BackupType.FullPlatform)
            return reference == "platform"
                && await db.Workspaces.AnyAsync(w => w.Id == WorkspaceId && w.IsDefault, ct);
        if (!Guid.TryParse(reference, out var id)) return false;
        if (type is BackupType.AppConfig or BackupType.Volume)
            return await access.CanTouchAppAsync(id, capability, ct);
        if (type is BackupType.Database or BackupType.Service)
            return await access.CanTouchServiceAsync(id, capability, ct);
        return false;
    }

    private async Task EnsureDefaultDestinationAsync(CancellationToken ct)
    {
        if (await db.BackupDestinations.AnyAsync(d => d.WorkspaceId == WorkspaceId, ct)) return;
        db.BackupDestinations.Add(new BackupDestination
        {
            WorkspaceId = WorkspaceId, Name = "Local", Type = BackupDestinationType.Local, IsDefault = true
        });
        await db.SaveChangesAsync(ct);
    }

    private static bool TryParseTarget(string value, out BackupType type, out string reference)
    {
        type = default; reference = string.Empty;
        var parts = (value ?? "").Split('|', 2);
        if (parts.Length != 2 || !Enum.TryParse(parts[0], out type)) return false;
        reference = parts[1];
        return true;
    }
}
