using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Sections whose data model + engines exist but whose full UI lands in later phases. They render
/// a shared placeholder so navigation is complete and honest rather than dead links.
/// </summary>
[Authorize]
public sealed class TemplatesController(HarboraDbContext db) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Templates";
        var templates = await db.AppTemplates.Where(t => t.IsEnabled)
            .OrderBy(t => t.Category).ThenBy(t => t.Name).ToListAsync(ct);
        return View(templates);
    }
}

[Authorize]
public sealed class SettingsController(
    HarboraDbContext db,
    ITokenService tokens,
    ICurrentUser currentUser) : Controller
{
    private bool IsProvider => User.IsInRole("Owner") || User.IsInRole("Admin");

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Settings";
        ViewBag.Tokens = await db.ApiTokens
            .Where(t => t.UserId == currentUser.UserId && !t.IsRevoked)
            .OrderByDescending(t => t.CreatedAt).ToListAsync(ct);

        var settings = await db.Settings.Where(s => !s.IsSecret).ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        ViewBag.IsProvider = IsProvider;
        ViewBag.PlatformName = settings.GetValueOrDefault(Harbora.Domain.Settings.SettingKeys.PlatformName, "Harbora");
        ViewBag.RootDomain = settings.GetValueOrDefault(Harbora.Domain.Settings.SettingKeys.PlatformRootDomain, "");
        ViewBag.AcmeEmail = settings.GetValueOrDefault(Harbora.Domain.Settings.SettingKeys.AcmeEmail, "");
        ViewBag.Culture = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return View();
    }

    /// <summary>Provider-only: update the platform display settings.</summary>
    [HttpPost("/settings/platform")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.PlatformManage)]
    public async Task<IActionResult> UpdatePlatform(string platformName, string? rootDomain, string? acmeEmail, CancellationToken ct)
    {
        await SetAsync(Harbora.Domain.Settings.SettingKeys.PlatformName, platformName, ct);
        await SetAsync(Harbora.Domain.Settings.SettingKeys.PlatformRootDomain, rootDomain ?? "", ct);
        await SetAsync(Harbora.Domain.Settings.SettingKeys.AcmeEmail, acmeEmail ?? "", ct);
        await db.SaveChangesAsync(ct);
        TempData["Message"] = "Settings saved.";
        return RedirectToAction(nameof(Index));
    }

    private async Task SetAsync(string key, string value, CancellationToken ct)
    {
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null) db.Settings.Add(new Harbora.Domain.Settings.Setting { Key = key, Value = value });
        else setting.Value = value;
    }

    /// <summary>Issues a CLI/API token. The plaintext is shown exactly once via TempData.</summary>
    [HttpPost("/settings/tokens")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateToken(string name, CancellationToken ct)
    {
        var userId = currentUser.UserId ?? Guid.Empty;
        var issued = tokens.Issue(userId, name, Harbora.Domain.Common.TokenType.Cli, null);
        db.ApiTokens.Add(new Harbora.Domain.Identity.ApiToken
        {
            UserId = userId,
            Name = string.IsNullOrWhiteSpace(name) ? "CLI token" : name,
            Prefix = issued.Prefix,
            TokenHash = issued.Hash,
            Type = Harbora.Domain.Common.TokenType.Cli
        });
        await db.SaveChangesAsync(ct);
        TempData["NewToken"] = issued.PlaintextToken;
        return RedirectToAction(nameof(Index));
    }
}
