using System.Text.RegularExpressions;
using Harbora.Data;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// Resolves which workspace's public status page a request is for, from the <c>Host</c> header alone
/// — <c>status-{workspace.Slug}.&lt;platform root domain&gt;</c> (P7, 2026-08-20 platform-options
/// plan). This is the whole seam: a request whose host does not match is untouched and falls through
/// to ordinary routing; one that does gets its slug stashed and its path rewritten under
/// <see cref="PathPrefix"/>, which only <see cref="Controllers.StatusPageController"/> answers.
///
/// <para>
/// <b>Sub-project 8's custom-domain shape.</b> A customer's own domain (<c>status.their.com</c>) is a
/// second host shape pointing at the same public endpoint, bound by an ordinary <c>Route</c> whose
/// backend is this panel — see <c>StatusPageDomainService</c>. When the regex below does not match,
/// this falls back to one indexed lookup on <c>DomainName.Host</c>, exactly the shape and exactly the
/// reasoning <c>MaintenanceModeMiddleware</c> already established for the same question ("does this
/// request's host mean something other than ordinary panel routing") on a host that also toggles
/// rather than being fixed at install time: no cache, because correctness beats one avoidable query,
/// and a stale cache would either strand a customer's real domain on a 404 after it was attached or
/// keep answering after it was removed. The controller and everything downstream of it —
/// <c>StatusPageReport</c>, tenancy, honesty rules — do not change: both shapes resolve to the same
/// <see cref="SlugItemKey"/>, and neither the controller nor the report was ever told which one did it.
/// </para>
/// </summary>
public sealed class StatusPageHostMiddleware(RequestDelegate next)
{
    /// <summary>Registered as this controller's own route prefix — never reachable by path alone.
    /// <see cref="Controllers.StatusPageController"/> trusts only <see cref="SlugItemKey"/>, which
    /// nothing but this middleware ever sets, so a request that reaches the prefix on a host that did
    /// not match resolves nothing rather than falling back to some other workspace.</summary>
    public const string PathSegment = "__status-page";

    private const string PathPrefix = "/" + PathSegment;

    /// <summary>Where the resolved slug is stashed for the controller to read back.</summary>
    public const string SlugItemKey = "Harbora.StatusPageWorkspaceSlug";

    // A DNS label: alphanumeric, may contain internal hyphens, 1–63 characters — Workspace.Slug's own
    // shape. Anchored so "status-" must be the start of the leftmost label, matching
    // ReservedHosts.IsReservedPrefix's own rule for the address this middleware is the other half of.
    private static readonly Regex HostPattern = new(
        @"^status-(?<slug>[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)\.",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task InvokeAsync(HttpContext context, HarboraDbContext db)
    {
        var host = context.Request.Host.Host;
        var match = HostPattern.Match(host);

        var slug = match.Success
            ? match.Groups["slug"].Value.ToLowerInvariant()
            : await CustomDomainSlugAsync(db, host, context.RequestAborted);

        if (slug is not null)
        {
            context.Items[SlugItemKey] = slug;

            var path = context.Request.Path.Value;
            context.Request.Path = string.IsNullOrEmpty(path) || path == "/"
                ? PathPrefix
                : PathPrefix + path;
        }

        await next(context);
    }

    /// <summary>
    /// The workspace slug behind a status page's custom domain, or null when <paramref name="host"/>
    /// is not one — unfiltered on both sides, the same as <c>MaintenanceModeMiddleware</c>'s own
    /// lookup: a visitor here has no session and no workspace, and <c>DomainName.Host</c> is unique
    /// across the platform, so this can resolve at most one workspace regardless of whose domain it
    /// turns out to be.
    /// </summary>
    private static async Task<string?> CustomDomainSlugAsync(HarboraDbContext db, string host, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(host)) return null;

        return await db.Domains.IgnoreQueryFilters()
            .Where(d => d.Host == host && d.StatusPageId != null)
            .Join(db.StatusPages.IgnoreQueryFilters(), d => d.StatusPageId, p => p.Id, (d, p) => p.WorkspaceId)
            .Join(db.Workspaces.IgnoreQueryFilters().Where(w => w.DeletedAt == null),
                workspaceId => workspaceId, w => w.Id, (workspaceId, w) => w.Slug)
            .FirstOrDefaultAsync(ct);
    }
}
