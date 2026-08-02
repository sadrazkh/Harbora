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
public sealed class TemplatesController(HarboraDbContext db, ICurrentUser currentUser) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private bool IsReviewer => User.IsInRole("Owner") || User.IsInRole("Admin");

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Templates";

        // Filtered in memory against one rule rather than a Where clause repeated per screen —
        // "who may see this" decides whether one tenant's unreviewed image is offered to another.
        var all = await db.AppTemplates.OrderBy(t => t.Category).ThenBy(t => t.Name).ToListAsync(ct);
        ViewBag.WorkspaceId = WorkspaceId;
        ViewBag.Reviewing = IsReviewer
            ? all.Where(t => t.Status == Harbora.Domain.Templates.TemplateStatus.Submitted).ToList()
            : [];

        return View(all.Where(t => Harbora.Infrastructure.Templates.TemplateCatalog.IsVisibleTo(t, WorkspaceId)).ToList());
    }

    /// <summary>
    /// Saves a template this workspace owns, refusing a manifest that cannot work. Checked here
    /// rather than at deploy time, which is an hour later and much more expensive to unpick.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Guid? id, string name, string category, string manifestJson, CancellationToken ct)
    {
        if (!Harbora.Infrastructure.Templates.TemplateManifest.TryParse(manifestJson, out _, out var errors))
        {
            TempData["Error"] = string.Join(" ", errors);
            return RedirectToAction(nameof(Index));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "A template needs a name.";
            return RedirectToAction(nameof(Index));
        }

        var template = id is { } existingId
            ? await db.AppTemplates.FirstOrDefaultAsync(t => t.Id == existingId, ct)
            : null;

        if (template is null)
        {
            template = new Harbora.Domain.Templates.AppTemplate
            {
                WorkspaceId = WorkspaceId,
                Key = Guid.NewGuid().ToString("N")[..8],
                Status = Harbora.Domain.Templates.TemplateStatus.Private
            };
            db.AppTemplates.Add(template);
        }
        else if (!Harbora.Infrastructure.Templates.TemplateCatalog.CanEdit(template, WorkspaceId))
        {
            // Editing behind an approval would make review meaningless: submit something harmless,
            // then change it afterwards.
            TempData["Error"] = "This template cannot be changed.";
            return RedirectToAction(nameof(Index));
        }

        template.Name = name.Trim();
        template.Category = string.IsNullOrWhiteSpace(category) ? "app" : category.Trim();
        template.ManifestJson = manifestJson;
        await db.SaveChangesAsync(ct);

        TempData["Message"] = "Saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(Guid id, CancellationToken ct)
    {
        var template = await db.AppTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (template is null) return NotFound();

        if (!Harbora.Infrastructure.Templates.TemplateCatalog.CanSubmit(template, WorkspaceId))
        {
            TempData["Error"] = "This template cannot be sent for review right now.";
            return RedirectToAction(nameof(Index));
        }

        template.Status = Harbora.Domain.Templates.TemplateStatus.Submitted;
        template.ReviewNote = null;
        await db.SaveChangesAsync(ct);
        TempData["Message"] = "Sent for review. It stays usable by you in the meantime.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>An admin decides whether a template is offered to every tenant.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Review(Guid id, bool approve, string? note, CancellationToken ct)
    {
        if (!IsReviewer) return Forbid();

        var template = await db.AppTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (template is null) return NotFound();

        // A rejection with no reason is a wall: the author cannot act on it.
        if (!approve && string.IsNullOrWhiteSpace(note))
        {
            TempData["Error"] = "Say why it is being sent back — the author cannot act on silence.";
            return RedirectToAction(nameof(Index));
        }

        template.Status = approve
            ? Harbora.Domain.Templates.TemplateStatus.Approved
            : Harbora.Domain.Templates.TemplateStatus.Rejected;
        template.ReviewNote = note;
        template.ReviewedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        TempData["Message"] = approve ? "Approved and added to the catalog." : "Sent back to its author.";
        return RedirectToAction(nameof(Index));
    }
}

[Authorize]
public sealed class SettingsController(
    HarboraDbContext db,
    ITokenService tokens,
    Harbora.Infrastructure.Assistant.AssistantService assistant,
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
        // The page stays open to everyone because a person's own API tokens live on it. The platform
        // half does not: the fields were rendered disabled, which stops them being edited and not
        // being read, and the ACME address is an administrator's personal email.
        ViewBag.PlatformName = settings.GetValueOrDefault(Harbora.Domain.Settings.SettingKeys.PlatformName, "Harbora");
        ViewBag.RootDomain = IsProvider
            ? settings.GetValueOrDefault(Harbora.Domain.Settings.SettingKeys.PlatformRootDomain, "")
            : "";
        ViewBag.AcmeEmail = IsProvider
            ? settings.GetValueOrDefault(Harbora.Domain.Settings.SettingKeys.AcmeEmail, "")
            : "";
        ViewBag.Culture = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

        // The key is never sent back to the page — only whether one is stored. A settings screen
        // that renders a secret so it can be re-saved is a settings screen that leaks it into every
        // browser cache and screen recording.
        var assistantConfig = await assistant.GetConfigAsync(ct);
        ViewBag.Assistant = IsProvider ? assistantConfig : null;
        ViewBag.AssistantHasKey = !string.IsNullOrWhiteSpace(assistantConfig.ApiKey);
        ViewBag.AssistantUnavailable =
            Harbora.Infrastructure.Assistant.AssistantAvailability.Check(assistantConfig)?.Reason;

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

    /// <summary>
    /// Provider-only: configure the AI assistant. Leaving the key box blank keeps the stored one,
    /// so saving a model change does not silently disable the feature.
    /// </summary>
    [HttpPost("/settings/assistant")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.PlatformManage)]
    public async Task<IActionResult> UpdateAssistant(
        bool enabled, string? provider, string? model, string? apiKey, string? baseUrl, CancellationToken ct)
    {
        await assistant.SaveConfigAsync(enabled, provider, model, apiKey, baseUrl, ct);
        TempData["Message"] = "Settings saved.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Provider-only: ask the configured provider to answer a fixed, empty question, and report what
    /// came back verbatim — including a refusal. A green tick that only means "we sent something" is
    /// the kind of check that makes people trust a broken setting.
    /// </summary>
    [HttpPost("/settings/assistant/test")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.PlatformManage)]
    public async Task<IActionResult> TestAssistant(CancellationToken ct)
    {
        var answer = await assistant.TestAsync(ct);

        if (answer.Ok) TempData["Message"] = $"The AI provider answered: {answer.Text}";
        else TempData["Error"] = answer.Text;

        return RedirectToAction(nameof(Index));
    }

    /// <summary>Provider-only: forget the stored API key. Its own action because it cannot be undone.</summary>
    [HttpPost("/settings/assistant/key/clear")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.PlatformManage)]
    public async Task<IActionResult> ClearAssistantKey(CancellationToken ct)
    {
        await assistant.ClearApiKeyAsync(ct);
        TempData["Message"] = "The AI provider key was removed.";
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
