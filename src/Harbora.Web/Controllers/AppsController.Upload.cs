using Harbora.Application.Abstractions;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

public sealed partial class AppsController
{
    /// <summary>
    /// A new version of an app that came from a zip, and so has no repository to pull a new version
    /// from. Until this existed only the CLI could update one, and somebody who gave us a zip has no
    /// CLI. The rest of the way is the pipeline's, on the same <c>SourceArchivePath</c> the CLI's push
    /// uses: it consumes the archive, and a refused queue here deletes it.
    /// </summary>
    [HttpPost("/apps/{id:guid}/upload")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsDeploy)]
    [RequestSizeLimit(UploadedArchive.MaxBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadedArchive.MaxBytes)]
    public async Task<IActionResult> Upload(Guid id, IFormFile? file, CancellationToken ct)
    {
        if (!await MayAsync(id, Capabilities.AppsDeploy, ct)) return NotFound();

        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        IActionResult Refused(string message)
        {
            TempData["Error"] = message;
            return RedirectToAction(nameof(Details), new { id });
        }

        // Deploying a zip onto an app that tracks a repository would run code the repository has
        // never seen, and the next push would quietly take it away again.
        if (app.SourceType != AppSourceType.Upload)
            return Refused(IsFa
                ? "این اپ از روی فایل ساخته نشده، پس نسخه‌ی تازه‌اش هم از همان منبع خودش می‌آید."
                : "This app wasn't made from an upload, so its new versions come from its own source.");

        if (file is null || file.Length == 0)
            return Refused(IsFa ? "فایلی انتخاب نشده بود، یا فایل خالی بود." : "No file was chosen, or the file was empty.");
        if (UploadedArchive.WhyNot(file, IsFa) is { } notAZip)
            return Refused(notAZip);

        // The same check the deploy button makes, before anything is written to disk.
        var check = await quota.CanAddAppAsync(WorkspaceId, app.InstanceSizeKey, excludeAppId: app.Id, ct);
        if (!check.Allowed)
            return Refused((IsFa ? check.ReasonFa : null) ?? check.Reason ?? "");

        var archivePath = UploadedArchive.PathForApp(runtimeOptions.Value.WorkDir, app.Slug, file.FileName);
        await UploadedArchive.SaveAsync(file, archivePath, ct);

        Guid deploymentId;
        try
        {
            deploymentId = await deployEngine.QueueDeploymentAsync(
                new DeploymentRequest(app.Id, DeploymentTrigger.Manual, currentUser.UserId ?? Guid.Empty,
                    SourceArchivePath: archivePath), ct);
        }
        catch (QuotaRefusedException ex)
        {
            UploadedArchive.Delete(archivePath);
            return Refused((IsFa ? ex.ReasonFa : null) ?? ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // A rollback is running. The upload is an orphan, so it goes rather than leaking a file per
            // refused try.
            UploadedArchive.Delete(archivePath);
            return Refused(ex.Message);
        }

        // A deployment already in flight is not a refusal: the engine coalesces onto it and answers with
        // its id. That one will never read this file, so taking the answer at its word would land
        // somebody on the old build's log believing their fix was running.
        if (await db.Deployments.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(d => d.Id == deploymentId && d.SourceArchivePath != archivePath, ct))
        {
            UploadedArchive.Delete(archivePath);
            return Refused(IsFa
                ? "یک ساخت هنوز در جریان است، پس این نسخه اجرا نشد. وقتی تمام شد، همین فایل را دوباره بفرست."
                : "A build is still running, so this version wasn't run. Send it again when that finishes.");
        }

        await audit.LogAsync("app.deploy", "app", id.ToString(), ClientIp, workspaceId: WorkspaceId, ct: ct);

        return RedirectToAction("Details", "Deployments", new { id = deploymentId });
    }
}
