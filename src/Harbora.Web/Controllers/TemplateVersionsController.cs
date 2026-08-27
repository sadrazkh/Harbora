using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Settings;
using Harbora.Domain.Common;
using Harbora.Domain.Templates;
using Harbora.Infrastructure.Services;
using Harbora.Infrastructure.Templates;
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
    IContainerRegistry registry,
    IManagedServiceEngine engine,
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

    /// <summary>
    /// Puts a version into the dropdown by hand.
    ///
    /// The list could only be published or withdrawn: what was in it came from the shipped
    /// manifests and from discovery, which follows the shape already in the catalogue and only ever
    /// looks forward. So a template that shipped with no versions had an empty dropdown for good,
    /// and an older release — the one a customer mid-upgrade needs to go back to — could not be
    /// offered at all.
    /// </summary>
    [HttpPost("{templateId:guid}/versions")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddVersion(Guid templateId, string? tag, CancellationToken ct)
    {
        var template = await db.AppTemplates.FirstOrDefaultAsync(t => t.Id == templateId, ct);
        if (template is null) return NotFound();

        var siblings = await db.AppTemplateVersions
            .Where(v => v.AppTemplateId == templateId)
            .ToListAsync(ct);

        // The same derivation the page showed next to the field. Two of them would be two answers
        // to "which repository is this tag looked up on", and the one on screen is the one somebody
        // typed against.
        var basedOn = BaseOf(siblings);
        var plan = TemplateVersionEntry.Plan(
            tag, RepositoryOf(template, siblings), siblings.Select(v => v.Version));
        if (!plan.Allowed)
        {
            TempData["Error"] = Explain(plan.Refusal);
            return RedirectToAction(nameof(Index));
        }

        // Resolved before anything is stored, exactly as discovery does. A version row without a
        // digest is an option on the deploy form that fails every time it is chosen — and this one
        // would be published, so it would be the first thing a customer saw.
        var digest = await registry.ResolveDigestAsync(plan.Repository, plan.Tag, ct);
        if (digest is null)
        {
            TempData["Error"] = IsFa
                ? $"«{plan.Tag}» روی {plan.Repository} پیدا نشد، یا رجیستری جواب نداد. چیزی اضافه نشد."
                : $"'{plan.Tag}' was not found on {plan.Repository}, or the registry did not answer. Nothing was added.";
            return RedirectToAction(nameof(Index));
        }

        db.AppTemplateVersions.Add(TemplateVersionEntry.Build(
            templateId, plan, digest, basedOn, template.ManifestJson));
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("template.version_added", "template_version",
            $"{template.Key}={plan.Tag}", ClientIp, workspaceId: null, ct: ct);

        TempData["Message"] = IsFa
            ? $"نسخهٔ {plan.Tag} اضافه و منتشر شد و از حالا در فهرست انتخاب نسخه دیده می‌شود."
            : $"{plan.Tag} was added and published; it is now in the version list.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Takes a version out of the list for good.
    ///
    /// Refused while any application is on it. Withdrawing is the reversible way to stop offering
    /// something; deleting the row an app points at leaves that app referring to a version that no
    /// longer exists, and the next thing to read it finds nothing where a pinned image should be.
    /// </summary>
    [HttpPost("versions/{id:guid}/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveVersion(Guid id, CancellationToken ct)
    {
        var version = await db.AppTemplateVersions.FirstOrDefaultAsync(v => v.Id == id, ct);
        if (version is null) return NotFound();

        // Across every workspace: this is a platform-wide catalogue, and the tenant filter would
        // count only the operator's own apps and report a version as unused because somebody else's
        // tenant is the one running it.
        var inUse = await db.Apps.IgnoreQueryFilters().CountAsync(a => a.TemplateVersionId == id, ct);
        if (inUse > 0)
        {
            TempData["Error"] = IsFa
                ? $"{inUse} برنامه روی این نسخه هستند، پس حذف نشد. برای اینکه دیگر پیشنهاد نشود «برداشتن» را بزنید."
                : $"{inUse} application(s) are on this version, so it was not deleted. Use withdraw to stop offering it.";
            return RedirectToAction(nameof(Index));
        }

        db.AppTemplateVersions.Remove(version);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("template.version_removed", "template_version", version.Version, ClientIp, workspaceId: null, ct: ct);

        TempData["Message"] = IsFa ? $"نسخهٔ {version.Version} حذف شد." : $"{version.Version} was deleted.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// The version a new one should be modelled on: the recommended one when there is one, then the
    /// best of the rest. Only versions that actually name a repository — one that does not cannot
    /// tell us where to look.
    /// </summary>
    private static AppTemplateVersion? BaseOf(IReadOnlyCollection<AppTemplateVersion> versions) =>
        versions
            .Where(v => !string.IsNullOrWhiteSpace(v.ImageRepository))
            .OrderByDescending(v => v.Lifecycle == VersionLifecycle.Recommended)
            .ThenBy(v => v.Lifecycle)
            .FirstOrDefault();

    /// <summary>
    /// Where this template's images live: whatever its versions already use, and failing that
    /// whatever its own manifest names. Null when it names nothing anywhere.
    /// </summary>
    private static string? RepositoryOf(AppTemplate template, IReadOnlyCollection<AppTemplateVersion> versions)
    {
        var fromVersion = BaseOf(versions)?.ImageRepository;
        if (!string.IsNullOrWhiteSpace(fromVersion)) return ImageReference.RepositoryOf(fromVersion);

        return TemplateManifest.TryParse(template.ManifestJson, out var manifest, out _)
            ? ImageReference.RepositoryOf(manifest?.Image)
            : null;
    }

    private string Explain(VersionEntryRefusal refusal) => (refusal, IsFa) switch
    {
        (VersionEntryRefusal.MissingTag, true) => "تگ نسخه را بنویسید.",
        (VersionEntryRefusal.MissingTag, false) => "Type a version tag.",
        (VersionEntryRefusal.InvalidTag, true) =>
            "این تگ شکل درستی ندارد. فقط حرف و رقم و _ . - مجاز است و نباید با . یا - شروع شود.",
        (VersionEntryRefusal.InvalidTag, false) =>
            "That is not a shape a registry tag can have: letters, digits, underscore, period and dash only, not starting with a period or dash.",
        (VersionEntryRefusal.UnknownRepository, true) =>
            "این قالب هیچ ایمیجی معرفی نکرده، پس معلوم نیست تگ را از کجا بپرسیم.",
        (VersionEntryRefusal.UnknownRepository, false) =>
            "This template names no image, so there is no repository to ask for that tag.",
        (_, true) => "این نسخه از قبل در فهرست هست.",
        (_, false) => "That version is already in the list."
    };

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
        await audit.LogAsync("template.version_published", "template_version", version.Version, ClientIp, workspaceId: null, ct: ct);

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
        await audit.LogAsync("template.version_withdrawn", "template_version", version.Version, ClientIp, workspaceId: null, ct: ct);

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
            $"{version.Version}={lifecycle}", ClientIp, workspaceId: null, ct: ct);

        TempData["Message"] = IsFa ? "وضعیت نسخه به‌روز شد." : "The version's lifecycle was updated.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// The versions of one database engine that customers may choose from.
    ///
    /// The shipped list is two entries written in C#, so offering PostgreSQL 17 — or keeping an
    /// older one for an application that needs it — took a release, while the ready-made apps beside
    /// them had this whole page.
    /// </summary>
    [HttpPost("services/{type}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveServiceVersions(
        ManagedServiceType type, string? versions, CancellationToken ct)
    {
        // Named rather than dropped. Half a list disappearing without a word is worse than a
        // refusal: the save reports success and the operator assumes what they typed is stored.
        var rejected = ServiceVersions.Rejected(versions);
        if (rejected.Count > 0)
        {
            TempData["Error"] = IsFa
                ? $"این‌ها شکل درستی برای تگ ندارند و چیزی ذخیره نشد: {string.Join("، ", rejected)}"
                : $"These are not shapes a tag can have, so nothing was saved: {string.Join(", ", rejected)}";
            return RedirectToAction(nameof(Index));
        }

        var stored = ServiceVersions.Format(ServiceVersions.Parse(versions));
        await WriteAsync(SettingKeys.ServiceVersions(type), stored, ct);
        await audit.LogAsync("service.versions", "setting", $"{type}={stored}", ClientIp, workspaceId: null, ct: ct);

        var shipped = engine.Catalog.FirstOrDefault(c => c.Type == type)?.Versions ?? [];
        TempData["Message"] = stored.Length == 0
            ? (IsFa
                ? $"فهرست {type} خالی شد، پس همان نسخه‌های پیش‌فرض ارائه می‌شوند: {string.Join("، ", shipped)}"
                : $"The {type} list was cleared, so the shipped versions are offered again: {string.Join(", ", shipped)}")
            : (IsFa
                ? $"نسخه‌های {type} ذخیره شد. اولی پیش‌فرض ساخت است."
                : $"{type} versions saved. The first one is what a new database gets by default.");

        return RedirectToAction(nameof(Index));
    }

    private async Task WriteAsync(string key, string value, CancellationToken ct)
    {
        var setting = await db.Settings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null)
        {
            setting = new Setting { Key = key };
            db.Settings.Add(setting);
        }

        setting.Value = value;
        await db.SaveChangesAsync(ct);
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
        await audit.LogAsync("template.discovery_setting", "setting", setting.Value, ClientIp, workspaceId: null, ct: ct);

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
        var serviceGroups = new List<ServiceVersionGroupViewModel>();
        foreach (var entry in engine.Catalog)
        {
            var stored = await db.Settings.IgnoreQueryFilters()
                .Where(s => s.Key == SettingKeys.ServiceVersions(entry.Type))
                .Select(s => s.Value).FirstOrDefaultAsync(ct);

            serviceGroups.Add(new ServiceVersionGroupViewModel(
                entry.Type,
                IsFa ? entry.DisplayNameFa : entry.DisplayName,
                ImageReference.RepositoryOf(entry.DefaultImage) ?? entry.DefaultImage,
                entry.Versions,
                ServiceVersions.Parse(stored)));
        }

        var templates = await db.AppTemplates.Where(t => t.IsBuiltIn)
            .OrderBy(t => t.Name).ToListAsync(ct);

        var versions = await db.AppTemplateVersions.ToListAsync(ct);

        var discoveryEnabled = await db.Settings
            .Where(s => s.Key == SettingKeys.RegistryDiscoveryEnabled)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);

        return new TemplateVersionAdminViewModel
        {
            Services = serviceGroups,
            DiscoveryEnabled = string.Equals(discoveryEnabled, "true", StringComparison.OrdinalIgnoreCase),
            Templates = templates.Select(t => new TemplateVersionGroupViewModel
            {
                Template = t,
                Repository = RepositoryOf(t, versions.Where(v => v.AppTemplateId == t.Id).ToList()),

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
