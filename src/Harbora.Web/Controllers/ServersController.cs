using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Servers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Manage server nodes. The local node runs the in-process engine; remote nodes run the Harbora
/// Agent and are reached over HTTP with a bearer token (stored encrypted). "Test" pings the agent.
/// </summary>
[Authorize]
[Route("servers")]
public sealed class ServersController(
    HarboraDbContext db,
    IServerEngineFactory engineFactory,
    ISecretProtector protector,
    Harbora.Application.Abstractions.ISystemClock clock) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Servers";
        var servers = await db.Servers.OrderByDescending(s => s.IsLocal).ThenBy(s => s.Name).ToListAsync(ct);
        return View(servers);
    }

    [HttpPost("add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(string name, string agentEndpoint, string token, CancellationToken ct)
    {
        var host = Uri.TryCreate(agentEndpoint, UriKind.Absolute, out var uri) ? uri.Host : agentEndpoint;
        var server = new Server
        {
            Name = string.IsNullOrWhiteSpace(name) ? host : name,
            Hostname = host,
            IsLocal = false,
            AgentEndpoint = agentEndpoint.TrimEnd('/'),
            AgentTokenHash = string.IsNullOrWhiteSpace(token) ? null : protector.Protect(token),
            Status = ServerStatus.Unknown
        };
        db.Servers.Add(server);
        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Test), new { id = server.Id });
    }

    [HttpGet("{id:guid}/test")]
    public async Task<IActionResult> Test(Guid id, CancellationToken ct)
    {
        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (server is null) return NotFound();

        try
        {
            var engine = await engineFactory.ResolveAsync(id, ct);
            var host = await engine.GetHostInfoAsync(ct);
            server.Status = ServerStatus.Online;
            server.CpuCores = host.CpuCores;
            server.TotalMemoryBytes = host.TotalMemoryBytes;
            server.TotalDiskBytes = host.TotalDiskBytes;
            server.DockerVersion = host.DockerVersion;
            server.LastHeartbeatAt = clock.UtcNow;
            TempData["Message"] = $"{server.Name} is online (Docker {host.DockerVersion}).";
        }
        catch (Exception ex)
        {
            server.Status = ServerStatus.Offline;
            TempData["Error"] = $"Could not reach {server.Name}: {ex.Message}";
        }
        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (server is null || server.IsLocal) { TempData["Error"] = "The local server cannot be removed."; return RedirectToAction(nameof(Index)); }

        if (await db.Apps.AnyAsync(a => a.ServerId == id, ct) || await db.ManagedServices.AnyAsync(s => s.ServerId == id, ct))
        {
            TempData["Error"] = "Move or delete this node's apps and services first.";
            return RedirectToAction(nameof(Index));
        }
        db.Servers.Remove(server);
        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index));
    }
}
