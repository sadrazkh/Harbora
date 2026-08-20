using Harbora.Domain.Identity;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// What the banner draws: the <see cref="SupportSession"/> row this request was validated against.
///
/// <para>
/// It does not query. <c>WorkspaceMembershipValidationMiddleware</c> has already read the row on
/// this request — that read is what enforces the hour — and leaves it here. So the banner cannot
/// render from a cookie the server has not just agreed with: no row, no banner, and no row means
/// the request was signed out before it reached a view at all.
/// </para>
///
/// <para>
/// The other direction is the one that matters more. The banner is the customer's protection, so it
/// must be impossible to be impersonated without it. Nothing sets this except the middleware, and
/// the middleware sets it on every authenticated cookie request carrying the claim — there is no
/// path that validates a support session and skips the banner.
/// </para>
/// </summary>
public sealed class SupportSessionView(IHttpContextAccessor accessor)
{
    /// <summary>Where the middleware leaves the validated row.</summary>
    public const string ItemKey = "harbora.support-session";

    /// <summary>The live session behind this request, or null when nobody is impersonating.</summary>
    public SupportSession? Current => accessor.HttpContext?.Items.TryGetValue(ItemKey, out var value) == true
        ? value as SupportSession
        : null;
}
