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
public sealed class TemplatesController(
    HarboraDbContext db,
    Harbora.Infrastructure.Templates.TemplateDeploymentService templateDeployments,
    IAuthorizationService authorization,
    IAuditLogger audit,
    ICurrentUser currentUser) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private bool IsReviewer => User.IsInRole("Owner") || User.IsInRole("Admin");
    private bool IsFa => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    [HttpGet("/templates")]
    public async Task<IActionResult> Index(string? q, string? category, string? scope, CancellationToken ct)
    {
        ViewData["Title"] = "Templates";

        // Filtered in memory against one rule rather than a Where clause repeated per screen —
        // "who may see this" decides whether one tenant's unreviewed image is offered to another.
        var all = await db.AppTemplates.OrderBy(t => t.Category).ThenBy(t => t.Name).ToListAsync(ct);
        var visible = all.Where(t => Harbora.Infrastructure.Templates.TemplateCatalog.IsVisibleTo(t, WorkspaceId));
        if (scope == "mine") visible = visible.Where(t => t.WorkspaceId == WorkspaceId);
        else if (scope == "featured") visible = visible.Where(t =>
            Harbora.Infrastructure.Templates.TemplateManifest.TryParse(t.ManifestJson, out var m, out _) && m!.Featured);

        if (!string.IsNullOrWhiteSpace(category))
            visible = visible.Where(t => t.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(q))
            visible = visible.Where(t => t.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                         || t.NameFa.Contains(q, StringComparison.OrdinalIgnoreCase)
                                         || t.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                                         || t.DescriptionFa.Contains(q, StringComparison.OrdinalIgnoreCase));

        var items = visible.Select(BuildItem).Where(i => i is not null).Cast<Harbora.Web.ViewModels.TemplateCatalogItemViewModel>().ToList();
        return View(new Harbora.Web.ViewModels.TemplateCatalogPageViewModel
        {
            Items = items,
            Reviewing = IsReviewer
                ? all.Where(t => t.Status == Harbora.Domain.Templates.TemplateStatus.Submitted).ToList()
                : [],
            WorkspaceId = WorkspaceId,
            Query = q,
            Category = category,
            Scope = scope,
            Categories = all.Where(t => Harbora.Infrastructure.Templates.TemplateCatalog.IsVisibleTo(t, WorkspaceId))
                .Select(t => t.Category).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList()
        });
    }

    [HttpGet("/templates/{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var template = await db.AppTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (template is null || !Harbora.Infrastructure.Templates.TemplateCatalog.IsVisibleTo(template, WorkspaceId))
            return NotFound();

        var item = BuildItem(template);
        if (item is null) return NotFound();
        ViewData["Title"] = item.Name;
        return View(item);
    }

    [HttpGet("/templates/{id:guid}/deploy")]
    public async Task<IActionResult> Deploy(Guid id, CancellationToken ct)
    {
        var template = await db.AppTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (template is null || !Harbora.Infrastructure.Templates.TemplateCatalog.IsVisibleTo(template, WorkspaceId))
            return NotFound();

        var item = BuildItem(template);
        if (item is null) return NotFound();
        if (!await CanDeployAsync(item)) return Forbid();

        var (versions, architecture, hasVersions) = await VersionChoicesAsync(template.Id, ct);
        return View(new Harbora.Web.ViewModels.TemplateDeployPageViewModel
        {
            Item = item,
            Versions = versions,
            Sizes = await SizeChoicesAsync(ct),
            NodeArchitecture = architecture,
            HasVersionsButNoneOfferable = hasVersions && versions.Count == 0,
            Input = new Harbora.Web.ViewModels.TemplateDeployInput
            {
                TemplateId = template.Id,
                ProjectName = item.Name,
                ResourceName = item.Name,

                // Preselected rather than left blank, so the form reflects what will happen if
                // somebody submits it without touching the selector.
                VersionId = versions.FirstOrDefault()?.Id
            }
        });
    }

    /// <summary>
    /// The versions to offer, what the server runs, and whether this template has versions at all.
    ///
    /// The third value matters: "no versions" and "versions, none of them usable here" produce the
    /// same empty list and need opposite messages — one is an ordinary template, the other is an
    /// entry somebody will otherwise keep trying to deploy.
    /// </summary>
    /// <summary>
    /// The resource plans this workspace may pick from, filtered by its plan — the same list and the
    /// same filter the application form uses, so a template cannot create a database of a size the
    /// person could not have chosen directly.
    /// </summary>
    private async Task<List<Microsoft.AspNetCore.Mvc.Rendering.SelectListItem>> SizeChoicesAsync(CancellationToken ct)
    {
        var plan = await db.Workspaces.Where(w => w.Id == WorkspaceId)
                .Select(w => w.PlanId).FirstOrDefaultAsync(ct) is { } planId
            ? await db.Plans.FirstOrDefaultAsync(p => p.Id == planId, ct)
            : await db.Plans.FirstOrDefaultAsync(p => p.IsDefault, ct);

        var allowed = plan is null || string.IsNullOrWhiteSpace(plan.AllowedSizeKeys)
            ? null
            : plan.AllowedSizeKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (await db.InstanceSizes.Where(s => s.IsEnabled).OrderBy(s => s.SortOrder).ToListAsync(ct))
            .Where(s => allowed is null || allowed.Contains(s.Key))
            .Select(s => new Microsoft.AspNetCore.Mvc.Rendering.SelectListItem(
                Harbora.Infrastructure.Tenancy.InstanceSizeLabel.For(
                    s.Name, s.CpuCores, s.MemoryBytes, s.DiskBytes), s.Key))
            .ToList();
    }

    private async Task<(IReadOnlyList<Harbora.Domain.Templates.AppTemplateVersion> Versions, string? Architecture, bool HasVersions)>
        VersionChoicesAsync(Guid templateId, CancellationToken ct)
    {
        var architecture = await db.Servers.Where(s => s.IsLocal)
            .Select(s => s.Architecture).FirstOrDefaultAsync(ct);

        var all = await db.AppTemplateVersions.AsNoTracking()
            .Where(v => v.AppTemplateId == templateId).ToListAsync(ct);

        return (Harbora.Infrastructure.Templates.VersionSelection.Offerable(all, architecture), architecture, all.Count > 0);
    }

    [HttpPost("/templates/{id:guid}/deploy")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Deploy(Guid id, Harbora.Web.ViewModels.TemplateDeployInput input, CancellationToken ct)
    {
        input.TemplateId = id;
        var template = await db.AppTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        var item = template is null ? null : BuildItem(template);
        if (template is null || item is null
            || !Harbora.Infrastructure.Templates.TemplateCatalog.IsVisibleTo(template, WorkspaceId))
            return NotFound();
        if (!await CanDeployAsync(item)) return Forbid();

        if (item.NeedsRepository && string.IsNullOrWhiteSpace(input.RepositoryUrl))
            ModelState.AddModelError(nameof(input.RepositoryUrl), IsFa ? "آدرس مخزن Git لازم است." : "A Git repository URL is required.");

        if (!ModelState.IsValid)
            return View(await RedrawAsync(item, input, ct));

        try
        {
            var result = await templateDeployments.DeployAsync(new Harbora.Infrastructure.Templates.TemplateDeployRequest(
                WorkspaceId,
                currentUser.UserId ?? Guid.Empty,
                id,
                input.ProjectName,
                input.ResourceName,
                input.RepositoryUrl,
                input.GitRef,
                input.Variables,
                input.DeployNow,
                // Named, because a positional argument list this long is how the next parameter
                // added in the middle silently becomes somebody else's value.
                InstanceSizeKey: input.InstanceSizeKey,
                VersionId: input.VersionId), ct);

            await audit.LogAsync("template.deploy", "template", id.ToString(),
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                metadataJson: $"{{\"projectId\":\"{result.ProjectId}\",\"dependencies\":{result.DependencyCount}}}",
                ct: ct);

            TempData["Message"] = IsFa
                ? $"پروژه ساخته شد؛ {result.DependencyCount} سرویس وابسته پیش از برنامه در صف قرار گرفت."
                : $"Project created; {result.DependencyCount} dependency service(s) were queued before the app.";

            if (result.DeploymentId is { } deploymentId)
                return RedirectToAction("Details", "Deployments", new { id = deploymentId });
            if (result.ServiceId is { } serviceId)
                return RedirectToAction("Details", "Databases", new { id = serviceId });
            return RedirectToAction("Details", "Projects", new { id = result.ProjectId });
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View(await RedrawAsync(item, input, ct));
        }
    }

    /// <summary>
    /// The page again after a refusal, with the version list reloaded.
    ///
    /// Reloaded rather than carried in a hidden field: the reason the submission failed may be that
    /// a version was withdrawn, and redrawing the old list would offer it again.
    /// </summary>
    private async Task<Harbora.Web.ViewModels.TemplateDeployPageViewModel> RedrawAsync(
        Harbora.Web.ViewModels.TemplateCatalogItemViewModel item,
        Harbora.Web.ViewModels.TemplateDeployInput input,
        CancellationToken ct)
    {
        var (versions, architecture, hasVersions) = await VersionChoicesAsync(input.TemplateId, ct);
        return new Harbora.Web.ViewModels.TemplateDeployPageViewModel
        {
            Item = item,
            Input = input,
            Versions = versions,
            Sizes = await SizeChoicesAsync(ct),
            NodeArchitecture = architecture,
            HasVersionsButNoneOfferable = hasVersions && versions.Count == 0
        };
    }

    /// <summary>
    /// Saves a template this workspace owns, refusing a manifest that cannot work. Checked here
    /// rather than at deploy time, which is an hour later and much more expensive to unpick.
    /// </summary>
    [HttpPost]
    [Route("/templates/custom")]
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
    [Route("/templates/{id:guid}/submit")]
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
    [Route("/templates/{id:guid}/review")]
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

    private Harbora.Web.ViewModels.TemplateCatalogItemViewModel? BuildItem(Harbora.Domain.Templates.AppTemplate template)
        => Harbora.Web.ViewModels.TemplateCatalogItemViewModel.Create(template, IsFa);

    private async Task<bool> CanDeployAsync(Harbora.Web.ViewModels.TemplateCatalogItemViewModel item)
    {
        if (!item.IsManagedService
            && !(await authorization.AuthorizeAsync(User, Capabilities.AppsCreate)).Succeeded)
            return false;

        // A stack that creates a database is a database-management operation too. Without this
        // check the template path would bypass the permission enforced by DatabasesController.
        if ((item.IsManagedService || item.Manifest.Requires.Count > 0)
            && !(await authorization.AuthorizeAsync(User, Capabilities.DatabasesManage)).Succeeded)
            return false;

        return true;
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

    private static bool IsFa =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

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
        var assistantUnavailable = Harbora.Infrastructure.Assistant.AssistantAvailability.Check(assistantConfig);
        ViewBag.AssistantUnavailable = IsFa ? assistantUnavailable?.ReasonFa : assistantUnavailable?.Reason;

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
