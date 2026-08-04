using Harbora.Domain.Identity;

namespace Harbora.Infrastructure.Navigation;

/// <summary>
/// Whether a specialist block on a page starts open.
///
/// Simple mode folds; it does not remove. Every control stays in the markup, one click away, with a
/// label saying what is inside — a form that quietly drops fields between modes is one where a
/// person's settings change depending on a preference they set weeks ago, and nothing on screen
/// says so.
///
/// The rule that matters is the second one. A block folded over a field the server just rejected is
/// a form that reports an error about a control the person cannot see, and no amount of re-reading
/// the page will show it to them.
/// </summary>
public static class PanelSections
{
    /// <summary>
    /// True when the block should be rendered expanded.
    /// </summary>
    /// <param name="mode">The panel mode this person is in.</param>
    /// <param name="hasErrors">
    /// Whether the form was rejected. Any rejection opens every specialist block, rather than only
    /// the one containing the offending field: working out which block a model-state key belongs to
    /// is a mapping that goes stale the first time somebody moves a field, and being wrong means the
    /// error is invisible.
    /// </param>
    public static bool StartsOpen(PanelMode mode, bool hasErrors = false) =>
        hasErrors || mode == PanelMode.Advanced;
}
