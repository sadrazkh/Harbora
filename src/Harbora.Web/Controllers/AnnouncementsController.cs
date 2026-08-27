using System.Globalization;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Platform;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Platform announcements — operator CRUD, plus the one action every signed-in person may reach:
/// dismissing their own copy of one.
///
/// <para>
/// Mixed authorization within one controller, the same shape <c>FeaturesController</c> and
/// <c>PlansController</c> already use: <c>[Authorize]</c> at the class level so any signed-in person
/// can reach <see cref="Dismiss"/> (the whole point of the banner is that it is not a platform-admin
/// surface), and <see cref="Capabilities.TenantsManage"/> layered onto the CRUD actions specifically —
/// the same policy <c>UsersController</c>, <c>AdminRevenueController</c> and <c>TenantsController</c>
/// already gate the rest of this platform-admin console with.
/// </para>
/// </summary>
[Authorize]
[Route("announcements")]
public sealed class AnnouncementsController(
    HarboraDbContext db,
    ICurrentUser currentUser,
    ISystemClock clock,
    Harbora.Infrastructure.Platform.AnnouncementNotifier notifier,
    IAuditLogger audit) : Controller
{
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    [Authorize(Policy = Capabilities.TenantsManage)]
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Announcements";

        var now = clock.UtcNow;
        var announcements = await db.Announcements.IgnoreQueryFilters().AsNoTracking()
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

        var rows = announcements.Select(a => new AnnouncementRow(
            a.Id, a.Title, a.Body, a.TitleFa, a.BodyFa, a.Severity,
            a.StartsAt, a.EndsAt, a.CreatedByEmail, a.CreatedAt, a.IsActiveAt(now))).ToList();

        return View(rows);
    }

    [Authorize(Policy = Capabilities.TenantsManage)]
    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        string? title, string? body, string? titleFa, string? bodyFa,
        AlertSeverity severity, string? startsAt, string? endsAt, CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
            return Back("Sign in again before posting an announcement.", error: true);

        if (!TryParseWindowField(startsAt, out var starts))
            return Back("Enter a valid start time.", error: true);
        if (!TryParseWindowField(endsAt, out var ends))
            return Back("Enter a valid end time.", error: true);

        title = (title ?? "").Trim();
        body = (body ?? "").Trim();
        titleFa = (titleFa ?? "").Trim();
        bodyFa = (bodyFa ?? "").Trim();

        if (AnnouncementRules.RefuseSave(title, body, titleFa, bodyFa, severity, starts, ends) is { } refusal)
            return Back(refusal, error: true);

        var email = await db.Users.IgnoreQueryFilters()
            .Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync(ct) ?? "";

        var announcement = new Announcement
        {
            Title = title, Body = body, TitleFa = titleFa, BodyFa = bodyFa,
            Severity = severity, StartsAt = starts, EndsAt = ends,
            CreatedByUserId = userId, CreatedByEmail = email
        };
        db.Announcements.Add(announcement);
        await db.SaveChangesAsync(ct);

        // Info stays banner-only. Warning additionally fans out through the existing N3 in-app path —
        // AnnouncementNotifier reuses NotificationService.FanOutToMembersAsync via
        // NotifyInAppOnlyAsync, it does not build a second one. Fired once, at creation, not on every
        // later edit — an operator correcting a typo does not re-page everybody who already saw it.
        if (announcement.Severity == AlertSeverity.Warning)
            await notifier.NotifyAsync(announcement, ct);

        await audit.LogAsync("announcement.created", "announcement", announcement.Id.ToString(), ClientIp, workspaceId: null, ct: ct);
        return Back("Announcement posted.");
    }

    [Authorize(Policy = Capabilities.TenantsManage)]
    [HttpGet("{id:guid}/edit")]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var announcement = await db.Announcements.IgnoreQueryFilters()
            .AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (announcement is null) return NotFound();

        ViewData["Title"] = "Edit announcement";
        return View(new AnnouncementRow(
            announcement.Id, announcement.Title, announcement.Body,
            announcement.TitleFa, announcement.BodyFa, announcement.Severity,
            announcement.StartsAt, announcement.EndsAt,
            announcement.CreatedByEmail, announcement.CreatedAt, announcement.IsActiveAt(clock.UtcNow)));
    }

    [Authorize(Policy = Capabilities.TenantsManage)]
    [HttpPost("{id:guid}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        Guid id, string? title, string? body, string? titleFa, string? bodyFa,
        AlertSeverity severity, string? startsAt, string? endsAt, CancellationToken ct)
    {
        var announcement = await db.Announcements.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (announcement is null) return NotFound();

        if (!TryParseWindowField(startsAt, out var starts))
            return Back("Enter a valid start time.", error: true);
        if (!TryParseWindowField(endsAt, out var ends))
            return Back("Enter a valid end time.", error: true);

        title = (title ?? "").Trim();
        body = (body ?? "").Trim();
        titleFa = (titleFa ?? "").Trim();
        bodyFa = (bodyFa ?? "").Trim();

        if (AnnouncementRules.RefuseSave(title, body, titleFa, bodyFa, severity, starts, ends) is { } refusal)
            return Back(refusal, error: true);

        announcement.Title = title;
        announcement.Body = body;
        announcement.TitleFa = titleFa;
        announcement.BodyFa = bodyFa;
        announcement.Severity = severity;
        announcement.StartsAt = starts;
        announcement.EndsAt = ends;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("announcement.edited", "announcement", announcement.Id.ToString(), ClientIp, workspaceId: null, ct: ct);
        return Back("Announcement updated.");
    }

    [Authorize(Policy = Capabilities.TenantsManage)]
    [HttpPost("{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var announcement = await db.Announcements.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (announcement is null) return NotFound();

        db.Announcements.Remove(announcement);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("announcement.deleted", "announcement", id.ToString(), ClientIp, workspaceId: null, ct: ct);
        return Back("Announcement removed.");
    }

    /// <summary>
    /// The banner's own button — every signed-in person, not only platform admins. Per-user,
    /// per-announcement (<see cref="AnnouncementDismissal"/>'s own doc), and idempotent: a second
    /// click, or two arriving together from a doubled tap, both land on "already dismissed" rather
    /// than a duplicate row or an error page.
    ///
    /// <para>
    /// Returns to <paramref name="returnUrl"/> via <see cref="Controller.LocalRedirect"/> — the same
    /// pattern <c>AccountController.SetPanelMode</c>/<c>SetRail</c> use for the same reason: this
    /// button lives on a partial rendered on every page, and dismissing one must not navigate the
    /// person away from whatever they were doing.
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/dismiss")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Dismiss(Guid id, string? returnUrl, CancellationToken ct)
    {
        if (currentUser.UserId is { } userId)
        {
            var alreadyDismissed = await db.AnnouncementDismissals
                .AnyAsync(d => d.AnnouncementId == id && d.UserId == userId, ct);

            if (!alreadyDismissed)
            {
                db.AnnouncementDismissals.Add(new AnnouncementDismissal
                {
                    AnnouncementId = id, UserId = userId, DismissedAt = clock.UtcNow
                });

                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException)
                {
                    // Lost a race with this same person's own second click/tab — the unique index on
                    // (AnnouncementId, UserId) is what refused the duplicate, and the outcome the
                    // loser wants is identical to the winner's: dismissed. Nothing here reads which
                    // of the two rows survived.
                }
            }
        }

        return LocalRedirect(string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl);
    }

    // ---- helpers ---

    /// <summary>Matches <c>VouchersController.Create</c>'s own parsing of a
    /// <c>&lt;input type="datetime-local"&gt;</c> field: empty means "no bound", not "invalid".</summary>
    private static bool TryParseWindowField(string? value, out DateTimeOffset? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value)) return true;

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var result))
            return false;

        parsed = result;
        return true;
    }

    private IActionResult Back(string message, bool error = false)
    {
        TempData[error ? "Error" : "Message"] = message;
        return RedirectToAction(nameof(Index));
    }
}
