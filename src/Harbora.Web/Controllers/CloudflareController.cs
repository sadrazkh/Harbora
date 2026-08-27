using Harbora.Application.Abstractions;
using Harbora.Domain.Authorization;
using Harbora.Infrastructure.Networking;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Harbora.Web.Controllers;

[Authorize(Policy = Capabilities.PlatformManage)]
[Route("admin/cloudflare")]
public sealed class CloudflareController(
    CloudflarePlatformService cloudflare,
    IDomainInspector domains,
    IAuditLogger audit) : Controller
{
    private bool IsFa =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Cloudflare";
        var state = await cloudflare.GetStateAsync(ct);
        DomainStatus? panel = null;
        if (!string.IsNullOrWhiteSpace(state.PanelDomain))
            panel = await domains.InspectAsync(state.PanelDomain, ct);

        return View(new CloudflareSettingsViewModel
        {
            Enabled = state.Enabled,
            HasToken = state.HasToken,
            Zone = state.Zone,
            LastVerifiedAt = state.LastVerifiedAt,
            PanelDomain = state.PanelDomain,
            RootDomain = state.RootDomain,
            S3Domain = state.S3Domain,
            PanelStatus = panel
        });
    }

    [HttpPost("test")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Test(string? token, string zone, CancellationToken ct)
    {
        var result = await cloudflare.TestAsync(token, zone, ct);
        TempData[result.Success ? "Message" : "Error"] = result.Message;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("enable")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Enable(
        string? token, string zone, bool proxyRecords, CancellationToken ct)
    {
        var result = await cloudflare.EnableAsync(token, zone, proxyRecords, ct);
        TempData[result.Success ? "Message" : "Error"] = result.Message;
        if (result.Warnings.Count > 0)
            TempData["Warnings"] = string.Join("\n", result.Warnings);

        if (result.Success)
            await audit.LogAsync("platform.cloudflare_enabled", "setting", zone,
                HttpContext.Connection.RemoteIpAddress?.ToString(), workspaceId: null, ct: ct);

        return RedirectToAction(nameof(Index));
    }
}
