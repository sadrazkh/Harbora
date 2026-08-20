using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Networking;
using Harbora.Domain.Status;
using Harbora.Infrastructure.Networking;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Workspace settings for the public status page (P7, 2026-08-20 platform-options plan) — opt-in,
/// which apps appear and under what name, and the manual incident log. Gated by
/// <see cref="Capabilities.AlertsManage"/>, the same Admin-level capability
/// <c>EventSubscriptionsController</c> reuses for the freshest precedent of a workspace-level
/// settings screen beside the alert machinery — read access follows the base authenticated policy,
/// same as that controller's own Index.
///
/// <para>
/// This controller never touches the anonymous route's data path. It writes <see cref="StatusPage"/>,
/// <see cref="StatusPageComponent"/> and <see cref="StatusIncident"/> rows under the caller's own
/// ambient workspace scope (the normal, filtered <c>HarboraDbContext</c>) — <c>StatusPageReport</c> is
/// the only place those tables are read back for a stranger.
/// </para>
/// </summary>
[Authorize]
[Route("status-page")]
public sealed class StatusPageSettingsController(
    HarboraDbContext db, ICurrentUser currentUser, AppAddressAssigner addresses, IAuditLogger audit) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private bool IsFa => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Status page";

        // Created lazily, disabled, the first time anyone opens this screen — its mere existence is
        // not publication (StatusPage's own doc). Every later action below can then assume the row
        // is there rather than defending against its absence a second way.
        var page = await db.StatusPages.FirstOrDefaultAsync(p => p.WorkspaceId == WorkspaceId, ct);
        if (page is null)
        {
            page = new StatusPage { WorkspaceId = WorkspaceId, IsEnabled = false };
            db.StatusPages.Add(page);
            await db.SaveChangesAsync(ct);
        }

        var components = await db.StatusPageComponents
            .Where(c => c.StatusPageId == page.Id)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.CreatedAt)
            .Join(db.Apps, c => c.AppId, a => a.Id,
                (c, a) => new StatusPageComponentRow(c.Id, a.Id, a.Name, c.DisplayName, c.SortOrder))
            .ToListAsync(ct);

        var chosenAppIds = components.Select(c => c.AppId).ToHashSet();
        var availableApps = await db.Apps.Where(a => a.WorkspaceId == WorkspaceId)
            .OrderBy(a => a.Name)
            .Select(a => new StatusPageAvailableAppRow(a.Id, a.Name))
            .ToListAsync(ct);
        availableApps = availableApps.Where(a => !chosenAppIds.Contains(a.Id)).ToList();

        var incidents = await db.StatusIncidents
            .Where(i => i.StatusPageId == page.Id)
            .OrderByDescending(i => i.StartedAt)
            .Select(i => new StatusPageIncidentRow(i.Id, i.TitleEn, i.TitleFa, i.BodyEn, i.BodyFa, i.StartedAt, i.ResolvedAt))
            .ToListAsync(ct);

        var slug = await db.Workspaces.Where(w => w.Id == WorkspaceId).Select(w => w.Slug).FirstOrDefaultAsync(ct);
        var rootDomain = await addresses.RootDomainAsync(ct);
        var publicHost = rootDomain is null || slug is null ? null : $"{ReservedHosts.StatusPagePrefix}{slug}.{rootDomain}";

        return View(new StatusPageSettingsViewModel
        {
            IsEnabled = page.IsEnabled,
            PublicHost = publicHost,
            Components = components,
            AvailableApps = availableApps,
            Incidents = incidents
        });
    }

    [HttpPost("enable")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AlertsManage)]
    public async Task<IActionResult> Enable(CancellationToken ct)
    {
        var page = await db.StatusPages.FirstOrDefaultAsync(p => p.WorkspaceId == WorkspaceId, ct);
        if (page is null)
        {
            page = new StatusPage { WorkspaceId = WorkspaceId, IsEnabled = true };
            db.StatusPages.Add(page);
        }
        else
        {
            page.IsEnabled = true;
        }
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("statuspage.enabled", "StatusPage", page.Id.ToString(), ClientIp, ct: ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("disable")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AlertsManage)]
    public async Task<IActionResult> Disable(CancellationToken ct)
    {
        var page = await db.StatusPages.FirstOrDefaultAsync(p => p.WorkspaceId == WorkspaceId, ct);
        if (page is null) return RedirectToAction(nameof(Index));

        page.IsEnabled = false;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("statuspage.disabled", "StatusPage", page.Id.ToString(), ClientIp, ct: ct);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Adds an app under the name the customer types here — never the app's real slug — which is
    /// exactly what <see cref="StatusPageComponent.DisplayName"/> exists for and the only string
    /// <c>StatusPageReport</c> will ever hand the public page for this app.
    /// </summary>
    [HttpPost("components")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AlertsManage)]
    public async Task<IActionResult> AddComponent(Guid appId, string displayName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            TempData["Error"] = IsFa ? "یک نام برای این سرویس وارد کنید." : "Give this component a name.";
            return RedirectToAction(nameof(Index));
        }

        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == appId && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        var page = await db.StatusPages.FirstOrDefaultAsync(p => p.WorkspaceId == WorkspaceId, ct);
        if (page is null)
        {
            page = new StatusPage { WorkspaceId = WorkspaceId, IsEnabled = false };
            db.StatusPages.Add(page);
            await db.SaveChangesAsync(ct);
        }

        var alreadyChosen = await db.StatusPageComponents
            .AnyAsync(c => c.StatusPageId == page.Id && c.AppId == appId, ct);
        if (alreadyChosen)
        {
            TempData["Error"] = IsFa ? "این سرویس قبلاً اضافه شده است." : "That app is already on the page.";
            return RedirectToAction(nameof(Index));
        }

        var nextOrder = await db.StatusPageComponents.Where(c => c.StatusPageId == page.Id)
            .Select(c => (int?)c.SortOrder).MaxAsync(ct) ?? -1;

        db.StatusPageComponents.Add(new StatusPageComponent
        {
            WorkspaceId = WorkspaceId, StatusPageId = page.Id, AppId = appId,
            DisplayName = displayName.Trim(), SortOrder = nextOrder + 1
        });
        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("components/{id:guid}/remove")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AlertsManage)]
    public async Task<IActionResult> RemoveComponent(Guid id, CancellationToken ct)
    {
        var component = await db.StatusPageComponents
            .FirstOrDefaultAsync(c => c.Id == id && c.WorkspaceId == WorkspaceId, ct);
        if (component is null) return NotFound();

        db.StatusPageComponents.Remove(component);
        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Both languages are required — the plan's own rule for anything a status page's own
    /// visitors are meant to read (mirrors Announcement's fa+en-both-required decision, sub-project 4).</summary>
    [HttpPost("incidents")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AlertsManage)]
    public async Task<IActionResult> PostIncident(
        string titleEn, string titleFa, string? bodyEn, string? bodyFa, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(titleEn) || string.IsNullOrWhiteSpace(titleFa))
        {
            TempData["Error"] = IsFa
                ? "عنوان رخداد باید به هر دو زبان وارد شود."
                : "The incident title is required in both languages.";
            return RedirectToAction(nameof(Index));
        }

        var page = await db.StatusPages.FirstOrDefaultAsync(p => p.WorkspaceId == WorkspaceId, ct);
        if (page is null) return RedirectToAction(nameof(Index));

        db.StatusIncidents.Add(new StatusIncident
        {
            WorkspaceId = WorkspaceId, StatusPageId = page.Id,
            TitleEn = titleEn.Trim(), TitleFa = titleFa.Trim(),
            BodyEn = string.IsNullOrWhiteSpace(bodyEn) ? null : bodyEn.Trim(),
            BodyFa = string.IsNullOrWhiteSpace(bodyFa) ? null : bodyFa.Trim(),
            StartedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("incidents/{id:guid}/resolve")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AlertsManage)]
    public async Task<IActionResult> ResolveIncident(Guid id, CancellationToken ct)
    {
        var incident = await db.StatusIncidents.FirstOrDefaultAsync(i => i.Id == id && i.WorkspaceId == WorkspaceId, ct);
        if (incident is null) return NotFound();

        incident.ResolvedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index));
    }
}
