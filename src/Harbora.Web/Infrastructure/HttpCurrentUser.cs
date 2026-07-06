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

public static class HarboraClaims
{
    public const string Workspace = "workspace";
}
