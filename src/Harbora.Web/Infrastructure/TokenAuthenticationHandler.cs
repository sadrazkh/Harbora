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

        // Same bootstrap as cookie login: establishes the caller's workspace, so it must bypass the
        // workspace filter.
        var membership = await db.WorkspaceMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.UserId == user.Id)
            .OrderByDescending(m => m.Workspace!.IsPersonal)
            .Select(m => new { m.WorkspaceId, m.Role })
            .FirstOrDefaultAsync(Context.RequestAborted);
        if (membership is null)
            return AuthenticateResult.Fail("This account is not a member of any workspace.");

        var principal = SessionPrincipalFactory.Create(user, membership.WorkspaceId, membership.Role, SchemeName);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
