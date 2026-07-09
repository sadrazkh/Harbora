using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Central view of every domain across the workspace's apps, with the DNS record each needs and
/// on-demand DNS + SSL health tests (so a customer can self-diagnose "why isn't my domain working").
/// </summary>
[Authorize]
[Route("domains")]
public sealed class DomainsController(
    HarboraDbContext db,
    ICurrentUser currentUser,
    IHttpClientFactory httpFactory) : Controller
{
    private static string? _publicIp;
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Domains";
        var rows = await db.Domains.Where(d => d.App!.WorkspaceId == WorkspaceId)
            .OrderBy(d => d.Host)
            .Select(d => new DomainRow(d.Id, d.Host, d.App!.Id, d.App.Name, d.SslEnabled, d.IsPrimary))
            .ToListAsync(ct);
        ViewBag.ServerIp = await PublicIpAsync(ct);
        return View(rows);
    }

    [HttpGet("test-dns")]
    public async Task<IActionResult> TestDns(string host, CancellationToken ct)
    {
        if (!await OwnsHostAsync(host, ct)) return NotFound();
        var expected = await PublicIpAsync(ct);
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host, ct);
            var ips = addrs.Select(a => a.ToString()).Distinct().ToList();
            return Json(new { resolved = ips, expected, ok = ips.Contains(expected) });
        }
        catch
        {
            return Json(new { resolved = Array.Empty<string>(), expected, ok = false, error = "not resolvable" });
        }
    }

    [HttpGet("test-ssl")]
    public async Task<IActionResult> TestSsl(string host, CancellationToken ct)
    {
        if (!await OwnsHostAsync(host, ct)) return NotFound();

        X509Certificate2? cert = null;
        var valid = false;
        var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, c, _, errors) =>
        {
            if (c is not null) cert = c as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(c.GetRawCertData());
            valid = errors == SslPolicyErrors.None;
            return true; // capture even invalid certs so we can report why
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        try
        {
            using var res = await client.GetAsync($"https://{host}/", HttpCompletionOption.ResponseHeadersRead, ct);
            return Json(new
            {
                ok = valid,
                status = (int)res.StatusCode,
                issuer = cert?.Issuer,
                expires = cert?.NotAfter.ToString("yyyy-MM-dd")
            });
        }
        catch (Exception ex)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    private Task<bool> OwnsHostAsync(string host, CancellationToken ct) =>
        db.Domains.AnyAsync(d => d.Host == host && d.App!.WorkspaceId == WorkspaceId, ct);

    private async Task<string> PublicIpAsync(CancellationToken ct)
    {
        if (_publicIp is not null) return _publicIp;
        try
        {
            var client = httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            _publicIp = (await client.GetStringAsync("https://api.ipify.org", ct)).Trim();
        }
        catch { _publicIp = "?"; }
        return _publicIp;
    }
}
