using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Ai;
using Harbora.Infrastructure.Ai;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// The customer's own view of the AI service: what their plan includes, what they have used, and
/// the keys they hold.
///
/// Deliberately shows no prompts or responses. The usage table records metadata only — see
/// AiUsageRecord for why — so this page can show a person what they spent without becoming a place
/// where their customers' data can be read.
/// </summary>
[Authorize]
[Route("ai")]
public sealed class AiController(
    HarboraDbContext db,
    ICurrentUser currentUser,
    IAuditLogger audit,
    ISystemClock clock) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "AI";

        var subscription = await db.AiSubscriptions
            .Include(s => s.AiPlan).ThenInclude(p => p!.Models)
            .FirstOrDefaultAsync(s => s.WorkspaceId == WorkspaceId && s.IsActive, ct);

        var vm = new AiOverviewViewModel { Subscription = subscription, Plan = subscription?.AiPlan };

        if (subscription?.AiPlan is not null)
        {
            var all = await db.AiModels.Where(m => m.IsEnabled).ToListAsync(ct);
            vm.Models = AiPlanAccess.ModelsFor(subscription.AiPlan, all);
        }

        vm.Keys = await db.AiUserApiKeys
            .Where(k => k.WorkspaceId == WorkspaceId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);

        // Recent requests, metadata only. Ordered newest first because the question people bring to
        // this page is almost always "what just happened".
        vm.Recent = await db.AiUsageRecords
            .Where(u => u.WorkspaceId == WorkspaceId)
            .OrderByDescending(u => u.CreatedAt)
            .Take(25)
            .ToListAsync(ct);

        var since = clock.UtcNow.AddDays(-30);
        vm.RequestsThisPeriod = await db.AiUsageRecords
            .CountAsync(u => u.WorkspaceId == WorkspaceId && u.CreatedAt >= since, ct);

        // The address a customer points their client at — built from the request so it is right
        // whatever hostname this installation answers on.
        vm.EndpointUrl = $"{Request.Scheme}://{Request.Host}/v1";

        return View(vm);
    }

    /// <summary>
    /// Issues a key. The secret travels back once, in TempData, and is never stored in a form a
    /// later page could render — a key readable from the panel is one a support screenshot leaks.
    /// </summary>
    [HttpPost("keys")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateKey(string label, CancellationToken ct)
    {
        var subscription = await db.AiSubscriptions
            .FirstOrDefaultAsync(s => s.WorkspaceId == WorkspaceId && s.IsActive, ct);

        if (subscription is null)
        {
            TempData["Error"] = "This workspace has no active AI subscription.";
            return RedirectToAction(nameof(Index));
        }

        var issued = AiApiKeys.Create();

        db.AiUserApiKeys.Add(new AiUserApiKey
        {
            WorkspaceId = WorkspaceId,
            UserId = currentUser.UserId ?? Guid.Empty,
            Label = string.IsNullOrWhiteSpace(label) ? "API key" : label.Trim(),
            Prefix = issued.Prefix,
            KeyHash = issued.Hash
        });

        await db.SaveChangesAsync(ct);
        await audit.LogAsync("ai.key_created", "ai_key", issued.Prefix, ClientIp, ct: ct);

        TempData["NewAiKey"] = issued.Secret;
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Revokes a key. The row stays: deleting it would take its usage history with it, and "which
    /// key ran up this bill" is exactly the question asked after a key leaks.
    /// </summary>
    [HttpPost("keys/{id:guid}/revoke")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeKey(Guid id, CancellationToken ct)
    {
        var key = await db.AiUserApiKeys
            .FirstOrDefaultAsync(k => k.Id == id && k.WorkspaceId == WorkspaceId, ct);
        if (key is null) return NotFound();

        key.IsRevoked = true;
        key.RevokedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("ai.key_revoked", "ai_key", key.Prefix, ClientIp, ct: ct);

        TempData["Message"] = $"{key.Label} was revoked. Requests using it will now be refused.";
        return RedirectToAction(nameof(Index));
    }
}
