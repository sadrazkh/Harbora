using System.Security.Claims;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Harbora.Web.Infrastructure;

public static class SessionPrincipalFactory
{
    /// <param name="support">
    /// The support session this cookie is being issued under, when a platform administrator is
    /// signing in as this user. The claims it adds are how the audit trail, the banner and the
    /// refused acts know who is really at the keyboard — but they are not the authority on whether
    /// the session is still alive. <c>WorkspaceMembershipValidationMiddleware</c> re-reads the row
    /// on every request for that, so nothing here can outlive it.
    /// </param>
    /// <param name="signInToken">
    /// The <see cref="SignInToken"/> a redemption is opening this cookie under. The same discipline as
    /// <paramref name="support"/> applies: the claim it adds names the row, the middleware re-reads the
    /// row on every request, and nothing here is the authority on whether the hour is still running.
    /// Mutually exclusive with <paramref name="support"/> in practice — a token session signs in fresh
    /// via <see cref="Create"/> rather than layering onto an existing principal, the same as a support
    /// session does — but nothing here enforces that beyond neither caller ever passing both.
    /// </param>
    public static ClaimsPrincipal Create(
        User user, Guid workspaceId, WorkspaceRole workspaceRole,
        string authenticationType = CookieAuthenticationDefaults.AuthenticationScheme,
        Guid? sessionId = null,
        SupportSession? support = null,
        SignInToken? signInToken = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(HarboraClaims.Workspace, workspaceId.ToString()),
            new(HarboraClaims.WorkspaceRole, workspaceRole.ToString())
        };
        if (sessionId is { } id) claims.Add(new Claim(HarboraClaims.Session, id.ToString()));
        if (support is not null)
        {
            claims.Add(new Claim(HarboraClaims.SupportSession, support.Id.ToString()));
            claims.Add(new Claim(HarboraClaims.SupportAdmin, support.AdminUserId.ToString()));
            claims.Add(new Claim(HarboraClaims.SupportAdminEmail, support.AdminEmail));
        }
        if (signInToken is not null)
            claims.Add(new Claim(HarboraClaims.SignInTokenSession, signInToken.Id.ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType));
    }
}
