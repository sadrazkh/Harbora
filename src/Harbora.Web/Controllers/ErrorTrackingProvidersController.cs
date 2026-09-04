using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.ErrorTracking;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// BYO Sentry/GlitchTip DSNs: a workspace's own error-tracking endpoint, attached to apps as a
/// <c>SENTRY_DSN</c> env var (1.8, 2026-09 market-gaps round two). Mirrors
/// <see cref="EmailProvidersController"/>'s attach shape exactly (F6), which itself mirrors
/// <see cref="StorageController"/>'s bucket-attach shape (F5).
///
/// Not a database rebuild/start/stop lifecycle of its own: the endpoint this DSN points at — a
/// GlitchTip instance deployed from the "sentry" one-click template, or a project on Sentry SaaS —
/// already has its own lifecycle (an ordinary <c>App</c>, plus the <c>ManagedService</c> Postgres/
/// Redis a self-hosted GlitchTip declares as <c>requires</c>, each with generated credentials and
/// billing through <c>TemplateDeploymentService</c> already). A row created by this controller is a
/// credential for an app to report through, never something Harbora provisions or proxies.
/// </summary>
[Authorize]
[Route("error-tracking")]
public sealed class ErrorTrackingProvidersController(
    HarboraDbContext db,
    ISecretProtector protector,
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
        ViewData["Title"] = "Error tracking";

        var providers = await db.ErrorTrackingProviders.AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

        var providerIds = providers.Select(p => p.Id).ToList();
        var attachments = providerIds.Count == 0
            ? []
            : await db.AppErrorTrackingProviders.AsNoTracking()
                .Where(et => providerIds.Contains(et.ErrorTrackingProviderId))
                .Select(et => new { et.ErrorTrackingProviderId, et.AppId, et.HasUnpublishedChanges, AppName = et.App!.Name })
                .ToListAsync(ct);

        var apps = await db.Apps.AsNoTracking()
            .Where(a => a.WorkspaceId == WorkspaceId)
            .OrderBy(a => a.Name)
            .Select(a => new { a.Id, a.Name })
            .ToListAsync(ct);

        return View(new ErrorTrackingProvidersPageViewModel
        {
            Providers = providers.Select(p =>
            {
                var attachedAppIds = attachments.Where(a => a.ErrorTrackingProviderId == p.Id)
                    .Select(a => a.AppId).ToHashSet();
                return new ErrorTrackingProviderViewModel(
                    p.Id, p.Name,
                    // Revealed only for the one provider asked for, and only on an explicit click —
                    // the same rule StorageController.Index applies to a bucket's secret key.
                    reveal == p.Id ? Unprotect(p.EncryptedDsn) : null,
                    attachments.Where(a => a.ErrorTrackingProviderId == p.Id)
                        .Select(a => new ErrorTrackingProviderAttachedAppViewModel(a.AppId, a.AppName, a.HasUnpublishedChanges)).ToList(),
                    apps.Where(a => !attachedAppIds.Contains(a.Id))
                        .Select(a => new ErrorTrackingProviderAttachableAppViewModel(a.Id, a.Name)).ToList());
            }).ToList(),
            RevealedProviderId = reveal
        });
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Create(string name, string dsn, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(dsn))
            return Back(IsFa ? "نام و DSN لازم است." : "Name and a DSN are required.", error: true);

        if (!Uri.TryCreate(dsn.Trim(), UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.UserInfo))
            return Back(IsFa
                ? "این یک DSN معتبر به‌نظر نمی‌رسد — شکل آن باید https://کلید@میزبان/شناسهٔ‌پروژه باشد."
                : "That does not look like a valid DSN — it should look like https://key@host/project-id.", error: true);

        db.ErrorTrackingProviders.Add(new ErrorTrackingProvider
        {
            WorkspaceId = WorkspaceId,
            Name = name.Trim(),
            EncryptedDsn = protector.Protect(dsn.Trim())
        });
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("error_tracking_provider.created", "error_tracking_provider", name, ClientIp, workspaceId: WorkspaceId, ct: ct);

        return Back(IsFa ? $"ارائه‌دهنده «{name}» ساخته شد." : $"Error-tracking provider '{name}' was created.");
    }

    /// <summary>Replaces every field, including the DSN when one is given — an empty DSN field leaves
    /// the stored credential untouched, the same "blank means unchanged" rule a password-change form
    /// uses everywhere else in this codebase.</summary>
    [HttpPost("{id:guid}/update")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Update(Guid id, string name, string? dsn, CancellationToken ct)
    {
        var provider = await db.ErrorTrackingProviders.FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == WorkspaceId, ct);
        if (provider is null) return NotFound();

        if (string.IsNullOrWhiteSpace(name))
            return Back(IsFa ? "نام لازم است." : "Name is required.", error: true);

        if (!string.IsNullOrWhiteSpace(dsn))
        {
            if (!Uri.TryCreate(dsn.Trim(), UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.UserInfo))
                return Back(IsFa
                    ? "این یک DSN معتبر به‌نظر نمی‌رسد — شکل آن باید https://کلید@میزبان/شناسهٔ‌پروژه باشد."
                    : "That does not look like a valid DSN — it should look like https://key@host/project-id.", error: true);
            provider.EncryptedDsn = protector.Protect(dsn.Trim());
        }

        provider.Name = name.Trim();

        // Every app carrying this provider has to pick the change up on its own next deploy — the
        // same reason EmailProvidersController rotating a provider's password leaves
        // HasUnpublishedChanges set.
        var attachments = await db.AppErrorTrackingProviders.Where(et => et.ErrorTrackingProviderId == id).ToListAsync(ct);
        foreach (var a in attachments) a.HasUnpublishedChanges = true;

        await db.SaveChangesAsync(ct);
        await audit.LogAsync("error_tracking_provider.updated", "error_tracking_provider", id.ToString(), ClientIp, workspaceId: WorkspaceId, ct: ct);

        return Back(IsFa
            ? $"ارائه‌دهنده «{provider.Name}» به‌روزرسانی شد. اپ‌های متصل با استقرار بعدی آن را دریافت می‌کنند."
            : $"Error-tracking provider '{provider.Name}' was updated. Attached apps pick it up on their next deploy.");
    }

    [HttpPost("{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var provider = await db.ErrorTrackingProviders.FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == WorkspaceId, ct);
        if (provider is null) return NotFound();

        var attachedTo = await db.AppErrorTrackingProviders.AsNoTracking()
            .Where(et => et.ErrorTrackingProviderId == id)
            .Select(et => et.App!.Name)
            .ToListAsync(ct);

        if (attachedTo.Count > 0)
        {
            return Back(IsFa
                ? $"این ارائه‌دهنده هنوز به {NamedList(attachedTo)} متصل است. برای حذف، ابتدا آن را از همه‌ی اپ‌ها جدا کنید."
                : $"This error-tracking provider is still attached to {NamedList(attachedTo)}. Detach it from every app first, then delete it.",
                error: true);
        }

        db.ErrorTrackingProviders.Remove(provider);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("error_tracking_provider.deleted", "error_tracking_provider", provider.Name, ClientIp, workspaceId: WorkspaceId, ct: ct);

        return Back(IsFa ? "ارائه‌دهنده حذف شد." : "The error-tracking provider was deleted.");
    }

    /// <summary>Attaches a provider to an app at the back of its precedence order — the same
    /// <c>EmailProvidersController.Attach</c> shape (F6): current max <c>AttachOrder</c> + 1, never
    /// reused, and starts <c>HasUnpublishedChanges</c> true because nothing here is live until the
    /// app's own next deploy assembles its environment.</summary>
    [HttpPost("{id:guid}/attach")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Attach(Guid id, Guid appId, string? returnUrl, CancellationToken ct)
    {
        var provider = await db.ErrorTrackingProviders.FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == WorkspaceId, ct);
        if (provider is null) return NotFound();

        if (!await access.CanTouchAppAsync(appId, Capabilities.AppsEnv, ct)) return NotFound();
        var appExists = await db.Apps.AsNoTracking().AnyAsync(a => a.Id == appId && a.WorkspaceId == WorkspaceId, ct);
        if (!appExists) return NotFound();

        if (await db.AppErrorTrackingProviders.AnyAsync(et => et.AppId == appId && et.ErrorTrackingProviderId == id, ct))
            return BackTo(returnUrl, IsFa ? "این ارائه‌دهنده از قبل به این اپ متصل است." : "This error-tracking provider is already attached.", error: true);

        var maxOrder = await db.AppErrorTrackingProviders
            .Where(et => et.AppId == appId)
            .Select(et => (int?)et.AttachOrder)
            .MaxAsync(ct) ?? 0;

        db.AppErrorTrackingProviders.Add(new AppErrorTrackingProvider
        {
            AppId = appId, ErrorTrackingProviderId = id, AttachOrder = maxOrder + 1, HasUnpublishedChanges = true
        });
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("error_tracking_provider.attached", "error_tracking_provider", $"{id}:{appId}", ClientIp, workspaceId: WorkspaceId, ct: ct);

        return BackTo(returnUrl, IsFa
            ? $"ارائه‌دهنده «{provider.Name}» متصل شد. متغیرش با استقرار بعدی این اپ اعمال می‌شود."
            : $"Attached '{provider.Name}'. Its variable applies on this app's next deploy.");
    }

    /// <summary>Removes the join row. The running container keeps the variable until the app's own
    /// next deploy — same as detaching an email provider (F6).</summary>
    [HttpPost("{id:guid}/detach")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Detach(Guid id, Guid appId, string? returnUrl, CancellationToken ct)
    {
        var provider = await db.ErrorTrackingProviders.FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == WorkspaceId, ct);
        if (provider is null) return NotFound();

        if (!await access.CanTouchAppAsync(appId, Capabilities.AppsEnv, ct)) return NotFound();

        var join = await db.AppErrorTrackingProviders.FirstOrDefaultAsync(et => et.AppId == appId && et.ErrorTrackingProviderId == id, ct);
        if (join is null) return NotFound();

        db.AppErrorTrackingProviders.Remove(join);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("error_tracking_provider.detached", "error_tracking_provider", $"{id}:{appId}", ClientIp, workspaceId: WorkspaceId, ct: ct);

        return BackTo(returnUrl, IsFa
            ? "ارائه‌دهنده جدا شد. تا استقرار بعدی، کانتینر در حال اجرا هنوز متغیرش را دارد."
            : "Detached. Until the next deploy, the running container still has its variable.");
    }

    /// <summary>"2 apps: api, worker" — the <c>EmailProvidersController</c> refusal idiom (F6), reused.</summary>
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
