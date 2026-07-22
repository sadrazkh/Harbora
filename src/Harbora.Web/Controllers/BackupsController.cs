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
public sealed class BackupsController(
    HarboraDbContext db,
    IBackupEngine engine,
    ISecretProtector protector,
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
        };

        vm.Targets.Add(($"{BackupType.FullPlatform}|platform", "🌐 Full platform"));
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

    [HttpPost("{id:guid}/restore")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.BackupsRestore)]
    public async Task<IActionResult> Restore(Guid id, string confirm, CancellationToken ct)
    {
        if (!await OwnsAsync(id, ct)) return NotFound();
        if (confirm != "RESTORE")
        {
            TempData["Error"] = "Restore not confirmed.";
            return RedirectToAction(nameof(Index));
        }
        await engine.RestoreAsync(id, ct);
        TempData["Message"] = "Restore completed.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("destinations")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.BackupsManage)]
    public async Task<IActionResult> CreateDestination(
        string name, BackupDestinationType type, string? localPath,
        string? endpoint, string? bucket, string? region, string? accessKey, string? secretKey, CancellationToken ct)
    {
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
            EncryptedSecretKey = string.IsNullOrWhiteSpace(secretKey) ? null : protector.Protect(secretKey)
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

    private Task<bool> OwnsAsync(Guid backupId, CancellationToken ct) =>
        db.Backups.AnyAsync(b => b.Id == backupId && b.WorkspaceId == WorkspaceId, ct);

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
