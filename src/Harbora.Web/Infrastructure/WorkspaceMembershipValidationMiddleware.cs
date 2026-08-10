using System.Security.Claims;
using Harbora.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// Revalidates the active membership and refreshes role claims on every authenticated request.
/// Removing a member or reducing a role therefore takes effect now, not when a seven-day cookie
/// eventually expires.
/// </summary>
public sealed class WorkspaceMembershipValidationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, HarboraDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        var userIdValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var workspaceValue = context.User.FindFirstValue(HarboraClaims.Workspace);
        if (!Guid.TryParse(userIdValue, out var userId) || !Guid.TryParse(workspaceValue, out var workspaceId))
        {
            await RejectAsync(context);
            return;
        }

        // Bearer tokens have their own expiry/revocation record. Browser cookies must name a live
        // server-side session so password changes and "sign out all devices" take effect now.
        if (context.User.Identity?.AuthenticationType == CookieAuthenticationDefaults.AuthenticationScheme)
        {
            var sessionValue = context.User.FindFirstValue(HarboraClaims.Session);
            if (!Guid.TryParse(sessionValue, out var sessionId))
            {
                await RejectAsync(context, "session");
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var session = await db.UserSessions.IgnoreQueryFilters().FirstOrDefaultAsync(
                s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now,
                context.RequestAborted);
            if (session is null)
            {
                await RejectAsync(context, "session");
                return;
            }

            if (session.LastSeenAt < now.AddMinutes(-5))
            {
                session.LastSeenAt = now;
                session.ExpiresAt = now + Harbora.Infrastructure.Security.AccountSessionService.Lifetime;
                await db.SaveChangesAsync(context.RequestAborted);
            }
        }

        var user = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Id == userId && u.IsActive)
            .Select(u => new { u.Role })
            .FirstOrDefaultAsync(context.RequestAborted);
        var membership = await db.WorkspaceMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.UserId == userId && m.WorkspaceId == workspaceId)
            .Select(m => new { m.Role })
            .FirstOrDefaultAsync(context.RequestAborted);
        if (user is null || membership is null)
        {
            await RejectAsync(context);
            return;
        }

        if (context.User.Identity is ClaimsIdentity identity)
        {
            Replace(identity, ClaimTypes.Role, user.Role.ToString());
            Replace(identity, HarboraClaims.WorkspaceRole, membership.Role.ToString());
        }
        await next(context);
    }

    private static void Replace(ClaimsIdentity identity, string type, string value)
    {
        foreach (var old in identity.FindAll(type).ToList()) identity.RemoveClaim(old);
        identity.AddClaim(new Claim(type, value));
    }

    private static async Task RejectAsync(HttpContext context, string reason = "workspace-membership")
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Your active workspace membership is no longer valid." });
            return;
        }

        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        context.Response.Redirect("/account/login?reason=" + reason);
    }
}
