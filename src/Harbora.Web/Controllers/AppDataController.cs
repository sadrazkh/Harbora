using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Infrastructure.Storage;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// The files an application keeps.
///
/// An app could declare a volume, mount it, and fill it with the only copy of somebody's uploads —
/// and there was no way to look at it. Not from the panel, not through the API. The answer to
/// "where did that file go" was to open a shell on the host, which is exactly the thing a platform
/// exists to make unnecessary.
///
/// This is the most dangerous surface in the panel and is treated as one. It needs
/// <see cref="Capabilities.AppsEnv"/> rather than the weaker read capability the rest of an
/// application's page uses, every path goes through <see cref="VolumePath"/>, and every write is
/// audited with the path it touched.
/// </summary>
[Authorize]
[Route("apps/{id:guid}/data")]
public sealed class AppDataController(
    HarboraDbContext db,
    VolumeFileService files,
    Harbora.Infrastructure.Security.ProjectAccessService access,
    IAuditLogger audit,
    ICurrentUser currentUser) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
    private bool IsFa => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    [HttpGet("")]
    public async Task<IActionResult> Index(Guid id, Guid? volumeId, string? path, CancellationToken ct)
    {
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsEnv, ct)) return Forbid();

        var app = await db.Apps.AsNoTracking()
            .Include(a => a.Volumes)
            .FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        var volume = volumeId is { } chosen
            ? app.Volumes.FirstOrDefault(v => v.Id == chosen)
            : app.Volumes.OrderBy(v => v.MountPath).FirstOrDefault();

        var model = new AppDataViewModel
        {
            AppId = app.Id,
            AppName = app.Name,
            Volumes = app.Volumes.OrderBy(v => v.MountPath)
                .Select(v => new AppDataVolumeViewModel(v.Id, v.Name, v.MountPath, v.ReadOnly))
                .ToList(),
            SelectedVolumeId = volume?.Id
        };

        if (volume is null)
        {
            ViewData["Title"] = app.Name;
            return View(model);
        }

        // A path that is not one is not an error page: it is somebody following a stale link, and
        // the root is the honest place to put them.
        var normalised = VolumePath.Normalise(path) ?? string.Empty;

        model = model with
        {
            Path = normalised,
            Parent = VolumePath.ParentOf(normalised),
            Entries = await files.ListAsync(app.ServerId, volume.Name, normalised, ct),
            IsReadOnly = volume.ReadOnly
        };

        ViewData["Title"] = app.Name;
        return View(model);
    }

    [HttpGet("download")]
    public async Task<IActionResult> Download(Guid id, Guid volumeId, string path, CancellationToken ct)
    {
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsEnv, ct)) return Forbid();

        var (app, volume, normalised) = await ResolveAsync(id, volumeId, path, ct);
        if (app is null || volume is null) return NotFound();
        if (normalised.Length == 0) return NotFound();

        var content = await files.ReadAsync(app!.ServerId, volume.Name, normalised, ct);
        if (content is null) return NotFound();

        await audit.LogAsync("app.data_read", "app", $"{app.Name}:{volume.Name}/{normalised}", ClientIp, ct: ct);

        // Always as an attachment, and always as bytes. Serving a file out of a customer's volume
        // inline would let them host script on the panel's own origin, which is the session every
        // other tenant is signed in with.
        return File(content, "application/octet-stream", VolumePath.NameOf(normalised));
    }

    [HttpPost("upload")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(VolumeFileService.MaxFileBytes + 1024 * 1024)]
    public async Task<IActionResult> Upload(
        Guid id, Guid volumeId, string? path, IFormFile? file, CancellationToken ct)
    {
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsEnv, ct)) return Forbid();

        var (app, volume, normalised) = await ResolveAsync(id, volumeId, path, ct);
        if (app is null || volume is null) return NotFound();

        if (file is null || file.Length == 0)
            return Back(id, volumeId, normalised, IsFa ? "فایلی انتخاب نشده بود." : "No file was chosen.", error: true);

        if (volume.ReadOnly)
            return Back(id, volumeId, normalised,
                IsFa ? "این والیوم فقط‌خواندنی است." : "This volume is mounted read-only.", error: true);

        // The browser's filename, not a path. A file called "../../etc/passwd" is the oldest upload
        // trick there is, and the name is taken apart by the same rule as everything else.
        var name = VolumePath.Normalise(Path.GetFileName(file.FileName));
        if (name is null || name.Length == 0)
            return Back(id, volumeId, normalised,
                IsFa ? "نام فایل قابل استفاده نیست." : "That filename cannot be used.", error: true);

        var target = normalised.Length == 0 ? name : $"{normalised}/{name}";

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);

        var outcome = await files.WriteAsync(app!.ServerId, volume.Name, target, buffer.ToArray(), ct);
        await audit.LogAsync("app.data_write", "app", $"{app.Name}:{volume.Name}/{target}", ClientIp, ct: ct);

        return Back(id, volumeId, normalised,
            outcome.Ok ? (IsFa ? $"«{name}» بارگذاری شد." : $"{name} was uploaded.") : outcome.Reason,
            error: !outcome.Ok);
    }

    /// <summary>Saves an edited text file back over itself.</summary>
    [HttpPost("save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(
        Guid id, Guid volumeId, string path, string? content, CancellationToken ct)
    {
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsEnv, ct)) return Forbid();

        var (app, volume, normalised) = await ResolveAsync(id, volumeId, path, ct);
        if (app is null || volume is null) return NotFound();
        if (normalised.Length == 0) return NotFound();

        if (volume.ReadOnly)
            return Back(id, volumeId, VolumePath.ParentOf(normalised) ?? string.Empty,
                IsFa ? "این والیوم فقط‌خواندنی است." : "This volume is mounted read-only.", error: true);

        var outcome = await files.WriteAsync(
            app!.ServerId, volume.Name, normalised,
            System.Text.Encoding.UTF8.GetBytes(content ?? string.Empty), ct);

        await audit.LogAsync("app.data_write", "app", $"{app.Name}:{volume.Name}/{normalised}", ClientIp, ct: ct);

        return Back(id, volumeId, VolumePath.ParentOf(normalised) ?? string.Empty,
            outcome.Ok
                ? (IsFa
                    ? "ذخیره شد. برای اینکه اپ آن را بخواند ممکن است لازم باشد ری‌استارت شود."
                    : "Saved. The application may need a restart before it reads it.")
                : outcome.Reason,
            error: !outcome.Ok);
    }

    [HttpPost("delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, Guid volumeId, string path, CancellationToken ct)
    {
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsEnv, ct)) return Forbid();

        var (app, volume, normalised) = await ResolveAsync(id, volumeId, path, ct);
        if (app is null || volume is null) return NotFound();

        if (volume.ReadOnly)
            return Back(id, volumeId, normalised,
                IsFa ? "این والیوم فقط‌خواندنی است." : "This volume is mounted read-only.", error: true);

        var outcome = await files.DeleteAsync(app!.ServerId, volume.Name, normalised, ct);
        await audit.LogAsync("app.data_delete", "app", $"{app.Name}:{volume.Name}/{normalised}", ClientIp, ct: ct);

        return Back(id, volumeId, VolumePath.ParentOf(normalised) ?? string.Empty,
            outcome.Ok
                ? (IsFa ? "حذف شد." : "Deleted.")
                : outcome.Reason,
            error: !outcome.Ok);
    }

    [HttpPost("folder")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> NewFolder(
        Guid id, Guid volumeId, string? path, string name, CancellationToken ct)
    {
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsEnv, ct)) return Forbid();

        var (app, volume, normalised) = await ResolveAsync(id, volumeId, path, ct);
        if (app is null || volume is null) return NotFound();

        var folder = VolumePath.Normalise(name);
        if (folder is null || folder.Length == 0 || folder.Contains('/'))
            return Back(id, volumeId, normalised,
                IsFa ? "نام پوشه قابل استفاده نیست." : "That folder name cannot be used.", error: true);

        var target = normalised.Length == 0 ? folder : $"{normalised}/{folder}";
        var outcome = await files.MakeDirectoryAsync(app!.ServerId, volume.Name, target, ct);
        await audit.LogAsync("app.data_write", "app", $"{app.Name}:{volume.Name}/{target}", ClientIp, ct: ct);

        return Back(id, volumeId, normalised,
            outcome.Ok ? (IsFa ? "پوشه ساخته شد." : "Folder created.") : outcome.Reason,
            error: !outcome.Ok);
    }

    /// <summary>The file's text, for the inline editor. Refused when it is not text.</summary>
    [HttpGet("edit")]
    public async Task<IActionResult> Edit(Guid id, Guid volumeId, string path, CancellationToken ct)
    {
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsEnv, ct)) return Forbid();

        var (app, volume, normalised) = await ResolveAsync(id, volumeId, path, ct);
        if (app is null || volume is null) return NotFound();
        if (normalised.Length == 0) return NotFound();

        var content = await files.ReadAsync(app!.ServerId, volume.Name, normalised, ct);
        if (content is null) return NotFound();

        // A NUL byte is the oldest and most reliable sign of a binary file. Opening one in a
        // textarea and saving it back would corrupt it silently, and the person would have no way
        // of knowing until whatever reads it failed.
        if (Array.IndexOf(content, (byte)0) >= 0)
            return Back(id, volumeId, VolumePath.ParentOf(normalised) ?? string.Empty,
                IsFa
                    ? "این فایل متنی نیست و در ویرایشگر باز نمی‌شود. دانلودش کنید."
                    : "That is not a text file, so it cannot be opened in the editor. Download it instead.",
                error: true);

        return View(new AppDataEditViewModel
        {
            AppId = id,
            AppName = app.Name,
            VolumeId = volume.Id,
            Path = normalised,
            Content = System.Text.Encoding.UTF8.GetString(content),
            IsReadOnly = volume.ReadOnly
        });
    }

    private async Task<(Harbora.Domain.Apps.App? App, Harbora.Domain.Apps.Volume? Volume, string Normalised)>
        ResolveAsync(Guid id, Guid volumeId, string? path, CancellationToken ct)
    {
        var app = await db.Apps.AsNoTracking()
            .Include(a => a.Volumes)
            .FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);

        // The volume has to belong to this application. Without that check the id in the query
        // string picks any volume on the platform, and the access check above only ever looked at
        // the app.
        var volume = app?.Volumes.FirstOrDefault(v => v.Id == volumeId);

        return (app, volume, VolumePath.Normalise(path) ?? string.Empty);
    }

    private IActionResult Back(Guid id, Guid volumeId, string path, string? message, bool error = false)
    {
        TempData[error ? "Error" : "Message"] = message;
        return RedirectToAction(nameof(Index), new { id, volumeId, path });
    }
}
