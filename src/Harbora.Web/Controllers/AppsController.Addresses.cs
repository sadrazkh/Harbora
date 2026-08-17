using Harbora.Application.Abstractions;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Infrastructure.Networking;
using Microsoft.AspNetCore.Authorization;
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
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> Addresses(CancellationToken ct)
    {
        var rootDomain = await addresses.RootDomainAsync(ct);

        // Same project-visibility filter Index applies: this rewrites live Traefik routing, and
        // listing — or, on the POST below, writing — an app in a project the caller is not scoped to
        // would be the least-gated write in the controller.
        var query = db.Apps.Where(a => a.WorkspaceId == WorkspaceId && !a.Domains.Any());
        if (await access.VisibleProjectIdsAsync(ct) is { } visible)
            query = query.Where(a => visible.Contains(a.Environment!.ProjectId));

        var addressless = await query.OrderBy(a => a.Slug).ToListAsync(ct);

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
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> ApplyAddresses(CancellationToken ct)
    {
        // Only apps with no domain at all. An app that already has one is never touched: the failure
        // worth guarding against here is not "an app has no address", it is "an app that had a working
        // custom domain lost it".
        var query = db.Apps.Include(a => a.Domains)
            .Where(a => a.WorkspaceId == WorkspaceId && !a.Domains.Any());
        if (await access.VisibleProjectIdsAsync(ct) is { } visible)
            query = query.Where(a => visible.Contains(a.Environment!.ProjectId));

        var addressless = await query.ToListAsync(ct);

        // Weighed as one batch, before anything is written — the way EnvironmentCloner.QuotaRefusalAsync
        // weighs a whole clone. Asking per app would let eleven apps each get a true answer and still
        // land eleven domains in a ten-domain plan, which is the exact defect fc4993d fixed for cloning
        // and this screen would otherwise still have.
        await using var quotaReservation = await quota.AcquireCreationLockAsync(WorkspaceId, ct);
        var willGetOne = 0;
        foreach (var app in addressless)
            if ((await addresses.PreviewAsync(app, ct)).HasAddress)
                willGetOne++;

        var governed = await quota.CanAddGovernedResourcesAsync(
            WorkspaceId, new GovernanceQuotaDelta(Domains: willGetOne), ct);
        if (!governed.Allowed)
        {
            TempData["Error"] = (IsFa ? governed.ReasonFa : null) ?? governed.Reason ?? "Plan quota exceeded.";
            return RedirectToAction(nameof(Addresses));
        }

        var given = 0;
        foreach (var app in addressless)
            if ((await addresses.AssignAsync(
                    app, requested: null, AppAddressRequestOrigin.Derived, suffix: null, ct)).HasAddress)
                given++;

        await db.SaveChangesAsync(ct);
        await quotaReservation.CommitAsync(ct);

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
