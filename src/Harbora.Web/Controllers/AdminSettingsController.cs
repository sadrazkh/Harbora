using System.Reflection;
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
    IAuditLogger audit,
    Harbora.Application.Abstractions.ISecretProtector protector,
    Harbora.Infrastructure.Notifications.PlatformMailer mailer,
    Harbora.Application.Abstractions.ICurrentUser currentUser,
    Harbora.Infrastructure.Security.ExternalLoginSettingsService externalLogins,
    Harbora.Web.Infrastructure.ExternalLoginSchemeCache externalLoginSchemes,
    Microsoft.Extensions.Options.IOptions<Harbora.Modules.Sync.Infrastructure.SyncFeatureOptions> syncFeatures,
    Microsoft.Extensions.Options.IOptions<Harbora.Modules.Backup.Infrastructure.BackupFeatureOptions> backupFeatures) : Controller
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
        await audit.LogAsync("platform.featured_templates", "setting", null, ClientIp, workspaceId: null, ct: ct);

        TempData["Message"] = IsFa ? "اپ‌های منتخب ذخیره شد." : "Featured applications saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("panel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePanel(
        string? defaultMode, string? defaultCulture, string? quickStartDefault, string? overviewDefault,
        CancellationToken ct)
    {
        // Only what it understands. An unrecognised value is cleared rather than stored, or the
        // setting silently stops applying and the default it names never happens.
        var mode = Enum.TryParse<PanelMode>(defaultMode, ignoreCase: true, out var parsed)
            ? parsed.ToString()
            : string.Empty;

        var culture = defaultCulture is "fa" or "en" ? defaultCulture : string.Empty;

        await WriteAsync(Harbora.Web.Infrastructure.PanelModeProvider.DefaultModeSettingKey, mode, ct);
        await WriteAsync(SettingKeys.DefaultCulture, culture, ct);

        // Stored through the same rule that reads them, so "" means "the shipped answer" on both
        // sides. Writing "false" for an unset dropdown would hide a panel nobody chose to hide.
        await WriteAsync(SettingKeys.QuickStartDefault,
            Harbora.Infrastructure.Navigation.RailVisibility.Format(
                Harbora.Infrastructure.Navigation.RailVisibility.ParseSetting(quickStartDefault)), ct);
        await WriteAsync(SettingKeys.OverviewDefault,
            Harbora.Infrastructure.Navigation.RailVisibility.Format(
                Harbora.Infrastructure.Navigation.RailVisibility.ParseSetting(overviewDefault)), ct);
        await audit.LogAsync("platform.panel_defaults", "setting", $"{mode}/{culture}", ClientIp, workspaceId: null, ct: ct);

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
        await audit.LogAsync("platform.resource_defaults", "setting", size, ClientIp, workspaceId: null, ct: ct);

        TempData["Message"] = IsFa ? "پیش‌فرض‌های منابع ذخیره شد." : "Resource defaults saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("platform")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePlatform(string? platformName, CancellationToken ct)
    {
        await WriteAsync(SettingKeys.PlatformName, (platformName ?? string.Empty).Trim(), ct);
        await audit.LogAsync("platform.name", "setting", platformName, ClientIp, workspaceId: null, ct: ct);

        TempData["Message"] = IsFa ? "نام پلتفرم ذخیره شد." : "Platform name saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("smtp")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSmtp(
        string? host, int? port, string? user, string? password, string? from, bool useSsl,
        CancellationToken ct)
    {
        await WriteAsync(SettingKeys.SmtpHost, (host ?? "").Trim(), ct);
        await WriteAsync(SettingKeys.SmtpPort, port is > 0 and < 65536 ? port.Value.ToString() : "", ct);
        await WriteAsync(SettingKeys.SmtpUser, (user ?? "").Trim(), ct);
        await WriteAsync(SettingKeys.SmtpFrom, (from ?? "").Trim(), ct);
        await WriteAsync(SettingKeys.SmtpUseSsl, useSsl ? "true" : "false", ct);

        // Left blank means "keep the stored one" — a settings form that must be re-fed the secret
        // to save anything else is a settings form that leaks it into autofill and screen shares.
        if (!string.IsNullOrWhiteSpace(password))
            await WriteAsync(SettingKeys.SmtpPassword, protector.Protect(password), ct);

        await audit.LogAsync("platform.smtp", "setting", (host ?? "").Trim(), ClientIp, workspaceId: null, ct: ct);

        TempData["Message"] = IsFa ? "تنظیمات SMTP ذخیره شد." : "SMTP settings saved.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Configures one external sign-in provider.
    ///
    /// <para>
    /// A blank secret keeps the stored one, the same way the SMTP form works and for the same reason.
    /// The scheme cache is emptied afterwards because the framework holds each scheme's options for
    /// the life of the process — without this, "Saved" would be true of the database and false of the
    /// sign-in page until somebody restarted the panel.
    /// </para>
    /// </summary>
    [HttpPost("sso")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSso(
        string provider, bool enabled, string? clientId, string? clientSecret,
        string? authority, string? displayName, CancellationToken ct)
    {
        if (Harbora.Domain.Identity.ExternalLoginProviders.Normalise(provider) is not { } key)
            return NotFound();

        await externalLogins.SaveAsync(key, enabled, clientId, clientSecret, authority, displayName, ct);
        externalLoginSchemes.Forget();
        await audit.LogAsync("platform.sso_provider", "setting", $"{key}/{(enabled ? "on" : "off")}", ClientIp, workspaceId: null, ct: ct);

        // Named, and honest about the difference between the switch and the effect: a provider
        // switched on with no client id shows no button, and a page that said "enabled" would be
        // reporting work it did not do.
        var saved = (await externalLogins.GetAsync(ct)).For(key);
        TempData[saved.Enabled && !saved.IsConfigured ? "Error" : "Message"] = saved.Enabled && !saved.IsConfigured
            ? (IsFa
                ? "ذخیره شد، اما تا کامل‌شدن شناسه و کلید، دکمه‌ای نشان داده نمی‌شود."
                : "Saved — but no button appears until the client id and secret are both filled in.")
            : (IsFa ? "تنظیمات ورود ذخیره شد." : "Sign-in provider saved.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("updates")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveUpdateCheck(bool updateCheck, CancellationToken ct)
    {
        await WriteAsync(SettingKeys.UpdateCheckEnabled, updateCheck ? "true" : "false", ct);
        await audit.LogAsync("platform.update_check", "setting", updateCheck ? "on" : "off", ClientIp, workspaceId: null, ct: ct);

        TempData["Message"] = updateCheck
            ? (IsFa ? "بررسی روزانهٔ به‌روزرسانی روشن شد." : "The daily update check is on.")
            : (IsFa ? "بررسی به‌روزرسانی خاموش شد." : "The update check is off.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("smtp/test")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestSmtp(CancellationToken ct)
    {
        // To the signed-in administrator, through exactly the path every real email takes. This
        // codebase has already shipped a Test button that reported success regardless; the lesson
        // is that the button must be capable of saying no.
        var to = currentUser.Email;
        if (string.IsNullOrWhiteSpace(to))
        {
            TempData["Error"] = IsFa ? "ایمیل حساب شما معلوم نیست." : "Your account has no email to send to.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            await mailer.SendAsync(to,
                IsFa ? "ایمیل آزمایشی Harbora" : "Harbora test email",
                IsFa ? "اگر این را می‌خوانید، SMTP پلتفرم کار می‌کند." : "If you can read this, platform SMTP works.",
                null, ct);
            TempData["Message"] = IsFa ? $"ایمیل آزمایشی به {to} رفت." : $"A test email went to {to}.";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The server's own words, because "failed" alone sends somebody digging through logs
            // for what this line already knew.
            TempData["Error"] = (IsFa ? "ارسال نشد: " : "Could not send: ") + e.Message;
        }

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

        var sso = await externalLogins.GetAsync(ct);

        return new AdminSettingsViewModel
        {
            Sso = Harbora.Domain.Identity.ExternalLoginProviders.All.Select(provider =>
            {
                var config = sso.For(provider);
                return new SsoProviderViewModel(
                    provider,
                    Harbora.Domain.Identity.ExternalLoginProviders.DisplayName(provider, config.DisplayName, IsFa),
                    config.Enabled,
                    config.ClientId,
                    HasSecret: config.ClientSecret is not null,
                    config.Authority,
                    config.DisplayName,
                    // The scheme's own callback path, on this panel's own host — what the provider's
                    // console has to be told, exactly.
                    RedirectUri: $"{Request.Scheme}://{Request.Host}" +
                                 Harbora.Web.Infrastructure.ExternalAuth.ProviderCallbackPath(provider),
                    config.IsConfigured);
            }).ToList(),

            Templates = templates,

            // Chosen first, in their order, so the form reads as the dashboard will.
            Featured = chosen.Where(k => templates.Any(t => t.Key == k)).ToList(),
            FeaturedSlots = FeaturedTemplates.Slots,
            DefaultPanelMode = await ReadAsync(
                Harbora.Web.Infrastructure.PanelModeProvider.DefaultModeSettingKey, ct),
            DefaultCulture = await ReadAsync(SettingKeys.DefaultCulture, ct),
            QuickStartDefault = Harbora.Infrastructure.Navigation.RailVisibility
                .ParseSetting(await ReadAsync(SettingKeys.QuickStartDefault, ct)),
            OverviewDefault = Harbora.Infrastructure.Navigation.RailVisibility
                .ParseSetting(await ReadAsync(SettingKeys.OverviewDefault, ct)),
            PlatformName = await ReadAsync(SettingKeys.PlatformName, ct),
            Sizes = await db.InstanceSizes.Where(s => s.IsEnabled).OrderBy(s => s.SortOrder)
                .Select(s => new SizeChoiceViewModel(s.Key, s.Name, s.CpuCores, s.MemoryBytes, s.DiskBytes))
                .ToListAsync(ct),
            DefaultInstanceSize = await ReadAsync(SettingKeys.DefaultInstanceSize, ct),
            PreviewsDefault = string.Equals(
                await ReadAsync(SettingKeys.PreviewsDefault, ct), "true", StringComparison.OrdinalIgnoreCase),
            RegistryDiscoveryEnabled = string.Equals(
                await ReadAsync(SettingKeys.RegistryDiscoveryEnabled, ct), "true", StringComparison.OrdinalIgnoreCase),

            // These two are configuration, not settings: they need an engine on the host before
            // they can do anything, so they are reported here rather than switched here. Reported
            // at all because a complete section that is simply invisible reads as a missing
            // feature — there is no way, from inside the panel, to tell the two apart.
            SyncEnabled = syncFeatures.Value.Sync,
            BackupEnabled = backupFeatures.Value.Backup,

            SmtpHost = await ReadAsync(SettingKeys.SmtpHost, ct),
            SmtpPort = await ReadAsync(SettingKeys.SmtpPort, ct),
            SmtpUser = await ReadAsync(SettingKeys.SmtpUser, ct),
            SmtpFrom = await ReadAsync(SettingKeys.SmtpFrom, ct),
            SmtpUseSsl = !string.Equals(
                await ReadAsync(SettingKeys.SmtpUseSsl, ct), "false", StringComparison.OrdinalIgnoreCase),
            SmtpHasPassword = !string.IsNullOrEmpty(await ReadAsync(SettingKeys.SmtpPassword, ct)),

            UpdateCheckEnabled = string.Equals(
                await ReadAsync(SettingKeys.UpdateCheckEnabled, ct), "true", StringComparison.OrdinalIgnoreCase),
            LatestReleaseTag = await ReadAsync(SettingKeys.UpdateLatestTag, ct),
            RunningVersion = System.Reflection.Assembly.GetEntryAssembly()?
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion,

            DrillStatus = await Harbora.Infrastructure.DisasterRecovery.RestoreDrillRecord
                .ReadAsync(db, DateTimeOffset.UtcNow, ct)
        };
    }
}
