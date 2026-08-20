using Harbora.Domain.Identity;
using Harbora.Web.Infrastructure;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Signing in as a customer, from the console that already lists them.
///
/// <para>
/// The owner rejected silent support access, so there is no quiet variant of this: starting a
/// session writes a row the customer can read, puts a banner on every page they load while it runs,
/// and stamps every audited act with both the account it ran as and the administrator behind it.
/// The reason is required for the same reason the credit note is — it is the only thing on the
/// record that explains why somebody was inside another person's account.
/// </para>
///
/// <para>
/// A confirmation page rather than a button on the tenant screen, following the idiom
/// <see cref="ConfirmCredit"/> established: this is not destructive, but it is not undoable either —
/// the customer will have seen the banner whatever happens next — so it is looked at once before it
/// happens rather than typed into a row of other controls.
/// </para>
/// </summary>
public sealed partial class TenantsController
{
    [HttpGet("{id:guid}/support/{userId:guid}")]
    public async Task<IActionResult> ConfirmSupport(Guid id, Guid userId, CancellationToken ct)
    {
        var vm = await SupportPageAsync(id, userId, TempData["SupportReason"] as string, ct);
        if (vm is null) return NotFound();

        ViewData["Title"] = $"Sign in as {vm.TargetEmail}";
        return View(vm);
    }

    /// <summary>
    /// Opens the session and swaps this browser onto the customer's account.
    ///
    /// <para>
    /// The audit row is written after the principal has been swapped, deliberately: from that line
    /// onwards every row this browser writes carries the session id and the administrator's id
    /// beside the customer's, and the session's own opening row is no exception. One query on
    /// <c>SupportSessionId</c> then returns the whole session — its start included — rather than the
    /// start being a differently-shaped row somebody has to know to look for separately.
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/support/{userId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartSupport(Guid id, Guid userId, string? reason, CancellationToken ct)
    {
        IActionResult Again(string error)
        {
            TempData["Error"] = error;
            // Carried back so a refusal is a correction rather than a retype — and out of the query
            // string, where a note about a customer's problem would land in every access log on the
            // way. A plain string survives the TempData round trip; a GUID-shaped one would come back
            // typed as a Guid, which is why nothing here carries an id that way.
            TempData["SupportReason"] = reason;
            return RedirectToAction(nameof(ConfirmSupport), new { id, userId });
        }

        if (currentUser.UserId is not { } adminUserId || currentUser.Email is not { } adminEmail)
            return Again("Sign in again — this session could not be attributed to anybody.");

        var start = await supportSessions.StartAsync(
            adminUserId, adminEmail, userId, id, reason, ClientIp, ct);
        if (!start.Ok) return Again(start.Refusal!);

        var support = start.Session!;
        var target = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        var membership = await db.WorkspaceMembers.IgnoreQueryFilters()
            .FirstAsync(m => m.UserId == userId && m.WorkspaceId == id, ct);

        // An ordinary browser session for the customer's account, so everything that revalidates one
        // — "sign out all devices", a suspended account, a revoked membership — applies to a support
        // session exactly as it applies to the customer's own. A second, special session type would
        // be a second place for all of that to be forgotten.
        var browserSession = await accountSessions.CreateAsync(
            target.Id, ClientIp, Request.Headers.UserAgent.ToString(), ct);

        var principal = SessionPrincipalFactory.Create(
            target, id, membership.Role, sessionId: browserSession.Id, support: support);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

        // SignInAsync writes the cookie; it does not change who this request thinks it is. Doing that
        // here is what puts the opening audit row inside the session it opens.
        HttpContext.User = principal;

        await audit.LogAsync("session.started", "workspace", id.ToString(), ClientIp,
            metadataJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                adminEmail,
                targetEmail = target.Email,
                reason = support.Reason,
                expiresAt = support.ExpiresAt.ToUnixTimeSeconds()
            }), ct: ct);

        return Redirect("/");
    }

    private async Task<TenantSupportViewModel?> SupportPageAsync(
        Guid id, Guid userId, string? reason, CancellationToken ct)
    {
        var ws = await db.Workspaces.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws is null) return null;

        var target = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (target is null) return null;

        // Platform admin acting on another workspace: the filtered read would find no membership and
        // the page would refuse a perfectly ordinary customer.
        var membership = await db.WorkspaceMembers.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(m => m.UserId == userId && m.WorkspaceId == id, ct);
        if (membership is null) return null;

        return new TenantSupportViewModel
        {
            WorkspaceId = ws.Id,
            WorkspaceName = ws.Name,
            TargetUserId = target.Id,
            TargetEmail = target.Email,
            TargetIsActive = target.IsActive,
            LifetimeMinutes = (int)SupportAccess.Lifetime.TotalMinutes,
            MaxReasonLength = SupportAccess.MaxReasonLength,
            Reason = reason
        };
    }
}
