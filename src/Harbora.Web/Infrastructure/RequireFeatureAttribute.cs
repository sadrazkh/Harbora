using Harbora.Application.Abstractions;
using Harbora.Domain.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// Refuses a request whose workspace is not entitled to the named feature.
///
/// <para>
/// This is the check. The greyed control and the locked sidebar entry are a courtesy on top of it —
/// somebody with the URL, an old bookmark or a scripted call reaches the action directly, and this
/// is the only thing standing there.
/// </para>
///
/// <para>
/// A page request is redirected to the locked page, which explains what the feature is and who can
/// switch it on. An API or fetch call gets 403 with a machine-readable reason, because a redirect to
/// HTML is what turns "not entitled" into an unparseable response in somebody's script.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireFeatureAttribute(string featureKey) : Attribute, IAsyncAuthorizationFilter
{
    public string FeatureKey { get; } = featureKey;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var services = context.HttpContext.RequestServices;
        var currentUser = services.GetRequiredService<ICurrentUser>();
        var gate = services.GetRequiredService<IFeatureGate>();

        // No workspace means nothing to be entitled with. Deny rather than fall through: an
        // unauthenticated or half-signed-in request must not reach a gated action at all.
        if (currentUser.WorkspaceId is not { } workspaceId)
        {
            context.Result = new ForbidResult();
            return;
        }

        var verdict = await gate.EvaluateAsync(workspaceId, FeatureKey, context.HttpContext.RequestAborted);
        if (verdict.IsEnabled) return;

        if (WantsJson(context))
        {
            context.Result = new ObjectResult(new
            {
                error = "feature_not_enabled",
                feature = FeatureKey,
                state = verdict.State.ToString().ToLowerInvariant()
            })
            { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }

        // Hidden means the panel does not mention it, so its locked page would be an advert for
        // something the operator chose not to sell. A plain 404 is the honest answer there.
        context.Result = verdict.State == FeatureState.Hidden
            ? new NotFoundResult()
            : new RedirectToActionResult("Locked", "Features", new { key = FeatureKey });
    }

    private static bool WantsJson(AuthorizationFilterContext context)
    {
        var request = context.HttpContext.Request;
        return request.Headers.XRequestedWith == "XMLHttpRequest"
            || request.Path.StartsWithSegments("/api")
            || (request.Headers.Accept.Count > 0
                && request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase)
                && !request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase));
    }
}
