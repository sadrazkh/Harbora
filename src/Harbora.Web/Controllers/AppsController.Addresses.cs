using Harbora.Domain.Apps;
using Harbora.Infrastructure.Networking;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>What an app without an address would be called, and the button that gives it one.</summary>
/// <param name="Slug">The app's slug, so the operator recognises it.</param>
/// <param name="Candidate">The hostname it would be given, or null with a reason.</param>
/// <param name="Reason">Why it would get nothing — null when Candidate is set.</param>
public record AppAddressCandidate(Guid Id, string Name, string Slug, string? Candidate, string? Reason);

public sealed record AppAddressPreviewViewModel(
    string? RootDomain, IReadOnlyList<AppAddressCandidate> Candidates);

public sealed partial class AppsController
{
    /// <summary>
    /// Which apps have no address, and what each would be given.
    ///
    /// A preview rather than a sweep: this rewrites live Traefik routing, and an operator who cannot
    /// see what a button will do before pressing it has not been given a choice.
    /// </summary>
    [HttpGet("apps/addresses")]
    public async Task<IActionResult> Addresses(CancellationToken ct)
    {
        var rootDomain = await addresses.RootDomainAsync(ct);

        var addressless = await db.Apps
            .Where(a => a.WorkspaceId == WorkspaceId && !a.Domains.Any())
            .OrderBy(a => a.Slug)
            .ToListAsync(ct);

        var candidates = new List<AppAddressCandidate>();
        foreach (var app in addressless)
        {
            // PreviewAsync, not a second copy of the rule: a preview computed separately from the
            // assignment is a preview that can disagree with what the button does, and the whole point
            // of this screen is that it does not.
            var decision = await addresses.PreviewAsync(app, ct);
            candidates.Add(new AppAddressCandidate(
                app.Id, app.Name, app.Slug, decision.Host, ReasonFor(decision.Outcome)));
        }

        return View(new AppAddressPreviewViewModel(rootDomain, candidates));
    }

    /// <summary>Gives every listed app the address the preview showed.</summary>
    [HttpPost("apps/addresses")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyAddresses(CancellationToken ct)
    {
        // Only apps with no domain at all. An app that already has one is never touched: the failure
        // worth guarding against here is not "an app has no address", it is "an app that had a working
        // custom domain lost it".
        var addressless = await db.Apps
            .Include(a => a.Domains)
            .Where(a => a.WorkspaceId == WorkspaceId && !a.Domains.Any())
            .ToListAsync(ct);

        var given = 0;
        foreach (var app in addressless)
            if ((await addresses.AssignAsync(app, requested: null, suffix: null, ct)).HasAddress)
                given++;

        await db.SaveChangesAsync(ct);

        TempData["Message"] = IsFa
            ? $"{given} اپ آدرس گرفت."
            : $"{given} app(s) were given an address.";
        return RedirectToAction(nameof(Addresses));
    }

    private string? ReasonFor(AppAddressOutcome outcome) => outcome switch
    {
        AppAddressOutcome.KindTakesNoTraffic => IsFa
            ? "این سرویس ترافیک ورودی ندارد، پس آدرسی نمی‌گیرد."
            : "This service takes no inbound traffic, so it gets no address.",
        AppAddressOutcome.NoRootDomain => IsFa
            ? "دامنهٔ اصلی پلتفرم تنظیم نشده است."
            : "No platform root domain is configured.",
        AppAddressOutcome.Reserved => IsFa
            ? "این نام یکی از نام‌های خودِ سامانه است."
            : "That name is one of the platform's own.",
        _ => null
    };
}
