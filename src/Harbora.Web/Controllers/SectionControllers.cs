using Harbora.Application.Abstractions;
using Harbora.Data;
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
public sealed class DatabasesController : Controller
{
    public IActionResult Index() { ViewData["Title"] = "Databases"; return View("_Placeholder"); }
}

[Authorize]
public sealed class BackupsController : Controller
{
    public IActionResult Index() { ViewData["Title"] = "Backups"; return View("_Placeholder"); }
}

[Authorize]
public sealed class TemplatesController(HarboraDbContext db) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Templates";
        var templates = await db.AppTemplates.Where(t => t.IsEnabled)
            .OrderBy(t => t.Category).ThenBy(t => t.Name).ToListAsync(ct);
        return View(templates);
    }
}

[Authorize]
public sealed class MonitoringController(IDockerEngine docker, ILogger<MonitoringController> logger) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Monitoring";
        try
        {
            var containers = await docker.ListContainersAsync("harbora.managed", ct);
            return View(containers.ToList());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Docker unavailable for monitoring.");
            return View(new List<ContainerInfo>());
        }
    }
}

[Authorize]
public sealed class SettingsController(
    HarboraDbContext db,
    ITokenService tokens,
    ICurrentUser currentUser) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Settings";
        ViewBag.Tokens = await db.ApiTokens
            .Where(t => t.UserId == currentUser.UserId && !t.IsRevoked)
            .OrderByDescending(t => t.CreatedAt).ToListAsync(ct);
        var settings = await db.Settings.Where(s => !s.IsSecret).OrderBy(s => s.Key).ToListAsync(ct);
        return View(settings);
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
