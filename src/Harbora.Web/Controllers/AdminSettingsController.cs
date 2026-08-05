using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Identity;
using Harbora.Domain.Settings;
using Harbora.Infrastructure.Templates;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// The settings only an operator sets.
///
/// They were scattered: which ready apps a person sees first was not a setting at all — the
/// dashboard took the first eight alphabetically — the default panel mode existed as a key nothing
/// wrote, and registry discovery lived on another page entirely. Each is a decision about how the
/// platform behaves for everybody, which is a different kind of thing from the preferences on
/// /settings, and it belongs somewhere an operator can find all of it at once.
/// </summary>
[Authorize(Policy = Capabilities.PlatformManage)]
[Route("admin/settings")]
public sealed class AdminSettingsController(
    HarboraDbContext db,
    IAuditLogger audit) : Controller
{
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
    private bool IsFa => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Platform settings";
        return View(await BuildAsync(ct));
    }

    [HttpPost("featured")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveFeatured(string[]? keys, CancellationToken ct)
    {
        // The order the form posts is the order they appear. A set with no order would put the
        // operator back where they started, with the alphabet deciding.
        await WriteAsync(SettingKeys.FeaturedTemplates, FeaturedTemplates.Format(keys ?? []), ct);
        await audit.LogAsync("platform.featured_templates", "setting", null, ClientIp, ct: ct);

        TempData["Message"] = IsFa ? "اپ‌های منتخب ذخیره شد." : "Featured applications saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("panel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePanel(string? defaultMode, string? defaultCulture, CancellationToken ct)
    {
        // Only what it understands. An unrecognised value is cleared rather than stored, or the
        // setting silently stops applying and the default it names never happens.
        var mode = Enum.TryParse<PanelMode>(defaultMode, ignoreCase: true, out var parsed)
            ? parsed.ToString()
            : string.Empty;

        var culture = defaultCulture is "fa" or "en" ? defaultCulture : string.Empty;

        await WriteAsync(Harbora.Web.Infrastructure.PanelModeProvider.DefaultModeSettingKey, mode, ct);
        await WriteAsync(SettingKeys.DefaultCulture, culture, ct);
        await audit.LogAsync("platform.panel_defaults", "setting", $"{mode}/{culture}", ClientIp, ct: ct);

        TempData["Message"] = IsFa ? "پیش‌فرض‌های پنل ذخیره شد." : "Panel defaults saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("resources")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveResources(string? defaultSize, bool previewsDefault, CancellationToken ct)
    {
        // Only a size that exists. Storing a key that was later withdrawn would leave every create
        // form preselecting nothing, silently, with the setting still reading as though it applied.
        var size = string.IsNullOrWhiteSpace(defaultSize)
            ? string.Empty
            : await db.InstanceSizes.Where(s => s.Key == defaultSize && s.IsEnabled)
                .Select(s => s.Key).FirstOrDefaultAsync(ct) ?? string.Empty;

        await WriteAsync(SettingKeys.DefaultInstanceSize, size, ct);
        await WriteAsync(SettingKeys.PreviewsDefault, previewsDefault ? "true" : "false", ct);
        await audit.LogAsync("platform.resource_defaults", "setting", size, ClientIp, ct: ct);

        TempData["Message"] = IsFa ? "پیش‌فرض‌های منابع ذخیره شد." : "Resource defaults saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("platform")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePlatform(string? platformName, CancellationToken ct)
    {
        await WriteAsync(SettingKeys.PlatformName, (platformName ?? string.Empty).Trim(), ct);
        await audit.LogAsync("platform.name", "setting", platformName, ClientIp, ct: ct);

        TempData["Message"] = IsFa ? "نام پلتفرم ذخیره شد." : "Platform name saved.";
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

    private async Task<string?> ReadAsync(string key, CancellationToken ct) =>
        await db.Settings.IgnoreQueryFilters().Where(s => s.Key == key)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);

    private async Task<AdminSettingsViewModel> BuildAsync(CancellationToken ct)
    {
        var templates = await db.AppTemplates.IgnoreQueryFilters()
            .Where(t => t.IsBuiltIn && t.IsEnabled && t.Category != "database")
            .OrderBy(t => t.Name)
            .Select(t => new TemplateChoiceViewModel(t.Key, t.Name, t.NameFa, t.Category, t.IconUrl))
            .ToListAsync(ct);

        var chosen = FeaturedTemplates.Parse(await ReadAsync(SettingKeys.FeaturedTemplates, ct));

        return new AdminSettingsViewModel
        {
            Templates = templates,

            // Chosen first, in their order, so the form reads as the dashboard will.
            Featured = chosen.Where(k => templates.Any(t => t.Key == k)).ToList(),
            FeaturedSlots = FeaturedTemplates.Slots,
            DefaultPanelMode = await ReadAsync(
                Harbora.Web.Infrastructure.PanelModeProvider.DefaultModeSettingKey, ct),
            DefaultCulture = await ReadAsync(SettingKeys.DefaultCulture, ct),
            PlatformName = await ReadAsync(SettingKeys.PlatformName, ct),
            Sizes = await db.InstanceSizes.Where(s => s.IsEnabled).OrderBy(s => s.SortOrder)
                .Select(s => new SizeChoiceViewModel(s.Key, s.Name, s.CpuCores, s.MemoryBytes, s.DiskBytes))
                .ToListAsync(ct),
            DefaultInstanceSize = await ReadAsync(SettingKeys.DefaultInstanceSize, ct),
            PreviewsDefault = string.Equals(
                await ReadAsync(SettingKeys.PreviewsDefault, ct), "true", StringComparison.OrdinalIgnoreCase),
            RegistryDiscoveryEnabled = string.Equals(
                await ReadAsync(SettingKeys.RegistryDiscoveryEnabled, ct), "true", StringComparison.OrdinalIgnoreCase)
        };
    }
}
