using Harbora.Application.Abstractions;
using Harbora.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// Refuses one act while a platform administrator is signed in as a customer.
///
/// <para>
/// <b>This is the check.</b> The confirmation page lists what a support session cannot do and the
/// buttons stay exactly where they were, because a hidden button is not a control — somebody with
/// the URL, a bookmark, or a form they had open before the session started reaches the action
/// directly, and this is the only thing standing there. Everything the list does not name stays
/// allowed on purpose: support usually has to <i>do</i> the thing to see it fail.
/// </para>
///
/// <para>
/// The refusal goes through the panel's ordinary path for "you may not do this" —
/// <see cref="ForbidResult"/>, which the cookie scheme turns into its access-denied page, and 403
/// with a machine-readable body for a call that wanted JSON. Deliberately the same shape a
/// capability refusal has: a support session hitting a wall should look like every other wall in
/// this panel rather than like a new kind of failure.
/// </para>
///
/// <para>
/// Every refusal is audited before it is returned. That row is written through the ordinary logger,
/// so it carries the session id and both user ids like everything else the session does — and it is
/// the only place an attempt that changed nothing is written down at all.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RefuseUnderSupportSessionAttribute(SupportRestrictedAct act)
    : Attribute, IAsyncAuthorizationFilter
{
    public SupportRestrictedAct Act { get; } = act;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var services = context.HttpContext.RequestServices;
        var support = services.GetRequiredService<ISupportSession>();

        // The ordinary case: nobody is impersonating, so there is nothing here to refuse and the
        // customer's own use of their own account is untouched.
        if (support.SessionId is not { } sessionId) return;

        var audit = services.GetRequiredService<IAuditLogger>();
        await audit.LogAsync(
            SupportRestrictions.RefusedAction,
            "support_session", sessionId.ToString(),
            context.HttpContext.Connection.RemoteIpAddress?.ToString(),
            metadataJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                act = Act.ToString(),
                path = context.HttpContext.Request.Path.Value
            }),
            ct: context.HttpContext.RequestAborted);

        var isFa = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

        if (WantsJson(context))
        {
            context.Result = new ObjectResult(new
            {
                error = "support_session_blocked",
                act = Act.ToString().ToLowerInvariant(),
                reason = SupportRestrictions.Refusal(Act, isFa)
            })
            { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }

        context.Result = new ForbidResult();
    }

    /// <summary>
    /// The same test <see cref="RequireFeatureAttribute"/> applies, and for the same reason: a
    /// redirect to HTML is what turns "refused" into an unparseable response in somebody's script.
    /// </summary>
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
