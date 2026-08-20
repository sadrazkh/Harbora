using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Networking;
using Harbora.Domain.Status;
using Harbora.Infrastructure.Networking;
using Harbora.Infrastructure.Status;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

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
    HarboraDbContext db, ICurrentUser currentUser, AppAddressAssigner addresses, IAuditLogger audit,
    StatusPageDomainService domains, IConfiguration config) : Controller
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

        var rootDomain = await addresses.RootDomainAsync(ct);
        var publicHost = await PublicHostAsync(rootDomain, ct);

        var customDomain = await db.Domains
            .Where(d => d.StatusPageId == page.Id)
            .Select(d => new StatusPageCustomDomainRow(
                d.Host,
                d.Certificate != null ? d.Certificate.Status : null,
                d.Certificate != null ? d.Certificate.ExpiresAt : null))
            .FirstOrDefaultAsync(ct);

        return View(new StatusPageSettingsViewModel
        {
            IsEnabled = page.IsEnabled,
            PublicHost = publicHost,
            CustomDomain = customDomain,
            Components = components,
            AvailableApps = availableApps,
            Incidents = incidents
        });
    }

    /// <summary>The address this page would answer on once enabled — null when the platform has no
    /// root domain configured yet, the same "nothing to build a name under" state <c>AppAddress</c>
    /// already carries for ordinary apps.</summary>
    private async Task<string?> PublicHostAsync(string? rootDomain, CancellationToken ct)
    {
        var slug = await db.Workspaces.Where(w => w.Id == WorkspaceId).Select(w => w.Slug).FirstOrDefaultAsync(ct);
        return rootDomain is null || slug is null ? null : $"{ReservedHosts.StatusPagePrefix}{slug}.{rootDomain}";
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

        // Makes status-{slug}.<platform domain> genuinely reachable through Traefik — sub-project 7
        // built the anonymous route and the opt-in flag; nothing before this made production Traefik
        // forward that host to the panel. Skipped, not refused, when no platform root domain is
        // configured — the same state under which an app itself has no address to be reached at
        // either, so there is nothing this call could route yet.
        var rootDomain = await addresses.RootDomainAsync(ct);
        var publicHost = await PublicHostAsync(rootDomain, ct);
        if (publicHost is not null)
        {
            var result = await domains.EnsurePlatformRouteAsync(WorkspaceId, publicHost, ct);
            if (!result.Success)
            {
                // Never "Enabled" for an apply that did not actually reach Traefik — the same honesty
                // rule sub-project 5's maintenance toggle established: a flag reading "on" must mean
                // the route is really there, not merely that the database says so.
                page.IsEnabled = false;
                await db.SaveChangesAsync(ct);
                TempData["Error"] = IsFa
                    ? $"صفحه فعال نشد: تغییرات مسیریابی اعمال نشد ({result.Error})."
                    : $"Could not enable the page: the routing change did not apply ({result.Error}).";
                return RedirectToAction(nameof(Index));
            }
        }

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

        var rootDomain = await addresses.RootDomainAsync(ct);
        var publicHost = await PublicHostAsync(rootDomain, ct);
        if (publicHost is not null)
            await domains.RemovePlatformRouteAsync(WorkspaceId, publicHost, ct);

        await audit.LogAsync("statuspage.disabled", "StatusPage", page.Id.ToString(), ClientIp, ct: ct);
        return RedirectToAction(nameof(Index));
    }

    // ---- custom domain (sub-project 8, 2026-08-20 platform-options plan) ----

    /// <summary>The same reserved-host question <c>AppsController.IsReservedHost</c>/<c>AddDomain</c>
    /// ask a typed app domain — the platform's own names, and now also the "status-" prefix under the
    /// platform's own root domain, which is this feature's own reserved namespace and not a customer's
    /// to attach a second workspace's status page under.</summary>
    private async Task<string?> ReservedHostRefusalAsync(string host, CancellationToken ct)
    {
        var reserved = ReservedHosts.IsReserved(host, ReservedHosts.ForPlatform(
            config["PANEL_DOMAIN"], config["NodeAgent:PublicUrl"], config["Storage:S3:PublicEndpoint"]));
        var rootDomain = await addresses.RootDomainAsync(ct);
        var reservedPrefix = ReservedHosts.IsReservedPrefix(host, rootDomain);
        if (!reserved && !reservedPrefix) return null;

        return IsFa
            ? $"«{host}» یکی از نام‌های خودِ سامانه است و نمی‌توان آن را وصل کرد."
            : $"'{host}' is one of the platform's own host names and cannot be attached.";
    }

    [HttpPost("domain")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AlertsManage)]
    public async Task<IActionResult> AttachDomain(string host, CancellationToken ct)
    {
        host = (host ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(host))
        {
            TempData["Error"] = IsFa ? "یک دامنه وارد کنید." : "Enter a domain.";
            return RedirectToAction(nameof(Index));
        }

        if (await ReservedHostRefusalAsync(host, ct) is { } refusal)
        {
            TempData["Error"] = refusal;
            return RedirectToAction(nameof(Index));
        }

        if (await db.Domains.IgnoreQueryFilters().AnyAsync(d => d.Host == host, ct))
        {
            TempData["Error"] = IsFa ? $"«{host}» پیش‌تر استفاده شده است." : $"'{host}' is already in use.";
            return RedirectToAction(nameof(Index));
        }

        var page = await db.StatusPages.FirstOrDefaultAsync(p => p.WorkspaceId == WorkspaceId, ct);
        if (page is null)
        {
            page = new StatusPage { WorkspaceId = WorkspaceId, IsEnabled = false };
            db.StatusPages.Add(page);
            await db.SaveChangesAsync(ct);
        }

        if (await db.Domains.IgnoreQueryFilters().AnyAsync(d => d.StatusPageId == page.Id, ct))
        {
            TempData["Error"] = IsFa
                ? "این صفحه از قبل یک دامنهٔ اختصاصی دارد؛ ابتدا آن را حذف کنید."
                : "This page already has a custom domain; remove it first.";
            return RedirectToAction(nameof(Index));
        }

        var result = await domains.AttachCustomDomainAsync(WorkspaceId, page.Id, host, ct);
        if (!result.Success)
        {
            TempData["Error"] = IsFa
                ? $"دامنه وصل نشد: تغییرات مسیریابی اعمال نشد ({result.Error})."
                : $"Could not attach the domain: the routing change did not apply ({result.Error}).";
            return RedirectToAction(nameof(Index));
        }

        await audit.LogAsync("statuspage.domain.attached", "StatusPage", page.Id.ToString(), ClientIp, ct: ct);
        TempData["Message"] = IsFa
            ? "دامنه وصل شد. تا زمانی که DNS به این سرور اشاره کند، ممکن است چند دقیقه طول بکشد."
            : "Domain attached. It may take a few minutes until DNS points here.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("domain/remove")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AlertsManage)]
    public async Task<IActionResult> RemoveDomain(CancellationToken ct)
    {
        var page = await db.StatusPages.FirstOrDefaultAsync(p => p.WorkspaceId == WorkspaceId, ct);
        if (page is null) return RedirectToAction(nameof(Index));

        await domains.RemoveCustomDomainAsync(WorkspaceId, page.Id, ct);
        await audit.LogAsync("statuspage.domain.removed", "StatusPage", page.Id.ToString(), ClientIp, ct: ct);
        TempData["Message"] = IsFa ? "دامنه حذف شد." : "Domain removed.";
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
