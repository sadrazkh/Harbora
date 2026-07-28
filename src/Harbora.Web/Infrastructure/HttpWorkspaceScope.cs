using Harbora.Application.Abstractions;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// Resolves the tenant scope for the DbContext's global query filters.
///
/// The distinction that matters is <b>request vs. system</b>, not authenticated vs. anonymous:
/// <list type="bullet">
/// <item>No <c>HttpContext</c> → background work (deploy pipeline, job worker, schedulers, startup
/// seeding). Those legitimately span tenants, so they run unscoped.</item>
/// <item>An <c>HttpContext</c> with no workspace claim → an unauthenticated or not-yet-onboarded
/// caller. Scoped to <see cref="Guid.Empty"/>, which matches no tenant's data. Deny by default: a
/// request must never fall back to seeing everything.</item>
/// </list>
/// </summary>
public sealed class HttpWorkspaceScope(IHttpContextAccessor accessor) : IWorkspaceScope
{
    public bool IsUnscoped => accessor.HttpContext is null;

    public Guid WorkspaceId =>
        Guid.TryParse(accessor.HttpContext?.User?.FindFirst(HarboraClaims.Workspace)?.Value, out var id)
            ? id
            : Guid.Empty;
}
