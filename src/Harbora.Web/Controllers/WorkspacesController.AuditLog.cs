using System.Globalization;
using Harbora.Domain.Auditing;
using Harbora.Web.Infrastructure;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// The rest of HARBORA-0056: a workspace operator's own slice of the audit trail, next to but
/// separate from the support-session history <see cref="WorkspacesController.SupportAccess"/> already
/// answers.
///
/// <para>
/// <b>The decision, recorded:</b> two views, not one. A platform administrator keeps the existing
/// whole-platform reader (<c>AuditController</c>, unchanged) because the trail spans every workspace
/// and names actor emails and IPs that are not ordinary tenant-visible data. A workspace operator gets
/// this page instead — their own workspace's rows, and nothing else — rather than either being locked
/// out of the audit log entirely or handed the platform-wide one. Doc 18's open question is closed:
/// scoped, not provider-only.
/// </para>
///
/// <para>
/// <b>The gap this page cannot close, and says so:</b> <c>AuditLog.WorkspaceId</c> is null for two
/// different reasons — a row written before the column existed (no backfill was attempted; a guessed
/// workspace is worse than an admitted gap), and a row for an action that is platform-level by nature
/// (a platform setting, a node enrollment, a sign-in before any workspace is chosen). Both are
/// legitimately absent from this list, not hidden and not misattributed, and the page says as much
/// rather than reading as a complete history it is not.
/// </para>
///
/// <para>
/// <b>Scoping, explicitly:</b> <c>AuditLog</c> carries no ambient query filter at all (see
/// <c>HarboraDbContext</c>'s remark on <c>AuditLog.WorkspaceId</c> — most rows are workspace-less by
/// nature, so an "own it or nothing" filter would misattribute every one of them to whichever
/// workspace happened to be ambient). <see cref="IgnoreQueryFilters"/> is called defensively for the
/// same reason <see cref="WorkspacesController.SupportAccess"/> calls it on <c>AuditLogs</c>, but the
/// only thing actually isolating one workspace's rows from another's — and from the platform-level and
/// pre-column rows — is the explicit <c>WorkspaceId ==</c> comparison below.
/// </para>
/// </summary>
public sealed partial class WorkspacesController
{
    private const int AuditLogPageSize = 50;

    /// <summary>
    /// Same hard cap the platform-wide export uses (<see cref="AuditController.MaxExportRows"/>): one
    /// bound, so a workspace with an unusually long history cannot be exported into an unbounded
    /// in-memory list any more than the whole-platform trail can.
    /// </summary>
    private const int AuditLogExportMaxRows = AuditController.MaxExportRows;

    /// <summary>
    /// The workspace's own slice of the audit trail, scoped and ordered exactly as the page shows it.
    ///
    /// Shared by <see cref="AuditLog"/> and both export actions below so the export can never drift
    /// from what the page displays: today that is only the workspace filter (this page carries no
    /// actor/action/date filter of its own — <c>WorkspaceViewModels.WorkspaceAuditLogViewModel</c> has
    /// no such fields, and neither this controller nor any script under <c>wwwroot</c> narrows the
    /// query further). If a filter is ever added to the page, adding it here is the only change an
    /// export needs to keep matching it.
    /// </summary>
    private IQueryable<AuditLog> WorkspaceAuditLogQuery() =>
        db.AuditLogs.IgnoreQueryFilters().AsNoTracking().Where(a => a.WorkspaceId == WorkspaceId);

    [HttpGet("audit-log")]
    public async Task<IActionResult> AuditLog([FromQuery] int page, CancellationToken ct)
    {
        ViewData["Title"] = IsFa ? "لاگ رویدادها" : "Audit log";

        if (WorkspaceId == Guid.Empty) return Challenge();
        page = Math.Max(1, page);

        var query = WorkspaceAuditLogQuery();

        var total = await query.CountAsync(ct);
        var entries = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip((page - 1) * AuditLogPageSize).Take(AuditLogPageSize)
            .Select(a => new WorkspaceAuditLogRow(
                a.Id, a.CreatedAt, a.ActorEmail, a.Action, a.TargetType, a.TargetId, a.IpAddress))
            .ToListAsync(ct);

        return View(new WorkspaceAuditLogViewModel
        {
            WorkspaceId = WorkspaceId,
            Entries = entries,
            Page = page,
            PageSize = AuditLogPageSize,
            TotalCount = total,
            ExportMaxRows = AuditLogExportMaxRows
        });
    }

    /// <summary>
    /// CSV export of exactly the rows <see cref="AuditLog"/> would list, not just the page on screen:
    /// same capability check (none beyond an active workspace session — the page has none either),
    /// same <see cref="WorkspaceAuditLogQuery"/>, but unpaginated up to <see cref="AuditLogExportMaxRows"/>
    /// rather than the 50-row page window. A truncated file says so inside itself
    /// (<see cref="AuditExportWriter"/>) rather than silently ending short of what it claims.
    /// </summary>
    [HttpGet("audit-log/export.csv")]
    public async Task<IActionResult> AuditLogExportCsv(CancellationToken ct)
    {
        if (WorkspaceId == Guid.Empty) return Challenge();

        var (entries, total) = await LoadExportRowsAsync(ct);
        var bytes = AuditExportWriter.Csv(entries, total, AuditLogExportMaxRows);
        return File(bytes, "text/csv", ExportFileName("csv"));
    }

    /// <summary>JSON twin of <see cref="AuditLogExportCsv"/> — same rows, same bound, same truncation flag.</summary>
    [HttpGet("audit-log/export.json")]
    public async Task<IActionResult> AuditLogExportJson(CancellationToken ct)
    {
        if (WorkspaceId == Guid.Empty) return Challenge();

        var (entries, total) = await LoadExportRowsAsync(ct);
        var bytes = AuditExportWriter.Json(entries, total, AuditLogExportMaxRows);
        return File(bytes, "application/json", ExportFileName("json"));
    }

    private async Task<(List<AuditLog> Entries, int Total)> LoadExportRowsAsync(CancellationToken ct)
    {
        var query = WorkspaceAuditLogQuery();
        var total = await query.CountAsync(ct);
        var entries = await query
            .OrderByDescending(a => a.CreatedAt)
            .Take(AuditLogExportMaxRows)
            .ToListAsync(ct);
        return (entries, total);
    }

    // Invariant calendar and culture: this is a download filename, not something to localise.
    private static string ExportFileName(string extension) =>
        $"harbora-workspace-audit-{DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.{extension}";
}
