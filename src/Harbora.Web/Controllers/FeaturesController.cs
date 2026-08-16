using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Features;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Both halves of entitlements: the page a customer lands on when something is locked, and the
/// console where the owner decides who it is locked for.
///
/// <para>
/// They live together because they are one sentence read from two ends — the customer sees "not on
/// your plan", the owner sees the row that says so — and separating them is how the two drift into
/// describing different rules.
/// </para>
/// </summary>
[Authorize]
[Route("features")]
public sealed class FeaturesController(
    HarboraDbContext db,
    IFeatureGate gate,
    ICurrentUser currentUser,
    IAuditLogger audit) : Controller
{
    private bool IsFa => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    /// <summary>
    /// What a customer sees instead of the feature.
    ///
    /// <para>
    /// Reachable by anybody, including somebody the feature is enabled for — a link in a support
    /// message has to work either way, and the page says which of the two they are.
    /// </para>
    /// </summary>
    [HttpGet("{key}")]
    public async Task<IActionResult> Locked(string key, CancellationToken ct)
    {
        var feature = PlatformFeatures.Find(key);
        if (feature is null) return NotFound();

        var verdict = currentUser.WorkspaceId is { } workspaceId
            ? await gate.EvaluateAsync(workspaceId, key, ct)
            : FeatureAccess.Resolve(key, null, null);

        // Hidden is the operator saying they do not sell this. Advertising it on a page of its own
        // would undo that decision in the one place a curious customer is most likely to look.
        if (verdict.State == FeatureState.Hidden) return NotFound();

        ViewData["Title"] = feature.Name(IsFa);
        return View(new FeatureLockedViewModel(feature, verdict));
    }

    // ------------------------------------------------------------ the console

    /// <summary>Every feature against every plan, plus the per-workspace exceptions.</summary>
    [HttpGet("admin")]
    [Authorize(Policy = Capabilities.PlatformManage)]
    public async Task<IActionResult> Admin(CancellationToken ct)
    {
        ViewData["Title"] = IsFa ? "قابلیت‌ها" : "Features";

        var plans = await db.Plans.IgnoreQueryFilters().AsNoTracking()
            .OrderByDescending(p => p.IsDefault).ThenBy(p => p.MonthlyPrice).ThenBy(p => p.Name)
            .ToListAsync(ct);

        var grants = await db.FeatureGrants.AsNoTracking().ToListAsync(ct);

        var workspaces = await db.Workspaces.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.DeletedAt == null)
            .OrderBy(w => w.Name)
            .Select(w => new { w.Id, w.Name, w.Slug, w.PlanId })
            .ToListAsync(ct);

        var overrides = grants
            .Where(g => g.Scope == FeatureScope.Workspace)
            .Select(g => new FeatureOverrideRow(
                g.Id,
                g.FeatureKey,
                g.TargetId,
                workspaces.FirstOrDefault(w => w.Id == g.TargetId)?.Name
                    // A grant whose workspace has been deleted is shown rather than swallowed: it is
                    // dead configuration, and the only place anybody would ever find it is here.
                    ?? (IsFa ? "(ورک‌اسپیس حذف‌شده)" : "(deleted workspace)"),
                g.State,
                g.Note))
            .OrderBy(o => o.WorkspaceName)
            .ToList();

        return View(new FeatureAdminViewModel(
            PlatformFeatures.All,
            plans.Select(p => new FeaturePlanRow(p.Id, p.Name, p.NameFa, p.IsDefault,
                PlatformFeatures.All.ToDictionary(
                    f => f.Key,
                    f => grants.FirstOrDefault(g => g.Scope == FeatureScope.Plan
                                                 && g.TargetId == p.Id
                                                 && g.FeatureKey == f.Key)?.State ?? FeatureState.Inherit)))
                .ToList(),
            overrides,
            workspaces.Select(w => new FeatureWorkspaceOption(w.Id, w.Name, w.Slug)).ToList()));
    }

    /// <summary>Sets — or clears — one decision at one level.</summary>
    [HttpPost("admin/set")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.PlatformManage)]
    /// <param name="returnUrl">
    /// Where to go back to. The tenant page posts here so there is one writer rather than two, and
    /// sending an operator to the console after they changed something on a customer's page would
    /// lose their place. Local URLs only — <see cref="Url.IsLocalUrl"/> — because a return address
    /// taken from a form is otherwise an open redirect.
    /// </param>
    public async Task<IActionResult> Set(
        FeatureScope scope, Guid targetId, string featureKey, FeatureState state, string? note,
        string? returnUrl, CancellationToken ct)
    {
        if (PlatformFeatures.Find(featureKey) is null) return NotFound();

        // The target has to exist. Without this a mistyped id becomes a grant that decides nothing
        // and can never be found again by looking at any plan or workspace.
        var targetExists = scope == FeatureScope.Plan
            ? await db.Plans.IgnoreQueryFilters().AnyAsync(p => p.Id == targetId, ct)
            : await db.Workspaces.IgnoreQueryFilters().AnyAsync(w => w.Id == targetId, ct);
        if (!targetExists) return NotFound();

        var grant = await db.FeatureGrants
            .FirstOrDefaultAsync(g => g.Scope == scope && g.TargetId == targetId && g.FeatureKey == featureKey, ct);

        if (state == FeatureState.Inherit)
        {
            // "No decision here" is the absence of a row, not a row saying nothing: leaving one
            // behind would make the console show an exception where there is none.
            if (grant is not null) db.FeatureGrants.Remove(grant);
        }
        else if (grant is null)
        {
            db.FeatureGrants.Add(new FeatureGrant
            {
                Scope = scope,
                TargetId = targetId,
                FeatureKey = featureKey,
                State = state,
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                SetByUserId = currentUser.UserId
            });
        }
        else
        {
            grant.State = state;
            grant.Note = string.IsNullOrWhiteSpace(note) ? grant.Note : note.Trim();
            grant.SetByUserId = currentUser.UserId;
            grant.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        await audit.LogAsync("features.set", scope.ToString(), targetId.ToString(),
            metadataJson: System.Text.Json.JsonSerializer.Serialize(
                new { featureKey, state = state.ToString(), note }),
            ct: ct);

        TempData["Message"] = IsFa ? "ذخیره شد." : "Saved.";
        return returnUrl is { Length: > 0 } && Url.IsLocalUrl(returnUrl)
            ? Redirect(returnUrl)
            : RedirectToAction(nameof(Admin));
    }
}
