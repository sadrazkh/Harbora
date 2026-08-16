namespace Harbora.Domain.Features;

/// <summary>
/// What one workspace may do with one feature.
///
/// <para>
/// <b>Persisted as an int on <see cref="FeatureGrant"/>. Append only</b>, like every other enum this
/// platform stores — a reordered member does not mislabel a row, it silently re-decides who is
/// entitled to what.
/// </para>
///
/// <para>
/// The distinction that matters is between <see cref="Locked"/> and <see cref="Hidden"/>, and it is
/// a product decision rather than a technical one. Locked is shown, greyed, with a reason and a way
/// to ask — it is how a paid tier advertises itself. Hidden is not shown at all, for the feature an
/// operator does not sell and does not want asked about. Both are refused by the server; the only
/// difference is whether the customer is told it exists.
/// </para>
/// </summary>
public enum FeatureState
{
    /// <summary>
    /// No decision at this level — defer to the level above. Only ever stored on a grant; it is
    /// never an answer, and <c>FeatureAccess.Resolve</c> cannot return it.
    /// </summary>
    Inherit = 0,

    /// <summary>Usable.</summary>
    Enabled = 1,

    /// <summary>Visible, greyed, refused. The state this whole mechanism exists for.</summary>
    Locked = 2,

    /// <summary>Absent from the panel entirely, and refused.</summary>
    Hidden = 3
}

/// <summary>Which level a stored decision belongs to.</summary>
public enum FeatureScope
{
    /// <summary>Applies to every workspace on one tenancy plan.</summary>
    Plan = 0,

    /// <summary>Applies to one workspace, and outranks its plan.</summary>
    Workspace = 1
}
