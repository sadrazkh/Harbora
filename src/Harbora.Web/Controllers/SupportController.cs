using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// The button on the support banner.
///
/// <para>
/// It lives outside the tenants console on purpose: while a support session is running, this browser
/// is the customer, and the customer's role cannot reach a page guarded by
/// <c>tenants.manage</c>. An "end now" the administrator is locked out of would be no button at all,
/// so this asks only for an authenticated caller and then for a live support session — which the
/// membership middleware has already validated against the row before this action is reached.
/// </para>
///
/// <para>
/// Ending puts the administrator back into their own account rather than dropping them at the login
/// form. That is not a convenience: a button whose cost is "sign in again" is a button people avoid
/// pressing, and the whole point of the hour is that leaving is cheap.
/// </para>
/// </summary>
[Authorize]
public sealed class SupportController(
    HarboraDbContext db,
    ISupportSession support,
    IAuditLogger audit,
    ICurrentUser currentUser,
    Harbora.Infrastructure.Identity.SupportSessionService supportSessions,
    Harbora.Infrastructure.Security.AccountSessionService accountSessions) : Controller
{
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    [HttpPost("/support/end")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> End(CancellationToken ct)
    {
        // No support session, nothing to end. Not an error worth a page: the ordinary way to reach
        // this is a stale tab whose session already expired, and the honest answer there is the
        // dashboard rather than a message about a session that is already over.
        if (support.SessionId is not { } sessionId) return Redirect("/");

        // Audited before the cookie is swapped, so the closing row is stamped with the session it
        // closes — the same one query that returns everything else this session did. currentUser
        // still reads as the customer at this instant, so its WorkspaceId names the workspace the
        // session ran against.
        await audit.LogAsync("session.ended", "support_session", sessionId.ToString(), ClientIp,
            workspaceId: currentUser.WorkspaceId, ct: ct);
        await supportSessions.EndAsync(sessionId, ct);

        var adminUserId = support.AdminUserId;
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        if (adminUserId is not { } adminId) return Redirect("/account/login");

        var admin = await db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == adminId && u.IsActive, ct);
        // Their account was suspended or deleted while they were inside somebody else's. Nothing to
        // restore, and inventing a session for a disabled account would be the one thing worse.
        if (admin is null) return Redirect("/account/login");

        var membership = await db.WorkspaceMembers.IgnoreQueryFilters()
            .Where(m => m.UserId == adminId
                && m.Workspace!.ArchivedAt == null && m.Workspace.DeletedAt == null)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.WorkspaceId, m.Role })
            .FirstOrDefaultAsync(ct);
        if (membership is null) return Redirect("/account/login");

        var session = await accountSessions.CreateAsync(
            admin.Id, ClientIp, Request.Headers.UserAgent.ToString(), ct);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            SessionPrincipalFactory.Create(admin, membership.WorkspaceId, membership.Role, sessionId: session.Id));

        return Redirect("/tenants");
    }
}
