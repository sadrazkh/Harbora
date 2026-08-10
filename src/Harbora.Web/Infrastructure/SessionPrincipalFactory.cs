using System.Security.Claims;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Harbora.Web.Infrastructure;

public static class SessionPrincipalFactory
{
    public static ClaimsPrincipal Create(
        User user, Guid workspaceId, WorkspaceRole workspaceRole,
        string authenticationType = CookieAuthenticationDefaults.AuthenticationScheme,
        Guid? sessionId = null)
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
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType));
    }
}
