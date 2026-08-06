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
    ICurrentUser currentUser) : Controller
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

        await audit.LogAsync("storage.object_read", "bucket", $"{id}:{key}", ClientIp, ct: ct);

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
            await audit.LogAsync("storage.object_write", "bucket", $"{id}:{key}", ClientIp, ct: ct);

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
            await audit.LogAsync("storage.object_delete", "bucket", $"{id}:{key}", ClientIp, ct: ct);

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

        return View(new StoragePageViewModel
        {
            IsConfigured = storage.IsConfigured,
            WhatIsMissing = storage.WhatIsMissing(),
            Endpoint = storage.CustomerEndpoint,
            Plans = await db.StoragePlans.Where(p => p.IsEnabled)
                .OrderBy(p => p.SortOrder).ThenBy(p => p.MonthlyPrice).ToListAsync(ct),
            Buckets = buckets.Select(b => new StorageBucketViewModel(
                b.Id, b.Name, b.AccessKey,
                // Revealed only for the one bucket asked for, and only on an explicit click. A page
                // that prints every secret is a page nobody can screen-share.
                reveal == b.Id ? Unprotect(b.EncryptedSecretKey) : null,
                b.QuotaBytes, b.UsedBytes, b.MeasuredAt, b.Status, b.FailureReason,
                b.StoragePlan?.Name)).ToList(),
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
            var held = await db.StorageBuckets.CountAsync(ct);
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
        await audit.LogAsync("storage.bucket_created", "bucket", name, ClientIp, ct: ct);

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

    [HttpPost("buckets/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var bucket = await db.StorageBuckets.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bucket is null) return NotFound();

        var result = await storage.DeleteAsync(bucket.Name, bucket.AccessKey, ct);

        // The row stays when the server refused. Removing it would hide a bucket that still exists
        // and still holds somebody's objects, and nothing would ever list it again.
        if (!result.Ok) return Back(result.Reason, error: true);

        db.StorageBuckets.Remove(bucket);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("storage.bucket_deleted", "bucket", bucket.Name, ClientIp, ct: ct);

        return Back(IsFa ? "باکت حذف شد." : "The bucket was deleted.");
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
