using Harbora.Domain.Identity;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// The customer's own side of support access.
///
/// <para>
/// The banner tells them while it is happening; this is where they can look afterwards, and where
/// they can look for the sessions they were not at the keyboard for. It lists every support session
/// ever opened against this workspace — who, when, why, how it ended — and, under each, the audited
/// acts performed while it ran.
/// </para>
///
/// <para>
/// This partially answers backlog HARBORA-0056, "what a workspace operator sees in the audit log".
/// It is not the whole answer: <c>AuditLog</c> has no workspace column, so the trail as a whole
/// still cannot be scoped to one tenant. What it can be scoped by is the support session itself,
/// which is a workspace's own row — so the slice a customer most needs, and the only slice that
/// records somebody else acting as them, is answerable today. The rest of HARBORA-0056 needs a
/// workspace on the audit row.
/// </para>
///
/// <para>
/// Open to every member of the workspace rather than to its admins. The page names support staff
/// and the reasons they gave, and nothing else — no other tenant's rows can reach it — and "somebody
/// from the platform was inside this workspace" is not news an owner should be able to keep from the
/// people working in it.
/// </para>
/// </summary>
public sealed partial class WorkspacesController
{
    /// <summary>How far back the page goes. A year of hours is far more than anyone has ever used.</summary>
    private const int MaxSessions = 200;

    [HttpGet("support-access")]
    public async Task<IActionResult> SupportAccess(CancellationToken ct)
    {
        ViewData["Title"] = IsFa ? "دسترسی پشتیبانی" : "Support access";

        if (WorkspaceId == Guid.Empty) return Challenge();

        // Explicitly by workspace. SupportSessions carries no global filter — see the remark on
        // HarboraDbContext.SupportSessions — so this comparison is the only thing standing between
        // one customer's support history and another's.
        var sessions = await supportSessions.ForWorkspaceAsync(WorkspaceId, MaxSessions, ct);

        // The acts, fetched for these sessions only. A support session's id is a workspace's own
        // property, so keying on it inherits the scoping above rather than needing its own.
        var ids = sessions.Select(s => s.Id).ToList();
        var acts = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.SupportSessionId != null && ids.Contains(a.SupportSessionId.Value))
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);

        var now = clock.UtcNow;
        return View(new WorkspaceSupportAccessViewModel
        {
            WorkspaceId = WorkspaceId,
            Sessions = sessions.Select(s => new WorkspaceSupportSessionRow(
                s.Id, s.AdminEmail, s.Reason, s.StartedAt, s.ExpiresAt, s.EndedAt, s.EndedBy,
                s.IsLiveAt(now))
            {
                Acts = acts.Where(a => a.SupportSessionId == s.Id)
                    .Select(a => new WorkspaceSupportActRow(
                        a.CreatedAt, a.Action, a.TargetType, a.TargetId))
                    .ToList()
            }).ToList()
        });
    }
}
