using Harbora.Data;
using Harbora.Domain.Settings;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// Until first-run setup completes, funnel every request to the setup wizard (except the
/// wizard itself and static assets). Once completed the check is a cheap cached read.
/// </summary>
public sealed class SetupGuardMiddleware(RequestDelegate next)
{
    private static bool _setupCompleted;

    public async Task InvokeAsync(HttpContext context, HarboraDbContext db)
    {
        var path = context.Request.Path.Value ?? "/";

        if (_setupCompleted || IsExempt(path))
        {
            await next(context);
            return;
        }

        var completed = await db.Settings
            .Where(s => s.Key == SettingKeys.SetupCompleted)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(context.RequestAborted);

        if (completed == "true")
        {
            _setupCompleted = true;
            await next(context);
            return;
        }

        if (!path.StartsWith("/setup", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Redirect("/setup");
            return;
        }

        await next(context);
    }

    /// <summary>Call after setup succeeds so subsequent requests skip the DB check.</summary>
    public static void MarkCompleted() => _setupCompleted = true;

    private static bool IsExempt(string path) =>
        path.StartsWith("/setup", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/webhooks", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/healthz", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/css", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/js", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/build", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/manifest", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/sw.js", StringComparison.OrdinalIgnoreCase);
}
