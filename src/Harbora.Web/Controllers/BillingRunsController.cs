using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Billing;
using Harbora.Domain.Jobs;
using Harbora.Infrastructure.Billing;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Web.Controllers;

/// <summary>Provider-only visibility and controlled retry for durable hourly accounting runs.</summary>
[Authorize(Policy = Capabilities.TenantsManage)]
[Route("billing-runs")]
public sealed class BillingRunsController(
    HarboraDbContext db,
    BillingRunRetryService retries,
    IAuditLogger audit,
    IOptions<BillingOptions> billing) : Controller
{
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Billing runs";
        var runs = await db.BillingRuns.AsNoTracking()
            .OrderByDescending(r => r.BillingHour)
            .Take(200)
            .ToListAsync(ct);
        var runIds = runs.Select(r => r.Id).ToList();
        var liveRunIds = await db.Jobs.AsNoTracking()
            .Where(j => j.Kind == JobKind.BillingHour && runIds.Contains(j.TargetId)
                        && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running))
            .Select(j => j.TargetId)
            .Distinct()
            .ToListAsync(ct);
        var live = liveRunIds.ToHashSet();
        var enabled = billing.Value.Enabled;

        return View(new BillingRunsPageViewModel
        {
            BillingEnabled = enabled,
            Runs = runs.Select(r => new BillingRunAdminRow(
                r.Id, r.BillingHour, r.Status, r.Attempts, r.WorkspacesCharged,
                r.LinesWritten, r.WorkspacesSuspended, r.StartedAt, r.CompletedAt,
                r.FailureSummary, live.Contains(r.Id),
                enabled && r.Status != BillingRunStatus.Succeeded && !live.Contains(r.Id))).ToList()
        });
    }

    [HttpPost("{id:guid}/retry")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
    {
        if (!billing.Value.Enabled)
        {
            TempData["Error"] = "Enable billing before retrying an accounting run.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var result = await retries.RetryAsync(id, ct);
            TempData["Message"] = result.AlreadyQueued
                ? "This billing run is already queued or running."
                : "Billing run queued for retry.";
            await audit.LogAsync("billing.run.retry", "billing_run", id.ToString(), ClientIp,
                metadataJson: JsonSerializer.Serialize(new { result.Queued, result.AlreadyQueued }), ct: ct);
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }
}
