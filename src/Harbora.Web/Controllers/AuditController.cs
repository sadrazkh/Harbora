using System.Text;
using Harbora.Data;
using Harbora.Domain.Auditing;
using Harbora.Domain.Authorization;
using Harbora.Web.Infrastructure;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// The audit trail (doc 10 §2.13, owed from P13). Entries have been written since the overhaul but
/// nothing could read them — an audit log nobody can inspect satisfies no one: not an incident
/// response, not a compliance review, not a user asking "who deleted my app?".
///
/// Restricted to platform administrators: the log spans every workspace and records actor emails
/// and IPs, so it is not ordinary tenant-visible data.
/// </summary>
[Authorize(Policy = Capabilities.PlatformManage)]
[Route("audit")]
public sealed class AuditController(HarboraDbContext db) : Controller
{
    private const int PageSize = 100;

    /// <summary>Hard cap on an export so a single click can't try to stream millions of rows.</summary>
    public const int MaxExportRows = 50_000;

    [HttpGet("")]
    public async Task<IActionResult> Index(string? action, string? actor, int page = 1, CancellationToken ct = default)
    {
        ViewData["Title"] = "Audit log";
        page = Math.Max(1, page);

        var query = Filter(action, actor);
        var total = await query.CountAsync(ct);

        var entries = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .AsNoTracking().ToListAsync(ct);

        return View(new AuditPageViewModel
        {
            Entries = entries,
            Page = page,
            PageSize = PageSize,
            TotalCount = total,
            ActionFilter = action,
            ActorFilter = actor,
            Actions = await db.AuditLogs.Select(a => a.Action).Distinct().OrderBy(a => a).ToListAsync(ct)
        });
    }

    /// <summary>CSV export of the current filter, so the trail can leave the box for review.</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export(string? action, string? actor, CancellationToken ct)
    {
        var entries = await Filter(action, actor)
            .OrderByDescending(a => a.CreatedAt)
            .Take(MaxExportRows)
            .AsNoTracking().ToListAsync(ct);

        var csv = new StringBuilder();
        csv.AppendLine("timestamp,actor,action,targetType,targetId,ipAddress,metadata");
        foreach (var e in entries)
            csv.AppendLine(CsvWriter.Row(
                e.CreatedAt.ToString("o"), e.ActorEmail, e.Action,
                e.TargetType, e.TargetId, e.IpAddress, e.MetadataJson));

        // UTF-8 BOM so Excel opens non-ASCII actor names correctly.
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        // Invariant calendar: this is a download filename, not something to localise.
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        return File(bytes, "text/csv", $"harbora-audit-{stamp}.csv");
    }

    private IQueryable<AuditLog> Filter(string? action, string? actor)
    {
        var query = db.AuditLogs.AsQueryable();
        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(a => a.Action == action);
        if (!string.IsNullOrWhiteSpace(actor))
            query = query.Where(a => a.ActorEmail.Contains(actor));
        return query;
    }

}
