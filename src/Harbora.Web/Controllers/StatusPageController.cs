using Harbora.Infrastructure.Status;
using Harbora.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Harbora.Web.Controllers;

/// <summary>
/// The public status page (P7, 2026-08-20 platform-options plan). Anonymous, and a tenancy boundary
/// with no session behind it: every request here resolves exactly one workspace from
/// <see cref="StatusPageHostMiddleware.SlugItemKey"/> — set only by that middleware, only from the
/// request's own <c>Host</c> header — and <see cref="StatusPageReport"/> is the one place that
/// resolved id ever touches a query, always with an explicit <c>WorkspaceId ==</c> predicate. There is
/// no route parameter, query string or form field anywhere on this controller that names a workspace;
/// nothing here could be asked for another tenant's page even by a caller trying to.
///
/// <para>
/// No cookies are read or set on this path (no <c>[Authorize]</c>, no antiforgery token, no session
/// cookie — the auth cookie is scoped to the panel's own domain and this page lives on a different
/// one). The page is deliberately not marked <c>noindex</c>: a status page wants to be findable by the
/// customer's own users searching for it.
/// </para>
/// </summary>
[AllowAnonymous]
[Route(StatusPageHostMiddleware.PathSegment)]
public sealed class StatusPageController(StatusPageReport report) : Controller
{
    private bool IsFa => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        // Set only by StatusPageHostMiddleware, and only when the Host header matched
        // status-{slug}.<platform domain>. Reaching this action any other way (the internal path
        // segment guessed directly, on the panel's own host) leaves this unset, and resolves nothing.
        if (HttpContext.Items[StatusPageHostMiddleware.SlugItemKey] is not string slug)
            return NotFound();

        var view = await report.BuildAsync(slug, IsFa, ct);
        // A disabled page and a workspace that does not exist are the same "not found" to the outside
        // world — see StatusPageReport.BuildAsync's own doc for why neither may be distinguished here.
        if (view is null) return NotFound();

        ViewData["Title"] = view.WorkspaceName;
        return View(view);
    }
}
