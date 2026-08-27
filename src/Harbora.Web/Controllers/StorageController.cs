using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Storage;
using Harbora.Infrastructure.Storage;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Object storage: a bucket, a key, and how full it is.
///
/// The platform had volumes, which belong to one application and go away with it, and backups,
/// which are not something an application writes to. There was nowhere to put the third thing
/// people actually need — a bucket an application uploads into and a browser downloads from.
///
/// Nothing here pretends. When an operator has not set object storage up, the page says which
/// configuration keys are missing and the create button is not offered: a bucket row for a bucket
/// that does not exist is the failure this codebase keeps finding.
/// </summary>
[Authorize]
[Route("storage")]
public sealed class StorageController(
    HarboraDbContext db,
    ObjectStorageAdmin storage,
    Harbora.Infrastructure.Storage.BucketObjectService objects,
    ISecretProtector protector,
    IAuditLogger audit,
    ICurrentUser currentUser,
    Harbora.Infrastructure.Security.ProjectAccessService access) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
    private bool IsFa => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    // ---- Browsing what is in a bucket ----
    //
    // Every action re-checks that the bucket belongs to the caller's workspace. The bucket id is in
    // the URL, and a page that trusted it would be a page that browses another tenant's objects
    // through a guessed guid.

    [HttpGet("buckets/{id:guid}/objects")]
    public async Task<IActionResult> Objects(Guid id, string? prefix, CancellationToken ct)
    {
        var bucket = await OwnedAsync(id, ct);
        if (bucket is null) return NotFound();

        ViewData["Title"] = bucket.Name;
        ViewBag.Bucket = bucket;
        ViewBag.Prefix = Harbora.Infrastructure.Storage.ObjectKey.NormalisePrefix(prefix) ?? "";
        ViewBag.Parent = Harbora.Infrastructure.Storage.ObjectKey.Parent(prefix);
        ViewBag.Objects = await objects.ListAsync(id, prefix, ct);
        return View();
    }

    [HttpGet("buckets/{id:guid}/objects/download")]
    public async Task<IActionResult> DownloadObject(Guid id, string key, CancellationToken ct)
    {
        if (await OwnedAsync(id, ct) is null) return NotFound();

        var bytes = await objects.ReadAsync(id, key, ct);
        if (bytes is null) return NotFound();

        await audit.LogAsync("storage.object_read", "bucket", $"{id}:{key}", ClientIp, workspaceId: WorkspaceId, ct: ct);

        // application/octet-stream on purpose: an object is whatever a customer uploaded, and
        // serving it as its claimed type from the panel's own origin is a stored-XSS delivery.
        return File(bytes, "application/octet-stream", System.IO.Path.GetFileName(key));
    }

    [HttpPost("buckets/{id:guid}/objects/upload")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> UploadObject(Guid id, string? prefix, IFormFile? file, CancellationToken ct)
    {
        if (await OwnedAsync(id, ct) is null) return NotFound();

        if (file is null || file.Length == 0)
        {
            TempData["Error"] = IsFa ? "فایلی انتخاب نشد." : "No file was chosen.";
            return RedirectToAction(nameof(Objects), new { id, prefix });
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);

        var folder = Harbora.Infrastructure.Storage.ObjectKey.NormalisePrefix(prefix) ?? "";
        var key = string.IsNullOrEmpty(folder)
            ? System.IO.Path.GetFileName(file.FileName)
            : folder + "/" + System.IO.Path.GetFileName(file.FileName);

        var outcome = await objects.WriteAsync(id, key, buffer.ToArray(), ct);
        if (outcome.Ok)
            await audit.LogAsync("storage.object_write", "bucket", $"{id}:{key}", ClientIp, workspaceId: WorkspaceId, ct: ct);

        TempData[outcome.Ok ? "Message" : "Error"] = outcome.Ok
            ? (IsFa ? $"«{key}» بارگذاری شد." : $"{key} was uploaded.")
            : outcome.Reason;

        return RedirectToAction(nameof(Objects), new { id, prefix });
    }

    [HttpPost("buckets/{id:guid}/objects/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> DeleteObject(Guid id, string key, string? prefix, CancellationToken ct)
    {
        if (await OwnedAsync(id, ct) is null) return NotFound();

        var outcome = await objects.DeleteAsync(id, key, ct);
        if (outcome.Ok)
            await audit.LogAsync("storage.object_delete", "bucket", $"{id}:{key}", ClientIp, workspaceId: WorkspaceId, ct: ct);

        TempData[outcome.Ok ? "Message" : "Error"] = outcome.Ok
            ? (IsFa ? "حذف شد." : "Deleted.")
            : outcome.Reason;

        return RedirectToAction(nameof(Objects), new { id, prefix });
    }

    private async Task<Harbora.Domain.Storage.StorageBucket?> OwnedAsync(Guid id, CancellationToken ct) =>
        await db.StorageBuckets.FirstOrDefaultAsync(b => b.Id == id && b.WorkspaceId == WorkspaceId, ct);

    [HttpGet("")]
    public async Task<IActionResult> Index(Guid? reveal, CancellationToken ct)
    {
        ViewData["Title"] = "Storage";

        var buckets = await db.StorageBuckets.AsNoTracking()
            .Include(b => b.StoragePlan)
            .OrderBy(b => b.Name)
            .ToListAsync(ct);

        // F5 (2026-08-21 functions-and-services plan): every attachment, read once and grouped by
        // bucket — the same "who is this wired to" question DatabasesController.Index already asks
        // and answers in a single query rather than one per row.
        var bucketIds = buckets.Select(b => b.Id).ToList();
        var attachments = bucketIds.Count == 0
            ? []
            : await db.AppStorageBuckets.AsNoTracking()
                .Where(sb => bucketIds.Contains(sb.StorageBucketId))
                .Select(sb => new { sb.StorageBucketId, sb.AppId, sb.HasUnpublishedChanges, AppName = sb.App!.Name })
                .ToListAsync(ct);

        var apps = await db.Apps.AsNoTracking()
            .Where(a => a.WorkspaceId == WorkspaceId)
            .OrderBy(a => a.Name)
            .Select(a => new { a.Id, a.Name })
            .ToListAsync(ct);

        return View(new StoragePageViewModel
        {
            IsConfigured = storage.IsConfigured,
            WhatIsMissing = storage.WhatIsMissing(),
            Endpoint = storage.CustomerEndpoint,
            Plans = await db.StoragePlans.Where(p => p.IsEnabled)
                .OrderBy(p => p.SortOrder).ThenBy(p => p.MonthlyPrice).ToListAsync(ct),
            Buckets = buckets.Select(b =>
            {
                var attachedAppIds = attachments.Where(a => a.StorageBucketId == b.Id)
                    .Select(a => a.AppId).ToHashSet();
                return new StorageBucketViewModel(
                    b.Id, b.Name, b.AccessKey,
                    // Revealed only for the one bucket asked for, and only on an explicit click. A page
                    // that prints every secret is a page nobody can screen-share.
                    reveal == b.Id ? Unprotect(b.EncryptedSecretKey) : null,
                    b.QuotaBytes, b.UsedBytes, b.MeasuredAt, b.Status, b.FailureReason,
                    b.StoragePlan?.Name,
                    attachments.Where(a => a.StorageBucketId == b.Id)
                        .Select(a => new StorageAttachedAppViewModel(a.AppId, a.AppName, a.HasUnpublishedChanges)).ToList(),
                    apps.Where(a => !attachedAppIds.Contains(a.Id))
                        .Select(a => new StorageAttachableAppViewModel(a.Id, a.Name)).ToList());
            }).ToList(),
            RevealedBucketId = reveal
        });
    }

    [HttpPost("buckets")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Create(string name, Guid? storagePlanId, CancellationToken ct)
    {
        if (!storage.IsConfigured)
            return Back(IsFa
                ? $"فضای ذخیره‌سازی روی این نصب تنظیم نشده. کلیدهای لازم: {storage.WhatIsMissing()}"
                : $"Object storage is not configured on this installation. Missing: {storage.WhatIsMissing()}", error: true);

        // Checked before anything is written, and named by which rule was broken — "invalid name"
        // sends somebody to the documentation, "must be at least 3 characters" does not.
        if (BucketName.Check(name) is var refusal && refusal != BucketNameRefusal.None)
            return Back(Explain(refusal), error: true);

        // Across every workspace: the storage server has one namespace, so two tenants asking for
        // "uploads" collide there if they are not stopped here.
        if (await db.StorageBuckets.IgnoreQueryFilters().AnyAsync(b => b.Name == name, ct))
            return Back(IsFa ? "این نام از قبل گرفته شده." : "That name is already taken.", error: true);

        var plan = storagePlanId is { } chosen
            ? await db.StoragePlans.FirstOrDefaultAsync(p => p.Id == chosen && p.IsEnabled, ct)
            : await db.StoragePlans.FirstOrDefaultAsync(p => p.IsDefault && p.IsEnabled, ct);

        // A tier that limits how many buckets somebody may have only means something if it is
        // counted before the next one is made.
        if (plan is { MaxBuckets: > 0 })
        {
            var held = await db.StorageBuckets.CountAsync(b => b.WorkspaceId == WorkspaceId, ct);
            if (held >= plan.MaxBuckets)
                return Back(IsFa
                    ? $"پلن «{plan.Name}» حداکثر {plan.MaxBuckets} باکت می‌دهد و همه استفاده شده‌اند."
                    : $"The {plan.Name} plan allows {plan.MaxBuckets} bucket(s) and they are all in use.", error: true);
        }

        var result = await storage.CreateAsync(name, plan?.QuotaBytes ?? 0, ct);

        // Nothing is recorded when the server refused. A row in Failed state would be a bucket in
        // the list that cannot be used and cannot be explained; a refusal with the reason on screen
        // is the same information without the wreckage.
        if (!result.Ok || result.Credential is null)
            return Back(result.Reason, error: true);

        db.StorageBuckets.Add(new StorageBucket
        {
            WorkspaceId = WorkspaceId,
            Name = name,
            AccessKey = result.Credential.AccessKey,
            EncryptedSecretKey = protector.Protect(result.Credential.SecretKey),
            StoragePlanId = plan?.Id,
            // Copied, like an instance's memory limit: the plan can be edited later and a page
            // reporting "8 GB of 20 GB" has to go on meaning what it meant.
            QuotaBytes = plan?.QuotaBytes ?? 0,
            Status = BucketStatus.Ready
        });
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("storage.bucket_created", "bucket", name, ClientIp, workspaceId: WorkspaceId, ct: ct);

        return Back(IsFa ? $"باکت «{name}» ساخته شد." : $"Bucket {name} was created.");
    }

    /// <summary>
    /// Asks the storage server how much is in the bucket.
    ///
    /// On demand rather than on every page load: measuring runs a container, and a page that starts
    /// one per bucket per visit is a page that gets slower the more somebody has. The figure keeps
    /// its timestamp so it is read as a measurement taken at a moment rather than as a live number.
    /// </summary>
    [HttpPost("buckets/{id:guid}/measure")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Measure(Guid id, CancellationToken ct)
    {
        var bucket = await db.StorageBuckets.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bucket is null) return NotFound();

        var used = await storage.MeasureAsync(bucket.Name, ct);

        // The timestamp is written even when the figure is not: "asked, and it did not answer" and
        // "never asked" are different states, and the page shows them differently.
        bucket.MeasuredAt = DateTimeOffset.UtcNow;
        if (used is not null) bucket.UsedBytes = used;
        await db.SaveChangesAsync(ct);

        return Back(used is null
            ? (IsFa ? "اندازه‌گیری نشد؛ سرور ذخیره‌سازی جواب نداد." : "It could not be measured — the storage server did not answer.")
            : (IsFa ? "اندازه‌گیری شد." : "Measured."),
            error: used is null);
    }

    /// <summary>
    /// Refuses while any app is still attached, naming them — the <c>ProjectsController.Delete</c> /
    /// <c>ConfigGroupsController.Delete</c> idiom, reused rather than reinvented (F5, 2026-08-21
    /// functions-and-services plan) — rather than letting the FK's <c>DeleteBehavior.Restrict</c>
    /// surface as a raw constraint failure, or worse, deleting credentials an app's next deploy is
    /// still going to ask for.
    /// </summary>
    [HttpPost("buckets/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var bucket = await db.StorageBuckets.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bucket is null) return NotFound();

        var attachedTo = await db.AppStorageBuckets.AsNoTracking()
            .Where(sb => sb.StorageBucketId == id)
            .Select(sb => sb.App!.Name)
            .ToListAsync(ct);

        if (attachedTo.Count > 0)
        {
            return Back(IsFa
                ? $"این باکت هنوز به {NamedList(attachedTo)} متصل است. برای حذف، ابتدا آن را از همه‌ی اپ‌ها جدا کنید."
                : $"This bucket is still attached to {NamedList(attachedTo)}. Detach it from every app first, then delete it.",
                error: true);
        }

        var result = await storage.DeleteAsync(bucket.Name, bucket.AccessKey, ct);

        // The row stays when the server refused. Removing it would hide a bucket that still exists
        // and still holds somebody's objects, and nothing would ever list it again.
        if (!result.Ok) return Back(result.Reason, error: true);

        db.StorageBuckets.Remove(bucket);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("storage.bucket_deleted", "bucket", bucket.Name, ClientIp, workspaceId: WorkspaceId, ct: ct);

        return Back(IsFa ? "باکت حذف شد." : "The bucket was deleted.");
    }

    /// <summary>
    /// Attaches a bucket to an app at the back of its precedence order — the same
    /// <c>AttachConfigGroup</c> shape: current max <c>AttachOrder</c> + 1, never reused, and starts
    /// <c>HasUnpublishedChanges</c> true because nothing here is live until the app's own next
    /// deploy assembles its environment (F5, 2026-08-21 functions-and-services plan).
    /// </summary>
    [HttpPost("buckets/{id:guid}/attach")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> AttachBucket(Guid id, Guid appId, string? returnUrl, CancellationToken ct)
    {
        var bucket = await db.StorageBuckets.FirstOrDefaultAsync(b => b.Id == id && b.WorkspaceId == WorkspaceId, ct);
        if (bucket is null) return NotFound();

        if (!await access.CanTouchAppAsync(appId, Capabilities.AppsEnv, ct)) return NotFound();
        var appExists = await db.Apps.AsNoTracking().AnyAsync(a => a.Id == appId && a.WorkspaceId == WorkspaceId, ct);
        if (!appExists) return NotFound();

        if (await db.AppStorageBuckets.AnyAsync(sb => sb.AppId == appId && sb.StorageBucketId == id, ct))
            return BackTo(returnUrl, IsFa ? "این باکت از قبل به این اپ متصل است." : "This bucket is already attached.", error: true);

        var maxOrder = await db.AppStorageBuckets
            .Where(sb => sb.AppId == appId)
            .Select(sb => (int?)sb.AttachOrder)
            .MaxAsync(ct) ?? 0;

        db.AppStorageBuckets.Add(new AppStorageBucket
        {
            AppId = appId, StorageBucketId = id, AttachOrder = maxOrder + 1, HasUnpublishedChanges = true
        });
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("storage.bucket_attached", "bucket", $"{id}:{appId}", ClientIp, workspaceId: WorkspaceId, ct: ct);

        return BackTo(returnUrl, IsFa
            ? $"باکت «{bucket.Name}» متصل شد. متغیرهایش با استقرار بعدی این اپ اعمال می‌شوند."
            : $"Attached '{bucket.Name}'. Its variables apply on this app's next deploy.");
    }

    /// <summary>Removes the join row. The running container keeps the variables until the app's own
    /// next deploy — same as detaching a config group (F5, 2026-08-21 functions-and-services plan).</summary>
    [HttpPost("buckets/{id:guid}/detach")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> DetachBucket(Guid id, Guid appId, string? returnUrl, CancellationToken ct)
    {
        var bucket = await db.StorageBuckets.FirstOrDefaultAsync(b => b.Id == id && b.WorkspaceId == WorkspaceId, ct);
        if (bucket is null) return NotFound();

        if (!await access.CanTouchAppAsync(appId, Capabilities.AppsEnv, ct)) return NotFound();

        var join = await db.AppStorageBuckets.FirstOrDefaultAsync(sb => sb.AppId == appId && sb.StorageBucketId == id, ct);
        if (join is null) return NotFound();

        db.AppStorageBuckets.Remove(join);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("storage.bucket_detached", "bucket", $"{id}:{appId}", ClientIp, workspaceId: WorkspaceId, ct: ct);

        return BackTo(returnUrl, IsFa
            ? "باکت جدا شد. تا استقرار بعدی، کانتینر در حال اجرا هنوز متغیرهای آن را دارد."
            : "Detached. Until the next deploy, the running container still has its variables.");
    }

    /// <summary>
    /// "2 apps: api, worker" — or past three, "5 apps: api, worker, cron and 2 more". The
    /// <c>ProjectsController.Delete</c> refusal idiom, reused exactly as
    /// <c>ConfigGroupsController</c> already reused it.
    /// </summary>
    private string NamedList(IReadOnlyList<string> names)
    {
        const int shown = 3;
        var listed = names.Count > shown
            ? string.Join(IsFa ? "، " : ", ", names.Take(shown)) +
              (IsFa ? $" و {names.Count - shown} مورد دیگر" : $" and {names.Count - shown} more")
            : string.Join(IsFa ? "، " : ", ", names);

        return IsFa ? $"{names.Count} اپ: {listed}" : $"{names.Count} app{(names.Count == 1 ? "" : "s")}: {listed}";
    }

    private IActionResult BackTo(string? returnUrl, string? message, bool error = false)
    {
        TempData[error ? "Error" : "Message"] = message;
        return string.IsNullOrWhiteSpace(returnUrl) ? RedirectToAction(nameof(Index)) : LocalRedirect(returnUrl);
    }

    private string? Unprotect(string value)
    {
        try { return protector.Unprotect(value); }
        catch { return null; }
    }

    private string Explain(BucketNameRefusal refusal) => (refusal, IsFa) switch
    {
        (BucketNameRefusal.Missing, true) => "نام باکت را بنویسید.",
        (BucketNameRefusal.Missing, false) => "Type a bucket name.",
        (BucketNameRefusal.TooShort, true) => $"نام باکت دست‌کم {BucketName.MinLength} نویسه است.",
        (BucketNameRefusal.TooShort, false) => $"A bucket name is at least {BucketName.MinLength} characters.",
        (BucketNameRefusal.TooLong, true) => $"نام باکت حداکثر {BucketName.MaxLength} نویسه است.",
        (BucketNameRefusal.TooLong, false) => $"A bucket name is at most {BucketName.MaxLength} characters.",
        (BucketNameRefusal.BadCharacters, true) =>
            "فقط حروف کوچک انگلیسی، رقم و خط تیره. نقطه هم نه — باکتی که نقطه دارد روی TLS قابل دسترسی نیست.",
        (BucketNameRefusal.BadCharacters, false) =>
            "Lowercase letters, digits and hyphens only. Not periods either: a bucket with one cannot be reached over TLS.",
        (BucketNameRefusal.BadEnds, true) => "نام باید با حرف یا رقم شروع و تمام شود.",
        (BucketNameRefusal.BadEnds, false) => "The name must start and end with a letter or a digit.",
        (BucketNameRefusal.LooksLikeAnAddress, true) => "نامی که شبیه آدرس IP است قابل استفاده نیست.",
        (BucketNameRefusal.LooksLikeAnAddress, false) => "A name shaped like an IP address cannot be used.",
        (_, true) => "این پسوند رزرو شده است.",
        (_, false) => "That suffix is reserved."
    };

    private IActionResult Back(string? message, bool error = false)
    {
        TempData[error ? "Error" : "Message"] = message;
        return RedirectToAction(nameof(Index));
    }
}
