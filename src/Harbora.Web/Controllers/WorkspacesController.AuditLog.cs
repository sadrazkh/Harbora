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

    [HttpGet("audit-log")]
    public async Task<IActionResult> AuditLog([FromQuery] int page, CancellationToken ct)
    {
        ViewData["Title"] = IsFa ? "لاگ رویدادها" : "Audit log";

        if (WorkspaceId == Guid.Empty) return Challenge();
        page = Math.Max(1, page);

        var query = db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.WorkspaceId == WorkspaceId);

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
            TotalCount = total
        });
    }
}
