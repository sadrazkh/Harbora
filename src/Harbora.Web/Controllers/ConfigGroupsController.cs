using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Workspace-level shared environment-variable groups (Sub-project 9, 2026-08-20 platform-options
/// plan). Entries mirror <see cref="EnvironmentVariable"/> exactly — same name/value/secret shape,
/// same <see cref="ISecretProtector"/> ciphertext, same masking — reached from here and attached to
/// an app from the app's own env page (<c>AppsController.ConfigGroups.cs</c>), the same split
/// <c>AlertsController</c>/<c>EventSubscriptionsController</c> already draw between "the shared
/// thing" and "what uses it".
/// </summary>
[Authorize]
[Route("config-groups")]
public sealed class ConfigGroupsController(
    HarboraDbContext db,
    ISecretProtector protector,
    ICurrentUser currentUser) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;

    private bool IsFa =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    /// <summary>
    /// "2 apps: api, worker" — or past three, "5 apps: api, worker, cron and 2 more". The
    /// <c>ProjectsController.Delete</c> refusal idiom: named, not merely counted, so a refusal gives
    /// somebody something to act on.
    /// </summary>
    private string NamedList(IReadOnlyList<string> names)
    {
        const int shown = 3;
        var listed = names.Count > shown
            ? string.Join(IsFa ? "، " : ", ", names.Take(shown)) +
              (IsFa ? $" و {names.Count - shown} مورد دیگر" : $" and {names.Count - shown} more")
            : string.Join(IsFa ? "، " : ", ", names);

        return IsFa ? $"{names.Count} اپ: {listed}" : $"{names.Count} app{(names.Count == 1 ? "" : "s")}: {listed}";
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Config groups";

        var groups = await db.ConfigGroups
            .Where(g => g.WorkspaceId == WorkspaceId)
            .OrderBy(g => g.Name)
            .Select(g => new ConfigGroupRow(
                g.Id, g.Name,
                g.Entries.Count,
                g.Entries.Count(e => e.IsSecret),
                g.Apps.Count))
            .ToListAsync(ct);

        // Attached-app names per group, for the "will pick this up" list — read once, keyed by
        // group id, rather than N+1 queries per row.
        var attachedApps = await db.AppConfigGroups
            .Where(cg => groups.Select(g => g.Id).Contains(cg.ConfigGroupId))
            .Select(cg => new { cg.ConfigGroupId, cg.HasUnpublishedChanges, AppName = cg.App!.Name })
            .ToListAsync(ct);

        var entriesByGroup = await db.ConfigGroupEntries
            .Where(e => groups.Select(g => g.Id).Contains(e.ConfigGroupId))
            .OrderBy(e => e.Key)
            .Select(e => new ConfigGroupEntryRow(e.Id, e.ConfigGroupId, e.Key, e.IsSecret, e.IsSecret ? null : e.Value))
            .ToListAsync(ct);

        return View(new ConfigGroupsPageViewModel
        {
            Groups = groups,
            Entries = entriesByGroup,
            AttachedApps = attachedApps
                .GroupBy(x => x.ConfigGroupId)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<AttachedAppRow>)g.Select(x => new AttachedAppRow(x.AppName, x.HasUnpublishedChanges)).ToList())
        });
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> Create(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = IsFa ? "یک نام برای گروه وارد کنید." : "Give this group a name.";
            return RedirectToAction(nameof(Index));
        }

        if (await db.ConfigGroups.AnyAsync(g => g.WorkspaceId == WorkspaceId && g.Name == name, ct))
        {
            TempData["Error"] = IsFa
                ? $"گروهی با نام «{name}» از قبل وجود دارد."
                : $"A group named '{name}' already exists.";
            return RedirectToAction(nameof(Index));
        }

        db.ConfigGroups.Add(new ConfigGroup { WorkspaceId = WorkspaceId, Name = name.Trim() });
        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Refuses while any app is still attached, naming them — the <c>ProjectsController.Delete</c>
    /// idiom — rather than letting the FK's <c>DeleteBehavior.Restrict</c> surface as a raw
    /// constraint failure.
    /// </summary>
    [HttpPost("{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var group = await db.ConfigGroups.FirstOrDefaultAsync(g => g.Id == id && g.WorkspaceId == WorkspaceId, ct);
        if (group is null) return NotFound();

        var attachedTo = await db.AppConfigGroups
            .Where(cg => cg.ConfigGroupId == id)
            .Select(cg => cg.App!.Name)
            .ToListAsync(ct);

        if (attachedTo.Count > 0)
        {
            TempData["Error"] = IsFa
                ? $"این گروه هنوز به {NamedList(attachedTo)} متصل است. برای حذف، ابتدا آن را از همه‌ی اپ‌ها جدا کنید."
                : $"This group is still attached to {NamedList(attachedTo)}. Detach it from every app first, then delete it.";
            return RedirectToAction(nameof(Index));
        }

        var entries = await db.ConfigGroupEntries.Where(e => e.ConfigGroupId == id).ToListAsync(ct);
        db.ConfigGroupEntries.RemoveRange(entries);
        db.ConfigGroups.Remove(group);
        await db.SaveChangesAsync(ct);

        TempData["Message"] = IsFa ? "گروه حذف شد." : "Group deleted.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Adds or updates an entry by key — the same upsert-by-key shape <c>AppsController.AddEnv</c>
    /// uses for an app's own variables. Marks every app currently attached to this group stale: their
    /// running container may no longer match what the group now says, until their own next deploy.
    /// </summary>
    [HttpPost("{id:guid}/entries")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> AddEntry(Guid id, string key, string? value, bool isSecret, CancellationToken ct)
    {
        var group = await db.ConfigGroups.Include(g => g.Entries)
            .FirstOrDefaultAsync(g => g.Id == id && g.WorkspaceId == WorkspaceId, ct);
        if (group is null) return NotFound();

        if (string.IsNullOrWhiteSpace(key))
        {
            TempData["Error"] = IsFa ? "کلید الزامی است." : "Key is required.";
            return RedirectToAction(nameof(Index));
        }

        var existing = group.Entries.FirstOrDefault(e => e.Key == key);
        var stored = isSecret ? protector.Protect(value ?? "") : value ?? "";
        if (existing is null)
            group.Entries.Add(new ConfigGroupEntry { ConfigGroupId = group.Id, Key = key, Value = stored, IsSecret = isSecret });
        else { existing.Value = stored; existing.IsSecret = isSecret; }

        await MarkAttachedAppsStaleAsync(group.Id, ct);
        await db.SaveChangesAsync(ct);

        TempData["Message"] = IsFa
            ? "متغیر ذخیره شد. اپ‌های متصل با استقرار بعدی آن را دریافت می‌کنند."
            : "Variable saved. Attached apps pick it up on their next deploy.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/entries/{entryId:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> DeleteEntry(Guid id, Guid entryId, CancellationToken ct)
    {
        var group = await db.ConfigGroups.FirstOrDefaultAsync(g => g.Id == id && g.WorkspaceId == WorkspaceId, ct);
        if (group is null) return NotFound();

        await db.ConfigGroupEntries.Where(e => e.Id == entryId && e.ConfigGroupId == id).ExecuteDeleteAsync(ct);
        await MarkAttachedAppsStaleAsync(id, ct);
        await db.SaveChangesAsync(ct);

        TempData["Message"] = IsFa
            ? "متغیر حذف شد. اپ‌های متصل با استقرار بعدی آن را دریافت می‌کنند."
            : "Variable removed. Attached apps pick it up on their next deploy.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Every app attached to this group flips to "applies on next deploy" — the
    /// <c>FunctionDefinition.HasUnpublishedChanges</c> idiom, reused rather than reinvented. Group
    /// entries carry no query filter of their own (reached only through the group, which is
    /// filtered), and neither does the join — both follow the <c>EnvironmentVariable</c> rule.
    /// </summary>
    private async Task MarkAttachedAppsStaleAsync(Guid groupId, CancellationToken ct)
    {
        var attachments = await db.AppConfigGroups
            .Where(cg => cg.ConfigGroupId == groupId && !cg.HasUnpublishedChanges)
            .ToListAsync(ct);
        foreach (var a in attachments) a.HasUnpublishedChanges = true;
    }
}
