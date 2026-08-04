using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Settings;
using Harbora.Domain.Templates;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Reviewing the versions of the ready-made apps: what is offered, what is a draft, and what the
/// registry has turned up since anyone last looked.
///
/// This page is what makes discovery worth having. A job that writes draft rows nobody can see is a
/// job that appears to work and changes nothing — the exact failure this codebase keeps finding.
/// </summary>
[Authorize(Policy = Capabilities.PlatformManage)]
[Route("admin/templates")]
public sealed class TemplateVersionsController(
    HarboraDbContext db,
    IAuditLogger audit,
    Harbora.Infrastructure.Templates.RegistryDiscoveryService discovery) : Controller
{
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
    private bool IsFa => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Template versions";
        return View(await BuildAsync(ct));
    }

    [HttpPost("versions/{id:guid}/publish")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish(Guid id, CancellationToken ct)
    {
        var version = await db.AppTemplateVersions.FirstOrDefaultAsync(v => v.Id == id, ct);
        if (version is null) return NotFound();

        // Refused rather than published as something nobody can deploy. A published version without
        // a digest is an option on the deploy form that fails every time it is chosen.
        if (string.IsNullOrWhiteSpace(version.ImageDigest))
        {
            TempData["Error"] = IsFa
                ? "این نسخه digest ندارد و قابل انتشار نیست."
                : "That version has no image digest and cannot be published.";
            return RedirectToAction(nameof(Index));
        }

        version.Publication = VersionPublication.Published;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("template.version_published", "template_version", version.Version, ClientIp, ct: ct);

        TempData["Message"] = IsFa ? $"نسخهٔ {version.Version} منتشر شد." : $"{version.Version} was published.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("versions/{id:guid}/withdraw")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(Guid id, CancellationToken ct)
    {
        var version = await db.AppTemplateVersions.FirstOrDefaultAsync(v => v.Id == id, ct);
        if (version is null) return NotFound();

        version.Publication = VersionPublication.Draft;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("template.version_withdrawn", "template_version", version.Version, ClientIp, ct: ct);

        // Deliberately says what withdrawing does not do. Apps already running this version keep
        // running it; the change is only about what is offered next.
        TempData["Message"] = IsFa
            ? $"نسخهٔ {version.Version} از فهرست برداشته شد. برنامه‌هایی که روی آن هستند تغییری نمی‌کنند."
            : $"{version.Version} is no longer offered. Apps already on it keep running.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("versions/{id:guid}/lifecycle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetLifecycle(Guid id, VersionLifecycle lifecycle, CancellationToken ct)
    {
        var version = await db.AppTemplateVersions.FirstOrDefaultAsync(v => v.Id == id, ct);
        if (version is null) return NotFound();

        if (lifecycle == VersionLifecycle.Recommended)
        {
            // Exactly one recommendation per template. Two means a deploy form with two default
            // selections and no rule for choosing between them.
            var siblings = await db.AppTemplateVersions
                .Where(v => v.AppTemplateId == version.AppTemplateId
                            && v.Id != version.Id
                            && v.Lifecycle == VersionLifecycle.Recommended)
                .ToListAsync(ct);

            foreach (var sibling in siblings) sibling.Lifecycle = VersionLifecycle.Stable;
        }

        version.Lifecycle = lifecycle;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("template.version_lifecycle", "template_version",
            $"{version.Version}={lifecycle}", ClientIp, ct: ct);

        TempData["Message"] = IsFa ? "وضعیت نسخه به‌روز شد." : "The version's lifecycle was updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("discovery")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetDiscovery(bool enabled, CancellationToken ct)
    {
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == SettingKeys.RegistryDiscoveryEnabled, ct);
        if (setting is null)
        {
            setting = new Setting { Key = SettingKeys.RegistryDiscoveryEnabled };
            db.Settings.Add(setting);
        }

        setting.Value = enabled ? "true" : "false";
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("template.discovery_setting", "setting", setting.Value, ClientIp, ct: ct);

        TempData["Message"] = enabled
            ? (IsFa ? "بررسی رجیستری روشن شد." : "Registry checks are on.")
            : (IsFa ? "بررسی رجیستری خاموش شد." : "Registry checks are off.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Runs a check now, so an operator does not have to wait a day to see whether it works.</summary>
    [HttpPost("discovery/run")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RunDiscovery(CancellationToken ct)
    {
        var added = await discovery.DiscoverAsync(ct);

        // Says zero as zero. "Checked, nothing new" and "checked nothing because the feature is off"
        // are different answers, and the second is the one an operator needs to hear.
        var enabled = await db.Settings
            .Where(s => s.Key == SettingKeys.RegistryDiscoveryEnabled)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);

        if (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
            TempData["Error"] = IsFa
                ? "بررسی رجیستری خاموش است، پس هیچ درخواستی فرستاده نشد."
                : "Registry checks are off, so nothing was requested.";
        else
            TempData["Message"] = added == 0
                ? (IsFa ? "بررسی انجام شد؛ نسخهٔ تازه‌ای پیدا نشد." : "Checked. Nothing new was found.")
                : (IsFa ? $"{added} نسخهٔ پیش‌نویس اضافه شد." : $"{added} draft version(s) were added.");

        return RedirectToAction(nameof(Index));
    }

    private async Task<TemplateVersionAdminViewModel> BuildAsync(CancellationToken ct)
    {
        var templates = await db.AppTemplates.Where(t => t.IsBuiltIn)
            .OrderBy(t => t.Name).ToListAsync(ct);

        var versions = await db.AppTemplateVersions.ToListAsync(ct);

        var discoveryEnabled = await db.Settings
            .Where(s => s.Key == SettingKeys.RegistryDiscoveryEnabled)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);

        return new TemplateVersionAdminViewModel
        {
            DiscoveryEnabled = string.Equals(discoveryEnabled, "true", StringComparison.OrdinalIgnoreCase),
            Templates = templates.Select(t => new TemplateVersionGroupViewModel
            {
                Template = t,

                // Drafts first: they are the ones needing a decision, and a page that buries them
                // under twenty published rows is a page where they are never seen.
                Versions = versions
                    .Where(v => v.AppTemplateId == t.Id)
                    .OrderBy(v => v.Publication == VersionPublication.Published)
                    .ThenBy(v => v.Lifecycle)
                    .ThenByDescending(v => v.DiscoveredAt ?? v.CreatedAt)
                    .ToList()
            }).ToList()
        };
    }
}
