using Harbora.Application.Abstractions;
using Harbora.Domain.Authorization;
using Harbora.Infrastructure.Verification;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>"Is it working?", as a button on an app's own page rather than only at the end of a launch.</summary>
public sealed partial class AppsController
{
    /// <summary>How long a press waits for something unsettled, like a certificate, before calling it a failure.</summary>
    private static readonly TimeSpan VerifyPatience = TimeSpan.FromSeconds(30);

    /// <summary>Past this, a report still waiting is yesterday's question rather than the one being asked.</summary>
    private static readonly TimeSpan UnfinishedVerifyLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Runs the checks now and goes back to the card, which reads what was stored.
    ///
    /// <para>
    /// A press that finds a run still waiting continues it, with the same start time. If every press
    /// began its own thirty seconds, an address that will never answer would say "waiting" for ever,
    /// however many times somebody pressed: the give-up time would always be ahead of them.
    /// </para>
    /// </summary>
    [HttpPost("apps/{id:guid}/verify")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsOperate)]
    public async Task<IActionResult> Verify(
        Guid id, [FromServices] IAppVerifier verifier, [FromServices] ISystemClock clock, CancellationToken ct)
    {
        if (!await MayAsync(id, Capabilities.AppsOperate, ct)) return NotFound();
        if (!await db.Apps.AnyAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct)) return NotFound();

        var now = clock.UtcNow;
        var latest = await verifier.GetLatestAsync(id, ct);
        var unfinished = latest is { IsComplete: false } && now - latest.StartedAt < UnfinishedVerifyLifetime
            ? latest
            : null;

        await verifier.RunAsync(id, unfinished, (unfinished?.StartedAt ?? now) + VerifyPatience, ct);

        return RedirectToAction(nameof(Details), null, new { id }, "app-checks");
    }
}
