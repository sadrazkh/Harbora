using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Features;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Features;

/// <summary>
/// Resolves entitlements out of the two grant levels and the shipped defaults.
///
/// <para>
/// Every decision it makes comes from <see cref="FeatureAccess.Resolve"/>; this class only fetches
/// rows. That split is the point — precedence is the part with behaviour worth testing, and it is
/// tested without any of this.
/// </para>
///
/// <para>
/// Answers are memoised per instance, which is per request for the web and per job for background
/// work. A sidebar asks about every feature and then a filter asks again about the one the page
/// needs; without this that is two round trips for an answer that cannot have changed in between.
/// </para>
/// </summary>
public sealed class FeatureGate(HarboraDbContext db) : IFeatureGate
{
    private readonly Dictionary<Guid, IReadOnlyDictionary<string, FeatureVerdict>> _memo = [];

    public async Task<FeatureVerdict> EvaluateAsync(Guid workspaceId, string featureKey, CancellationToken ct)
    {
        var all = await EvaluateAllAsync(workspaceId, ct);
        return all.TryGetValue(featureKey, out var verdict)
            ? verdict
            // A key nothing in the catalogue knows about resolves to the shipped default for an
            // unknown key, which is Hidden. Fails closed: a typo in an attribute locks a page rather
            // than opening one.
            : FeatureAccess.Resolve(featureKey, null, null);
    }

    public async Task<IReadOnlyDictionary<string, FeatureVerdict>> EvaluateAllAsync(
        Guid workspaceId, CancellationToken ct)
    {
        if (_memo.TryGetValue(workspaceId, out var cached)) return cached;

        // A workspace with no plan is on the platform default plan, exactly as IQuotaService reads
        // it — a customer nobody assigned a plan to is not a customer entitled to everything.
        var planId = await db.Workspaces
            .Where(w => w.Id == workspaceId)
            .Select(w => w.PlanId)
            .FirstOrDefaultAsync(ct);

        planId ??= await db.Plans
            .Where(p => p.IsDefault && p.IsEnabled)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(ct);

        // One query for both levels. The scope discriminator keeps a plan id and a workspace id
        // from ever being confused for one another even though both live in TargetId.
        var grants = await db.FeatureGrants
            .Where(g => (g.Scope == FeatureScope.Workspace && g.TargetId == workspaceId)
                     || (g.Scope == FeatureScope.Plan && planId != null && g.TargetId == planId))
            .ToListAsync(ct);

        var resolved = PlatformFeatures.All.ToDictionary(
            feature => feature.Key,
            feature => FeatureAccess.Resolve(
                feature.Key,
                plan: State(grants, FeatureScope.Plan, feature.Key),
                workspace: State(grants, FeatureScope.Workspace, feature.Key)));

        _memo[workspaceId] = resolved;
        return resolved;
    }

    private static FeatureState? State(List<FeatureGrant> grants, FeatureScope scope, string key) =>
        grants.FirstOrDefault(g => g.Scope == scope && g.FeatureKey == key)?.State;
}
