using Harbora.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Harbora.Web.Controllers;

/// <summary>
/// Renders the themed 503 <see cref="MaintenanceModeMiddleware"/> rewrites a request to (P5,
/// 2026-08-20 platform-options plan). Reachable only through that rewrite — a direct hit on
/// <see cref="MaintenanceModeMiddleware.MaintenancePath"/> carries no
/// <see cref="MaintenanceModeMiddleware.ContextKey"/> in <c>HttpContext.Items</c>, and 404 is the
/// honest answer for that, not a generic maintenance page shown to nobody in particular.
/// </summary>
[AllowAnonymous]
[Route(MaintenanceModeMiddleware.MaintenancePath)]
public sealed class MaintenanceController : Controller
{
    /// <summary>
    /// No <c>[HttpGet]</c>/verb attribute on purpose: Traefik forwards whatever method the visitor's
    /// browser or client actually sent — a page load is GET, but a form POST or an API call mid-
    /// maintenance is not, and every one of them has to come back as this page, not a 404 for using
    /// the wrong verb against a route nobody chose.
    /// </summary>
    [Route("")]
    public IActionResult Show()
    {
        if (HttpContext.Items[MaintenanceModeMiddleware.ContextKey] is not MaintenanceInfo info)
            return NotFound();

        // Never cached — the moment maintenance is turned off this same URL must stop answering 503.
        Response.Headers.CacheControl = "no-store";
        Response.Headers.RetryAfter = "120";
        Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return View(info);
    }
}
