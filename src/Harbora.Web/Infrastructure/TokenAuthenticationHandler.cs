using System.Security.Claims;
using System.Text.Encodings.Web;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// Authenticates API/CLI requests presenting `Authorization: Bearer hbr_...`. Validates the
/// token via <see cref="ITokenService"/> (constant-time hash compare) and materialises the
/// user's claims so API controllers can authorise identically to cookie users.
/// </summary>
public sealed class TokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ITokenService tokens,
    HarboraDbContext db) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Token";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var presented = header["Bearer ".Length..].Trim();
        var userId = await tokens.ValidateAsync(presented, Context.RequestAborted);
        if (userId is null)
            return AuthenticateResult.Fail("Invalid or expired token.");

        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, Context.RequestAborted);
        if (user is null)
            return AuthenticateResult.Fail("User not found or inactive.");

        var workspace = await db.WorkspaceMembers.AsNoTracking()
            .Where(m => m.UserId == user.Id).Select(m => m.WorkspaceId).FirstOrDefaultAsync(Context.RequestAborted);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(HarboraClaims.Workspace, workspace.ToString())
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
