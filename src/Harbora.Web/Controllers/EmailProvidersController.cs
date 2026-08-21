using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Email;
using Harbora.Infrastructure.Email;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// BYO SMTP providers: a workspace's own credentials for somebody else's mail server, attached to
/// apps as <c>SMTP_*</c> env vars (F6, 2026-08-21 functions-and-services plan — HARBORA-0038 phase
/// 1). Mirrors <see cref="StorageController"/>'s bucket-attach shape exactly (F5).
///
/// Not <c>MailController</c> — that is a separate, already-shipped feature (Harbora hosting
/// mailboxes on its own Stalwart server, at <c>/mail</c>). Nothing here talks to that server; a row
/// created by this controller is a credential for an app to send through, never something Harbora
/// relays or terminates.
/// </summary>
[Authorize]
[Route("email-providers")]
public sealed class EmailProvidersController(
    HarboraDbContext db,
    ISecretProtector protector,
    EmailProviderMailer mailer,
    IAuditLogger audit,
    ICurrentUser currentUser,
    Harbora.Infrastructure.Security.ProjectAccessService access) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
    private bool IsFa => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    [HttpGet("")]
    public async Task<IActionResult> Index(Guid? reveal, CancellationToken ct)
    {
        ViewData["Title"] = "Email providers";

        var providers = await db.EmailProviders.AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

        var providerIds = providers.Select(p => p.Id).ToList();
        var attachments = providerIds.Count == 0
            ? []
            : await db.AppEmailProviders.AsNoTracking()
                .Where(ep => providerIds.Contains(ep.EmailProviderId))
                .Select(ep => new { ep.EmailProviderId, ep.AppId, ep.HasUnpublishedChanges, AppName = ep.App!.Name })
                .ToListAsync(ct);

        var apps = await db.Apps.AsNoTracking()
            .Where(a => a.WorkspaceId == WorkspaceId)
            .OrderBy(a => a.Name)
            .Select(a => new { a.Id, a.Name })
            .ToListAsync(ct);

        return View(new EmailProvidersPageViewModel
        {
            Providers = providers.Select(p =>
            {
                var attachedAppIds = attachments.Where(a => a.EmailProviderId == p.Id)
                    .Select(a => a.AppId).ToHashSet();
                return new EmailProviderViewModel(
                    p.Id, p.Name, p.Host, p.Port, p.Username,
                    // Revealed only for the one provider asked for, and only on an explicit click —
                    // the same rule StorageController.Index applies to a bucket's secret key.
                    reveal == p.Id ? Unprotect(p.EncryptedPassword) : null,
                    p.FromAddress, p.FromName, p.UseSsl,
                    p.LastTestedAt, p.LastTestSucceeded, p.LastTestMessage,
                    attachments.Where(a => a.EmailProviderId == p.Id)
                        .Select(a => new EmailProviderAttachedAppViewModel(a.AppId, a.AppName, a.HasUnpublishedChanges)).ToList(),
                    apps.Where(a => !attachedAppIds.Contains(a.Id))
                        .Select(a => new EmailProviderAttachableAppViewModel(a.Id, a.Name)).ToList());
            }).ToList(),
            RevealedProviderId = reveal
        });
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Create(
        string name, string host, int port, string? username, string? password,
        string fromAddress, string? fromName, bool useSsl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(fromAddress))
            return Back(IsFa
                ? "نام، میزبان و آدرس فرستنده لازم است."
                : "Name, host and a From address are required.", error: true);

        if (port is < 1 or > 65535)
            return Back(IsFa ? "پورت باید بین ۱ و ۶۵۵۳۵ باشد." : "Port must be between 1 and 65535.", error: true);

        db.EmailProviders.Add(new EmailProvider
        {
            WorkspaceId = WorkspaceId,
            Name = name.Trim(),
            Host = host.Trim(),
            Port = port,
            Username = username?.Trim() ?? "",
            EncryptedPassword = protector.Protect(password ?? ""),
            FromAddress = fromAddress.Trim(),
            FromName = string.IsNullOrWhiteSpace(fromName) ? null : fromName.Trim(),
            UseSsl = useSsl
        });
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("email_provider.created", "email_provider", name, ClientIp, ct: ct);

        return Back(IsFa ? $"ارائه‌دهنده «{name}» ساخته شد." : $"Email provider '{name}' was created.");
    }

    /// <summary>Replaces every field, including the password when one is given — an empty password
    /// field leaves the stored credential untouched, the same "blank means unchanged" rule a
    /// password-change form uses everywhere else in this codebase.</summary>
    [HttpPost("{id:guid}/update")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Update(
        Guid id, string name, string host, int port, string? username, string? password,
        string fromAddress, string? fromName, bool useSsl, CancellationToken ct)
    {
        var provider = await db.EmailProviders.FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == WorkspaceId, ct);
        if (provider is null) return NotFound();

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(fromAddress))
            return Back(IsFa
                ? "نام، میزبان و آدرس فرستنده لازم است."
                : "Name, host and a From address are required.", error: true);

        if (port is < 1 or > 65535)
            return Back(IsFa ? "پورت باید بین ۱ و ۶۵۵۳۵ باشد." : "Port must be between 1 and 65535.", error: true);

        provider.Name = name.Trim();
        provider.Host = host.Trim();
        provider.Port = port;
        provider.Username = username?.Trim() ?? "";
        if (!string.IsNullOrEmpty(password)) provider.EncryptedPassword = protector.Protect(password);
        provider.FromAddress = fromAddress.Trim();
        provider.FromName = string.IsNullOrWhiteSpace(fromName) ? null : fromName.Trim();
        provider.UseSsl = useSsl;

        // Every app carrying this provider has to pick the change up on its own next deploy — the
        // same reason StorageController rotating a bucket's key leaves HasUnpublishedChanges set.
        var attachments = await db.AppEmailProviders.Where(ep => ep.EmailProviderId == id).ToListAsync(ct);
        foreach (var a in attachments) a.HasUnpublishedChanges = true;

        await db.SaveChangesAsync(ct);
        await audit.LogAsync("email_provider.updated", "email_provider", id.ToString(), ClientIp, ct: ct);

        return Back(IsFa
            ? $"ارائه‌دهنده «{provider.Name}» به‌روزرسانی شد. اپ‌های متصل با استقرار بعدی آن را دریافت می‌کنند."
            : $"Email provider '{provider.Name}' was updated. Attached apps pick it up on their next deploy.");
    }

    [HttpPost("{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var provider = await db.EmailProviders.FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == WorkspaceId, ct);
        if (provider is null) return NotFound();

        var attachedTo = await db.AppEmailProviders.AsNoTracking()
            .Where(ep => ep.EmailProviderId == id)
            .Select(ep => ep.App!.Name)
            .ToListAsync(ct);

        if (attachedTo.Count > 0)
        {
            return Back(IsFa
                ? $"این ارائه‌دهنده هنوز به {NamedList(attachedTo)} متصل است. برای حذف، ابتدا آن را از همه‌ی اپ‌ها جدا کنید."
                : $"This email provider is still attached to {NamedList(attachedTo)}. Detach it from every app first, then delete it.",
                error: true);
        }

        db.EmailProviders.Remove(provider);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("email_provider.deleted", "email_provider", provider.Name, ClientIp, ct: ct);

        return Back(IsFa ? "ارائه‌دهنده حذف شد." : "The email provider was deleted.");
    }

    /// <summary>Attaches a provider to an app at the back of its precedence order — the same
    /// <c>StorageController.AttachBucket</c> shape (F5): current max <c>AttachOrder</c> + 1, never
    /// reused, and starts <c>HasUnpublishedChanges</c> true because nothing here is live until the
    /// app's own next deploy assembles its environment.</summary>
    [HttpPost("{id:guid}/attach")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Attach(Guid id, Guid appId, string? returnUrl, CancellationToken ct)
    {
        var provider = await db.EmailProviders.FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == WorkspaceId, ct);
        if (provider is null) return NotFound();

        if (!await access.CanTouchAppAsync(appId, Capabilities.AppsEnv, ct)) return NotFound();
        var appExists = await db.Apps.AsNoTracking().AnyAsync(a => a.Id == appId && a.WorkspaceId == WorkspaceId, ct);
        if (!appExists) return NotFound();

        if (await db.AppEmailProviders.AnyAsync(ep => ep.AppId == appId && ep.EmailProviderId == id, ct))
            return BackTo(returnUrl, IsFa ? "این ارائه‌دهنده از قبل به این اپ متصل است." : "This email provider is already attached.", error: true);

        var maxOrder = await db.AppEmailProviders
            .Where(ep => ep.AppId == appId)
            .Select(ep => (int?)ep.AttachOrder)
            .MaxAsync(ct) ?? 0;

        db.AppEmailProviders.Add(new AppEmailProvider
        {
            AppId = appId, EmailProviderId = id, AttachOrder = maxOrder + 1, HasUnpublishedChanges = true
        });
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("email_provider.attached", "email_provider", $"{id}:{appId}", ClientIp, ct: ct);

        return BackTo(returnUrl, IsFa
            ? $"ارائه‌دهنده «{provider.Name}» متصل شد. متغیرهایش با استقرار بعدی این اپ اعمال می‌شوند."
            : $"Attached '{provider.Name}'. Its variables apply on this app's next deploy.");
    }

    /// <summary>Removes the join row. The running container keeps the variables until the app's own
    /// next deploy — same as detaching a bucket (F5).</summary>
    [HttpPost("{id:guid}/detach")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Detach(Guid id, Guid appId, string? returnUrl, CancellationToken ct)
    {
        var provider = await db.EmailProviders.FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == WorkspaceId, ct);
        if (provider is null) return NotFound();

        if (!await access.CanTouchAppAsync(appId, Capabilities.AppsEnv, ct)) return NotFound();

        var join = await db.AppEmailProviders.FirstOrDefaultAsync(ep => ep.AppId == appId && ep.EmailProviderId == id, ct);
        if (join is null) return NotFound();

        db.AppEmailProviders.Remove(join);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("email_provider.detached", "email_provider", $"{id}:{appId}", ClientIp, ct: ct);

        return BackTo(returnUrl, IsFa
            ? "ارائه‌دهنده جدا شد. تا استقرار بعدی، کانتینر در حال اجرا هنوز متغیرهای آن را دارد."
            : "Detached. Until the next deploy, the running container still has its variables.");
    }

    /// <summary>
    /// The honesty requirement this whole sub-project turns on: reports the provider's real answer,
    /// never "sent" for a refusal (the <c>AdminSettingsController.TestSmtp</c> idiom, reused). Sends
    /// to the signed-in caller's own address, through <see cref="EmailProviderMailer"/> — the exact
    /// path an attached app's <c>SMTP_*</c> env would resolve to, so a green test here means what it
    /// says.
    /// </summary>
    [HttpPost("{id:guid}/test")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> TestSend(Guid id, CancellationToken ct)
    {
        var provider = await db.EmailProviders.FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == WorkspaceId, ct);
        if (provider is null) return NotFound();

        var to = currentUser.Email;
        if (string.IsNullOrWhiteSpace(to))
            return Back(IsFa ? "ایمیل حساب شما معلوم نیست." : "Your account has no email to send to.", error: true);

        try
        {
            await mailer.SendTestAsync(provider, to, ct);
            provider.LastTestedAt = DateTimeOffset.UtcNow;
            provider.LastTestSucceeded = true;
            provider.LastTestMessage = null;
            await db.SaveChangesAsync(ct);
            return Back(IsFa ? $"ایمیل آزمایشی به {to} رفت." : $"A test email went to {to}.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The server's own words, because "failed" alone sends somebody digging through logs for
            // what this line already knew — and because a Test button that reports success regardless
            // is exactly the defect class this codebase has spent weeks removing.
            provider.LastTestedAt = DateTimeOffset.UtcNow;
            provider.LastTestSucceeded = false;
            provider.LastTestMessage = e.Message;
            await db.SaveChangesAsync(ct);
            return Back((IsFa ? "ارسال نشد: " : "Could not send: ") + e.Message, error: true);
        }
    }

    /// <summary>"2 apps: api, worker" — the <c>StorageController</c> refusal idiom (F5), reused.</summary>
    private string NamedList(IReadOnlyList<string> names)
    {
        const int shown = 3;
        var listed = names.Count > shown
            ? string.Join(IsFa ? "، " : ", ", names.Take(shown)) +
              (IsFa ? $" و {names.Count - shown} مورد دیگر" : $" and {names.Count - shown} more")
            : string.Join(IsFa ? "، " : ", ", names);

        return IsFa ? $"{names.Count} اپ: {listed}" : $"{names.Count} app{(names.Count == 1 ? "" : "s")}: {listed}";
    }

    private string? Unprotect(string value)
    {
        try { return protector.Unprotect(value); }
        catch { return null; }
    }

    private IActionResult Back(string? message, bool error = false)
    {
        TempData[error ? "Error" : "Message"] = message;
        return RedirectToAction(nameof(Index));
    }

    private IActionResult BackTo(string? returnUrl, string? message, bool error = false)
    {
        TempData[error ? "Error" : "Message"] = message;
        return string.IsNullOrWhiteSpace(returnUrl) ? RedirectToAction(nameof(Index)) : LocalRedirect(returnUrl);
    }
}
