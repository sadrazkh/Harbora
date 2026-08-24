using Harbora.Domain.Authorization;
using Harbora.Domain.Configuration;
using Harbora.Infrastructure.Deployments;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// File-override rules for this app's own config file (C2, 2026-08-22 config-delivery plan) — the
/// panel replacing a value inside a file the app already reads (<c>appsettings.json</c>,
/// <c>config/database.yml</c>, ...) at deploy time, so a developer never has to rewrite their code to
/// read environment variables and never has to commit a real password.
///
/// A dedicated page (<c>/apps/{id}/config-overrides</c>), the same shape <c>AppDataController</c>
/// already uses for a single-purpose surface reached from an app rather than folded into the big
/// tabbed <c>Details</c> view — C3 (making the choice legible across env/groups/overrides in one
/// place) is a separate, later sub-project.
/// </summary>
public sealed partial class AppsController
{
    [HttpGet("/apps/{id:guid}/config-overrides")]
    public async Task<IActionResult> ConfigOverrides(Guid id, CancellationToken ct)
    {
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsEnv, ct)) return NotFound();

        var app = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        var model = await BuildConfigOverridesViewModelAsync(app.Id, app.Name, ct);
        ViewData["Title"] = app.Name;
        return View(model);
    }

    [HttpPost("/apps/{id:guid}/config-overrides")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> AddConfigOverride(
        Guid id, string filePath, string? formatOverride, string keyPath, string valueKind,
        string? literalValue, string? secretValue, string? attachedServiceAlias, CancellationToken ct)
    {
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsEnv, ct)) return NotFound();

        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(keyPath))
        {
            TempData["Error"] = IsFa ? "مسیر فایل و مسیر کلید هر دو الزامی‌اند." : "Both a file path and a key path are required.";
            return RedirectToAction(nameof(ConfigOverrides), new { id });
        }

        ConfigFileFormat? resolvedFormat = null;
        if (!string.IsNullOrWhiteSpace(formatOverride))
        {
            if (!Enum.TryParse<ConfigFileFormat>(formatOverride, ignoreCase: true, out var parsed))
            {
                TempData["Error"] = IsFa ? "قالب انتخاب‌شده شناخته‌شده نیست." : "That format is not recognised.";
                return RedirectToAction(nameof(ConfigOverrides), new { id });
            }
            resolvedFormat = parsed;
        }
        else if (ConfigFileFormatDetector.FromExtension(filePath) is null)
        {
            TempData["Error"] = IsFa
                ? "قالب فایل از پسوند آن قابل تشخیص نیست؛ یک قالب را صریحاً انتخاب کنید."
                : "This file's format could not be detected from its extension — choose one explicitly.";
            return RedirectToAction(nameof(ConfigOverrides), new { id });
        }

        var rule = new ConfigOverrideRule
        {
            AppId = app.Id,
            FilePath = filePath.Trim(),
            FormatOverride = resolvedFormat,
            KeyPath = keyPath.Trim(),
            HasUnpublishedChanges = true
        };

        switch (valueKind)
        {
            case "secret":
                rule.ValueKind = Domain.Configuration.ConfigOverrideValueKind.Secret;
                rule.EncryptedSecretValue = protector.Protect(secretValue ?? string.Empty);
                break;

            case "service":
                if (string.IsNullOrWhiteSpace(attachedServiceAlias))
                {
                    TempData["Error"] = IsFa
                        ? "نام مستعار یک دیتابیس متصل را وارد کنید."
                        : "Enter the alias of an attached database.";
                    return RedirectToAction(nameof(ConfigOverrides), new { id });
                }
                rule.ValueKind = Domain.Configuration.ConfigOverrideValueKind.AttachedServiceConnectionString;
                rule.AttachedServiceAlias = attachedServiceAlias.Trim();
                break;

            default:
                rule.ValueKind = Domain.Configuration.ConfigOverrideValueKind.Literal;
                rule.LiteralValue = literalValue ?? string.Empty;
                break;
        }

        db.ConfigOverrideRules.Add(rule);
        await db.SaveChangesAsync(ct);

        TempData["Message"] = IsFa
            ? "قانون اضافه شد. با استقرار بعدی این اپ اعمال می‌شود."
            : "Rule added. It applies on this app's next deploy.";
        return RedirectToAction(nameof(ConfigOverrides), new { id });
    }

    [HttpPost("/apps/{id:guid}/config-overrides/{ruleId:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> DeleteConfigOverride(Guid id, Guid ruleId, CancellationToken ct)
    {
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsEnv, ct)) return NotFound();

        var rule = await db.ConfigOverrideRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.AppId == id, ct);
        if (rule is not null)
        {
            db.ConfigOverrideRules.Remove(rule);
            await db.SaveChangesAsync(ct);
        }

        TempData["Message"] = rule is not null
            ? (IsFa ? "قانون حذف شد." : "Rule removed.")
            : (IsFa ? "این قانون دیگر وجود ندارد." : "That rule no longer exists.");
        return RedirectToAction(nameof(ConfigOverrides), new { id });
    }

    /// <summary>
    /// "Validate a rule against the deployed app before deploying" — read the file, resolve the key
    /// path, show the current value and what it would become, entirely without touching anything.
    /// The plan's own phrase for the difference this makes: debugging in one minute, not in ten
    /// deploys.
    /// </summary>
    [HttpPost("/apps/{id:guid}/config-overrides/{ruleId:guid}/validate")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> ValidateConfigOverride(Guid id, Guid ruleId, CancellationToken ct)
    {
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsEnv, ct)) return NotFound();

        var app = await db.Apps.Include(a => a.ConfigOverrideRules)
            .FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        var rule = app.ConfigOverrideRules.FirstOrDefault(r => r.Id == ruleId);
        if (rule is null) return NotFound();

        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == app.ServerId, ct);
        ConfigOverrideValidationRow validation;

        if (server is null || !server.IsLocal)
        {
            validation = new ConfigOverrideValidationRow(rule.Id, false, null, null, false,
                IsFa
                    ? "این اپ روی نود راه‌دور اجرا می‌شود؛ اعتبارسنجی در برابر اپ مستقرشده فقط برای نود محلی پشتیبانی می‌شود."
                    : "This app runs on a remote node; validating against the deployed app is only supported on this platform's local node.");
        }
        else
        {
            var containerId = await FindLiveContainerIdAsync(app, ct);
            if (containerId is null)
            {
                validation = new ConfigOverrideValidationRow(rule.Id, false, null, null, false,
                    IsFa ? "هیچ کانتینر در حال اجرایی برای این اپ پیدا نشد." : "No running container was found for this app.");
            }
            else
            {
                var preview = await configOverrides.PreviewAsync(app, rule, containerId, ct);
                validation = new ConfigOverrideValidationRow(
                    rule.Id, preview.Ok, preview.CurrentValue, preview.WouldBecomeValue,
                    preview.WouldBecomeIsSecret, preview.Failure?.Detail);
            }
        }

        var model = await BuildConfigOverridesViewModelAsync(app.Id, app.Name, ct);
        model.Validation = validation;
        ViewData["Title"] = app.Name;
        return View(nameof(ConfigOverrides), model);
    }

    private async Task<string?> FindLiveContainerIdAsync(Domain.Apps.App app, CancellationToken ct)
    {
        try
        {
            var docker = await engines.ResolveAsync(app.ServerId, ct);
            var containers = await docker.ListContainersAsync(null, ct);
            var slugExclusive = !await db.Apps.IgnoreQueryFilters()
                .AnyAsync(a => a.Slug == app.Slug && a.WorkspaceId != app.WorkspaceId, ct);
            return DeploymentPlanning.CurrentContainerId(containers, app.WorkspaceId, app.Slug, slugExclusive);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve the live container for app {App} to validate a config override.", app.Id);
            return null;
        }
    }

    private async Task<ConfigOverridesPageViewModel> BuildConfigOverridesViewModelAsync(Guid appId, string appName, CancellationToken ct)
    {
        var rules = await db.ConfigOverrideRules.AsNoTracking()
            .Where(r => r.AppId == appId)
            .OrderBy(r => r.FilePath).ThenBy(r => r.KeyPath)
            .ToListAsync(ct);

        return new ConfigOverridesPageViewModel
        {
            AppId = appId,
            AppName = appName,
            Rules = rules.Select(r => new ConfigOverrideRuleRow(
                r.Id, r.FilePath, r.FormatOverride?.ToString(), r.KeyPath,
                r.ValueKind switch
                {
                    Domain.Configuration.ConfigOverrideValueKind.Secret => IsFa ? "رمز" : "Secret",
                    Domain.Configuration.ConfigOverrideValueKind.AttachedServiceConnectionString =>
                        IsFa ? "رشتهٔ اتصال سرویس متصل" : "Attached service connection string",
                    _ => IsFa ? "مقدار ثابت" : "Literal"
                },
                r.ValueKind == Domain.Configuration.ConfigOverrideValueKind.Literal ? r.LiteralValue : null,
                r.ValueKind != Domain.Configuration.ConfigOverrideValueKind.Literal,
                r.HasUnpublishedChanges))
                .ToList()
        };
    }
}
