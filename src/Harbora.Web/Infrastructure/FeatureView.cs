using Harbora.Application.Abstractions;
using Harbora.Domain.Features;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// What a view needs to draw a locked control, without every view learning how entitlements resolve.
///
/// <para>
/// Injected into the layout and any page that offers something gated, so the lock looks the same
/// everywhere: one grey style, one lock icon, one link to the page that explains it. A view that
/// hand-rolled its own <c>opacity-50</c> would be a second answer to "is this available", free to
/// disagree with the filter that actually refuses.
/// </para>
/// </summary>
public sealed class FeatureView(IFeatureGate gate, ICurrentUser currentUser)
{
    private IReadOnlyDictionary<string, FeatureVerdict>? _all;

    public async Task<FeatureVerdict> GetAsync(string key, CancellationToken ct = default)
    {
        if (currentUser.WorkspaceId is not { } workspaceId)
            return FeatureAccess.Resolve(key, null, null);

        _all ??= await gate.EvaluateAllAsync(workspaceId, ct);
        return _all.TryGetValue(key, out var verdict) ? verdict : FeatureAccess.Resolve(key, null, null);
    }

    public async Task<bool> IsEnabledAsync(string key, CancellationToken ct = default) =>
        (await GetAsync(key, ct)).IsEnabled;

    /// <summary>Shown at all — enabled or locked, but not hidden.</summary>
    public async Task<bool> IsVisibleAsync(string key, CancellationToken ct = default) =>
        (await GetAsync(key, ct)).IsVisible;

    /// <summary>
    /// The sentence beside a greyed control. Deliberately says who can lift it rather than only that
    /// it is off: "not available on your plan" with no next step is the message people file a ticket
    /// about anyway.
    /// </summary>
    public static string Explain(FeatureVerdict verdict, bool isFa) => verdict.DecidedBy switch
    {
        FeatureDecision.Workspace when isFa => "این قابلیت برای این ورک‌اسپیس فعال نشده است. از مالک پلتفرم بخواهید آن را روشن کند.",
        FeatureDecision.Workspace => "This feature is switched off for this workspace. Ask the platform owner to enable it.",
        FeatureDecision.Plan when isFa => "این قابلیت در پلن فعلی شما نیست. برای فعال‌سازی با مالک پلتفرم تماس بگیرید.",
        FeatureDecision.Plan => "This feature is not part of your current plan. Contact the platform owner to enable it.",
        _ when isFa => "این قابلیت هنوز برای شما فعال نشده است.",
        _ => "This feature has not been enabled for you yet."
    };
}
