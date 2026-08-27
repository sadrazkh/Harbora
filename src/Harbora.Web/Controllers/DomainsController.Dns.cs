using Harbora.Domain.Authorization;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Harbora.Web.Controllers;

/// <summary>
/// The workspace's own bring-your-own Cloudflare token (F9, 2026-08-21 functions-and-services plan,
/// decision 5): add/remove the token, and list/add/delete A, AAAA, CNAME, TXT and MX records for
/// whatever zones it can see. v1 only — no zone creation, no DNSSEC, no bulk import; running
/// authoritative DNS ourselves stays deferred.
///
/// <para>
/// Reachable from the Domains page's own summary card, but kept on its own route: listing zones and
/// records means a live call to Cloudflare, and the Domains index above must not pay that cost (or
/// depend on Cloudflare answering) on every page load.
/// </para>
/// </summary>
public sealed partial class DomainsController
{
    [HttpGet("dns")]
    public async Task<IActionResult> Dns(string? zone, CancellationToken ct)
    {
        ViewData["Title"] = "DNS";
        var state = await customerDns.GetStateAsync(WorkspaceId, ct);

        if (!state.HasToken)
        {
            return View(new CustomerDnsPageViewModel
            {
                HasToken = false,
                LastVerifiedAt = null,
                LastVerificationError = null,
                Zones = [],
                ZonesError = null
            });
        }

        var zones = await customerDns.ListZonesAsync(WorkspaceId, ct);
        var model = new CustomerDnsPageViewModel
        {
            HasToken = true,
            LastVerifiedAt = state.LastVerifiedAt,
            LastVerificationError = state.LastVerificationError,
            Zones = zones.Zones.Select(z => new CustomerDnsZoneRow(z.Id, z.Name)).ToList(),
            ZonesError = zones.Success ? null : zones.Error
        };

        if (zones.Success && !string.IsNullOrWhiteSpace(zone))
        {
            var picked = zones.Zones.FirstOrDefault(z => z.Id == zone);
            if (picked is not null)
            {
                var records = await customerDns.ListRecordsAsync(WorkspaceId, zone, ct);
                model = model with
                {
                    SelectedZoneId = picked.Id,
                    SelectedZoneName = picked.Name,
                    Records = records.Success
                        ? records.Records.Select(r =>
                            new CustomerDnsRecordRow(r.Id, r.Type, r.Name, r.Content, r.Ttl, r.Priority, r.Proxied))
                            .ToList()
                        : null,
                    RecordsError = records.Success ? null : records.Error
                };
            }
        }

        return View(model);
    }

    [HttpPost("dns/token")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> SaveDnsToken(string? token, CancellationToken ct)
    {
        var result = await customerDns.SaveTokenAsync(WorkspaceId, token, ct);
        TempData[result.Success ? "Message" : "Error"] = result.Message;
        if (result.Success)
            await audit.LogAsync("domains.dns_token_saved", "workspace", WorkspaceId.ToString(), ClientIp, workspaceId: WorkspaceId, ct: ct);
        return RedirectToAction(nameof(Dns));
    }

    [HttpPost("dns/token/remove")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> RemoveDnsToken(CancellationToken ct)
    {
        var result = await customerDns.RemoveTokenAsync(WorkspaceId, ct);
        TempData[result.Success ? "Message" : "Error"] = result.Message;
        if (result.Success)
            await audit.LogAsync("domains.dns_token_removed", "workspace", WorkspaceId.ToString(), ClientIp, workspaceId: WorkspaceId, ct: ct);
        return RedirectToAction(nameof(Dns));
    }

    [HttpPost("dns/records")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> CreateDnsRecord(
        string zone, string type, string name, string content, int ttl, int? priority, CancellationToken ct)
    {
        var result = await customerDns.CreateRecordAsync(WorkspaceId, zone, type, name, content, ttl, priority, ct);
        TempData[result.Success ? "Message" : "Error"] = result.Message;
        if (result.Success)
            await audit.LogAsync("domains.dns_record_created", "zone", zone, ClientIp, workspaceId: WorkspaceId, ct: ct);
        return RedirectToAction(nameof(Dns), new { zone });
    }

    [HttpPost("dns/records/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> DeleteDnsRecord(string zone, string recordId, CancellationToken ct)
    {
        var result = await customerDns.DeleteRecordAsync(WorkspaceId, zone, recordId, ct);
        TempData[result.Success ? "Message" : "Error"] = result.Message;
        if (result.Success)
            await audit.LogAsync("domains.dns_record_deleted", "zone", $"{zone}:{recordId}", ClientIp, workspaceId: WorkspaceId, ct: ct);
        return RedirectToAction(nameof(Dns), new { zone });
    }

    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
}
