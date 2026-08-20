using Harbora.Data;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// What <see cref="Controllers.MaintenanceController.Show"/> reads back — everything the themed 503
/// needs, gathered here so the controller and view never query the database themselves.
/// </summary>
public sealed record MaintenanceInfo(
    Guid AppId, string AppName, string? MessageEn, string? MessageFa, DateTimeOffset? Since);

/// <summary>
/// A request arriving on an app's own host while that app is in maintenance is not talking to the
/// app at all: <c>AppOperationsService.SetMaintenanceModeAsync</c> already repointed every Route for
/// that host at this very panel container (P5, 2026-08-20 platform-options plan), so whatever path
/// or method the visitor sent has to come back as the maintenance page, not fall through to ordinary
/// panel routing — which knows nothing about a customer's app and would 404 or, worse, coincidentally
/// match a real panel route.
///
/// <para>
/// Rewrites the request to <see cref="MaintenancePath"/> and hands the resolved
/// <see cref="MaintenanceInfo"/> down through <c>HttpContext.Items</c>, the same shape
/// <c>UseStatusCodePagesWithReExecute</c> already uses to keep the true cause of a response separate
/// from the path that ends up rendering it. A direct hit on <see cref="MaintenancePath"/> — nobody
/// went through this rewrite — finds nothing in <c>Items</c> and the controller answers 404.
/// </para>
///
/// <para>
/// Positioned ahead of static files and routing, the same reasoning <c>SetupGuardMiddleware</c>
/// already applies one step later in the pipeline: a customer's own <c>/favicon.ico</c> or
/// <c>/api/...</c> must not coincidentally resolve against the panel's own files or routes while
/// their app is supposed to be showing a maintenance page.
/// </para>
///
/// <para>
/// One indexed lookup by <c>DomainName.Host</c> (unique in the schema) per request rather than a
/// cache: <c>SetupGuardMiddleware</c>'s own cache is safe because setup completion is monotonic —
/// once true it is never false again — but maintenance mode toggles on and off by design, and a
/// panel restart with a cold cache would either serve a customer's real app when the flag says
/// otherwise or the reverse. Correctness over one avoidable query per request.
/// </para>
/// </summary>
public sealed class MaintenanceModeMiddleware(RequestDelegate next)
{
    public const string MaintenancePath = "/__maintenance";

    /// <summary>Where the resolved <see cref="MaintenanceInfo"/> is handed to the controller.</summary>
    public const string ContextKey = "Harbora.MaintenanceApp";

    public async Task InvokeAsync(HttpContext context, HarboraDbContext db)
    {
        var host = context.Request.Host.Host;

        if (!string.IsNullOrEmpty(host))
        {
            // Unfiltered on both sides — a visitor here has no session and no workspace, and the
            // tenant-filter trap's own prescribed shape is IgnoreQueryFilters plus an explicit,
            // narrow predicate rather than trusting whatever scope this DbContext happens to carry.
            // Host is what narrows it: DomainName.Host is unique across the whole platform, so this
            // can resolve at most one app regardless of whose domain it turns out to be.
            var hit = await db.Domains.IgnoreQueryFilters()
                .Where(d => d.Host == host)
                .Join(
                    db.Apps.IgnoreQueryFilters().Where(a => a.MaintenanceMode),
                    d => d.AppId, a => a.Id,
                    (d, a) => new MaintenanceInfo(a.Id, a.Name, a.MaintenanceMessage, a.MaintenanceMessageFa, a.MaintenanceSince))
                .FirstOrDefaultAsync(context.RequestAborted);

            if (hit is not null)
            {
                context.Items[ContextKey] = hit;
                context.Request.Path = MaintenancePath;
                context.Request.QueryString = QueryString.Empty;
            }
        }

        await next(context);
    }
}
