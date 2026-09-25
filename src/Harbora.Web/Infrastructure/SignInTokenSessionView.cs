using Harbora.Domain.Identity;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// What the banner draws: the <see cref="SignInToken"/> row this request was validated against — the
/// token-session counterpart of <see cref="SupportSessionView"/>, following its own remark exactly.
///
/// <para>
/// It does not query. <c>WorkspaceMembershipValidationMiddleware</c> has already read the row on this
/// request — that read is what enforces the token session's own hour — and leaves it here. So the
/// banner cannot render from a cookie the server has not just agreed with: no row, no banner, and no
/// row means the request was signed out before it reached a view at all.
/// </para>
/// </summary>
public sealed class SignInTokenSessionView(IHttpContextAccessor accessor)
{
    /// <summary>Where the middleware leaves the validated row.</summary>
    public const string ItemKey = "harbora.signin-token-session";

    /// <summary>The live token behind this request, or null when this session was not opened by one.</summary>
    public SignInToken? Current => accessor.HttpContext?.Items.TryGetValue(ItemKey, out var value) == true
        ? value as SignInToken
        : null;
}
