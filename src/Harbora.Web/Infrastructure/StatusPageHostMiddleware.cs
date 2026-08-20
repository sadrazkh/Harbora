using System.Text.RegularExpressions;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// Resolves which workspace's public status page a request is for, from the <c>Host</c> header alone
/// — <c>status-{workspace.Slug}.&lt;platform root domain&gt;</c> (P7, 2026-08-20 platform-options
/// plan). This is the whole seam: a request whose host does not match is untouched and falls through
/// to ordinary routing; one that does gets its slug stashed and its path rewritten under
/// <see cref="PathPrefix"/>, which only <see cref="Controllers.StatusPageController"/> answers.
///
/// <para>
/// <b>The extension point for sub-project 8.</b> A custom domain (<c>status.their.com</c>) is a
/// second host shape pointing at the same public endpoint — Traefik, not this middleware, is what
/// will bind that host to a router whose backend is this panel, the same way <c>node-agent.yml</c>
/// binds the node channel's host today. When that lands, this class gains a second recognised shape
/// (a DB lookup by custom domain) beside the regex below; the controller and everything downstream of
/// it — <c>StatusPageReport</c>, tenancy, honesty rules — do not change, because they were never told
/// which shape resolved the slug, only what it resolved to.
/// </para>
///
/// <para>
/// No database read happens here on purpose. Whether the resolved slug is a real, enabled status page
/// is <see cref="Infrastructure.Status.StatusPageReport"/>'s question to answer, once, inside the
/// request — this middleware's only job is host pattern matching, so it stays trivially unit-testable
/// and never becomes a second place tenancy could be gotten wrong.
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

    public Task InvokeAsync(HttpContext context)
    {
        var match = HostPattern.Match(context.Request.Host.Host);
        if (match.Success)
        {
            context.Items[SlugItemKey] = match.Groups["slug"].Value.ToLowerInvariant();

            var path = context.Request.Path.Value;
            context.Request.Path = string.IsNullOrEmpty(path) || path == "/"
                ? PathPrefix
                : PathPrefix + path;
        }

        return next(context);
    }
}
