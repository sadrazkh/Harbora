using Harbora.Domain.Common;

namespace Harbora.Domain.Features;

/// <summary>
/// One stored decision about one feature, at one level.
///
/// <para>
/// Platform configuration, not tenant data — a grant is read by background work that has no session
/// and therefore no workspace, so this table carries <b>no global workspace query filter</b>. The
/// alternative has already been paid for once here: a filtered table read without a session returns
/// nothing, and the caller reports success having decided nobody is entitled to anything.
/// </para>
/// </summary>
public class FeatureGrant : BaseEntity
{
    public FeatureScope Scope { get; set; }

    /// <summary>The plan or workspace this decision is about. Which one is told by <see cref="Scope"/>.</summary>
    public Guid TargetId { get; set; }

    /// <summary>A key from <see cref="PlatformFeatures"/>.</summary>
    public string FeatureKey { get; set; } = string.Empty;

    public FeatureState State { get; set; } = FeatureState.Inherit;

    /// <summary>
    /// Why, in the operator's own words. Shown beside the grant in the console — six months later
    /// "enabled for this one customer" without a reason is indistinguishable from a mistake.
    /// </summary>
    public string? Note { get; set; }

    public Guid? SetByUserId { get; set; }
}

/// <summary>
/// The answer, and which level gave it.
///
/// <para>
/// <see cref="DecidedBy"/> is not decoration: "why can this customer not use Functions" is the
/// question an owner asks, and a bare state cannot tell them whether the plan withheld it or
/// somebody switched it off for this workspace specifically.
/// </para>
/// </summary>
/// <param name="Key">The feature asked about.</param>
/// <param name="State">Never <see cref="FeatureState.Inherit"/>.</param>
/// <param name="DecidedBy">Which level supplied <paramref name="State"/>.</param>
public readonly record struct FeatureVerdict(string Key, FeatureState State, FeatureDecision DecidedBy)
{
    public bool IsEnabled => State == FeatureState.Enabled;

    /// <summary>Shown, greyed and refused — the state the whole mechanism exists for.</summary>
    public bool IsLocked => State == FeatureState.Locked;

    /// <summary>Whether the panel mentions it at all. Locked is visible; hidden is not.</summary>
    public bool IsVisible => State is FeatureState.Enabled or FeatureState.Locked;
}

/// <summary>Which level decided a verdict.</summary>
public enum FeatureDecision
{
    /// <summary>Nobody has decided; this is what the product ships with.</summary>
    ShippedDefault = 0,
    Plan = 1,
    Workspace = 2
}

/// <summary>
/// The resolution rule, as a pure function.
///
/// <para>
/// Deliberately separated from the service that loads rows, so the part with the actual behaviour —
/// precedence, and what an absent decision means — is testable without a database, a workspace or a
/// session.
/// </para>
/// </summary>
public static class FeatureAccess
{
    /// <summary>
    /// Most specific wins: workspace over plan over the shipped default. <see cref="FeatureState.Inherit"/>
    /// at any level means "no decision here", which is what makes a stored row removable without
    /// deleting it.
    /// </summary>
    public static FeatureVerdict Resolve(string key, FeatureState? plan, FeatureState? workspace)
    {
        if (workspace is { } w && w != FeatureState.Inherit)
            return new FeatureVerdict(key, w, FeatureDecision.Workspace);

        if (plan is { } p && p != FeatureState.Inherit)
            return new FeatureVerdict(key, p, FeatureDecision.Plan);

        return new FeatureVerdict(key, PlatformFeatures.DefaultFor(key), FeatureDecision.ShippedDefault);
    }
}
