using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Jobs;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Jobs;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Web.Controllers;

/// <summary>
/// Every durable job this workspace owns, on one page (P5, 2026-08-17 app-environment-management
/// design). The audit calls this the biggest systemic UX gap: twelve job kinds, eleven enqueue
/// sites, a queue-position rule already written bilingually for nine of them — and no page anywhere
/// listed a single row.
///
/// <para>
/// <b>Scoping (§7 Q3(a)).</b> <c>Job</c> carries no query filter and never will — it is one of the
/// deliberately unfiltered platform tables (<c>HarboraDbContext.ApplyWorkspaceFilters</c>), because a
/// billing tick and a handful of pre-login emails belong to nobody in particular. Rather than adding
/// a filter that would either miss those rows or, worse, leak them into every tenant at once (a null
/// <c>WorkspaceId</c> matching an "own it or nothing" filter for everybody), this controller filters
/// by hand on the denormalised <c>Job.WorkspaceId</c> the enqueue sites now stamp — the same pattern
/// <c>NotificationsController</c> already uses for <c>UserNotification</c>, an equally unfiltered,
/// equally hand-keyed table. <c>DeploymentsController</c>'s own platform-wide read of <c>Jobs</c> (for
/// queue position) is untouched: there is still no global filter for it to interact with.
/// </para>
///
/// <para>
/// Follows <c>AuditController</c>'s filter/paging idiom, the way sub-project N3 already did for
/// <c>/notifications</c> — a query-string filter, a page size, a total count and a two-button pager —
/// so the three pages agree rather than a third list style being invented here.
/// </para>
/// </summary>
[Authorize]
[Route("activity")]
public sealed class ActivityController(
    HarboraDbContext db,
    ICurrentUser currentUser,
    IJobQueue jobs,
    IOptions<JobQueueOptions> jobQueueOptions,
    ISystemClock clock,
    IAuditLogger audit) : Controller
{
    private const int PageSize = 50;

    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private static bool IsFa =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    [HttpGet("")]
    public async Task<IActionResult> Index(
        [FromQuery] JobKind? kind, [FromQuery] JobStatus? status, [FromQuery] int page = 1,
        CancellationToken ct = default)
    {
        ViewData["Title"] = "Activity";
        page = Math.Max(1, page);

        var query = Filter(kind, status);
        var total = await query.CountAsync(ct);

        var entries = await query
            .OrderByDescending(j => j.CreatedAt)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .AsNoTracking().ToListAsync(ct);

        return View(new ActivityPageViewModel
        {
            Entries = entries,
            Page = page,
            PageSize = PageSize,
            TotalCount = total,
            KindFilter = kind,
            StatusFilter = status,
            QueueExplanations = await QueueExplanationsAsync(entries, ct)
        });
    }

    /// <summary>
    /// Cancels one of this workspace's own jobs — the generic cancel <c>IJobQueue</c> has exposed
    /// since P3 but which only <see cref="DeploymentsController"/> has ever called. Looked up by
    /// (id, WorkspaceId) together, the same guard <c>NotificationsController.MarkRead</c> uses, so
    /// posting another workspace's job id — guessed, or copied from a shared support thread — finds
    /// nothing and cancels nothing, rather than trusting the id alone.
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var row = await db.Jobs.AsNoTracking()
            .Where(j => j.Id == id && j.WorkspaceId == WorkspaceId)
            .Select(j => new { j.Kind, j.TargetId, j.Status })
            .FirstOrDefaultAsync(ct);

        if (row is null) return NotFound();

        if (row.Status is not (JobStatus.Pending or JobStatus.Running))
        {
            TempData["Error"] = IsFa
                ? "این کار دیگر در صف یا در حال اجرا نیست."
                : "This job is no longer queued or running.";
            return RedirectToAction(nameof(Index));
        }

        var cancelled = await jobs.RequestCancellationAsync(row.Kind, row.TargetId, ct);

        if (cancelled)
        {
            await audit.LogAsync("job.cancelled", "job", id.ToString(), ClientIp, workspaceId: WorkspaceId, ct: ct);
            TempData["Message"] = IsFa ? "درخواست لغو ثبت شد." : "Cancellation requested.";
        }
        else
        {
            TempData["Error"] = IsFa
                ? "این کار پیش از لغو به پایان رسیده بود."
                : "This job had already finished, so there was nothing to cancel.";
        }

        return RedirectToAction(nameof(Index));
    }

    private IQueryable<Job> Filter(JobKind? kind, JobStatus? status)
    {
        var query = db.Jobs.Where(j => j.WorkspaceId == WorkspaceId);
        if (kind is not null) query = query.Where(j => j.Kind == kind);
        if (status is not null) query = query.Where(j => j.Status == status);
        return query;
    }

    /// <summary>
    /// One resolved sentence per Pending/Running row on this page, computed the way
    /// <see cref="DeploymentsController.Details"/> computes it for one deployment — except this reads
    /// the platform-wide claimable set once and reuses it for every live row on the page, rather than
    /// once per row.
    /// </summary>
    private async Task<Dictionary<Guid, string>> QueueExplanationsAsync(
        IReadOnlyList<Job> entries, CancellationToken ct)
    {
        var live = entries.Where(j => j.Status is JobStatus.Pending or JobStatus.Running).ToList();
        var result = new Dictionary<Guid, string>();
        if (live.Count == 0) return result;

        // Every term of JobClaimQuery.Claimable, platform-wide — QueuePosition.For needs the whole
        // set to know what stands ahead of (or holds the key from) any one of these rows.
        var rows = await db.Jobs.AsNoTracking()
            .Where(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running)
            .Select(j => new
            {
                j.Id, j.Kind, j.TargetId, j.ExclusiveWith, j.Status, j.CreatedAt, j.NextAttemptAt,
                j.CancelRequested
            })
            .ToListAsync(ct);

        var claimable = rows.Select(r => new QueuedJob(
            r.Id, r.Kind, r.ExclusiveWith ?? r.TargetId, r.Status, r.CreatedAt, r.NextAttemptAt,
            r.CancelRequested)).ToList();

        var now = clock.UtcNow;
        var maxConcurrency = jobQueueOptions.Value.EffectiveMaxConcurrency;
        var isFa = IsFa;

        foreach (var job in live)
        {
            var place = QueuePosition.For(claimable, job.Id, now, maxConcurrency);
            if (QueuePosition.Describe(place, isFa) is { } description)
                result[job.Id] = description;
        }

        return result;
    }
}
