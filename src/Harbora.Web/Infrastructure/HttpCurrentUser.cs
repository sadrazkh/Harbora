using System.Security.Claims;
using Harbora.Application.Abstractions;

namespace Harbora.Web.Infrastructure;

/// <summary>Resolves the caller from cookie/bearer claims set during authentication.</summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? User => accessor.HttpContext?.User;

    public Guid? UserId =>
        Guid.TryParse(User?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public string? Email => User?.FindFirstValue(ClaimTypes.Email);

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated ?? false;

    public Guid? WorkspaceId =>
        Guid.TryParse(User?.FindFirstValue("workspace"), out var id) ? id : null;
}

/// <summary>
/// Reads the support-session claims off the cookie the way <see cref="HttpCurrentUser"/> reads the
/// ordinary ones.
///
/// <para>
/// What this resolves is "who is really at the keyboard", not "is the session still valid". The
/// second question belongs to <c>WorkspaceMembershipValidationMiddleware</c>, which re-reads the row
/// on every request and signs the cookie out when the row says the hour is over — so by the time any
/// controller, audit row or view asks this, the claims it reports have already been checked against
/// the database for this request.
/// </para>
/// </summary>
public sealed class HttpSupportSession(IHttpContextAccessor accessor) : ISupportSession
{
    private ClaimsPrincipal? User => accessor.HttpContext?.User;

    public Guid? SessionId =>
        Guid.TryParse(User?.FindFirstValue(HarboraClaims.SupportSession), out var id) ? id : null;

    public Guid? AdminUserId =>
        Guid.TryParse(User?.FindFirstValue(HarboraClaims.SupportAdmin), out var id) ? id : null;

    public string? AdminEmail => User?.FindFirstValue(HarboraClaims.SupportAdminEmail);
}

public static class HarboraClaims
{
    public const string Workspace = "workspace";
    public const string WorkspaceRole = "workspace_role";
    public const string Session = "session_id";

    /// <summary>The <c>SupportSession</c> row this cookie was issued against. The row is the truth.</summary>
    public const string SupportSession = "support_session_id";

    /// <summary>The platform administrator behind a support session.</summary>
    public const string SupportAdmin = "support_admin_id";

    /// <summary>Their address, so an audit row and a banner can name them without a join.</summary>
    public const string SupportAdminEmail = "support_admin_email";
}
