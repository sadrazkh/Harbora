namespace Harbora.Infrastructure.Navigation;

/// <summary>One of the side panels a person can fold away.</summary>
public enum RailPanel
{
    /// <summary>
    /// The shelf of ready-made things offered beside a list — apps on <c>/apps</c>, database engines
    /// on <c>/databases</c>. One preference for both: it is the same "things to create" shelf, just
    /// filled in with whatever the page is a list of.
    ///
    /// <para>
    /// The member name follows <c>Apps/Index.cshtml</c>, where the panel is labelled "Quick start" /
    /// "شروع سریع". <c>Databases/Index.cshtml</c> draws the same panel under a different label,
    /// "Quick provision" / "ساخت سریع" — deliberate, not a naming slip: renaming the enum member to
    /// chase whichever page's label is currently on screen would just move the mismatch, since one
    /// value backs two labels either way.
    /// </para>
    /// </summary>
    QuickStart = 0,

    /// <summary>The counts and the status bar on <c>/apps</c>.</summary>
    Overview = 1
}

/// <summary>
/// Whether a side panel is open, for this person, on every device.
///
/// The same three-layer rule as <see cref="PanelModeResolver"/>, and for the same reason: a
/// preference kept in the browser is a property of the laptop rather than of the person, so
/// somebody who folded Quick start away meets it again on their phone — and, since the same
/// preference now spans pages too, on the Databases list as well as the Apps one. What differs is
/// that the shipped answer is not the same for both panels — Quick start is a shelf of things to
/// install and is in the way once somebody has installed them, and Overview is the count of what
/// they already have, which is the reason the Apps page exists.
///
/// Folding is never removing. The panel keeps its heading and its toggle when closed, or the
/// setting becomes a way to lose a feature and never find it again — the same discipline
/// <see cref="PanelSections"/> holds inside a form. With every panel on a page closed there is
/// nothing left to fold to, so the rail itself is not drawn — <c>RailPreferences.AnyOpenAsync</c> is
/// what a page checks before deciding whether to draw anything at all, and each page that can reach
/// that state keeps a way to reopen it outside the rail, in its own toolbar.
/// </summary>
public static class RailVisibility
{
    /// <summary>
    /// What each panel does when nobody has said otherwise: closed. The rail used to reserve its
    /// column whether or not a panel was drawn in it, so an open-by-default panel cost every visitor
    /// the same width whether they wanted it or not. Somebody who opens a panel keeps seeing it —
    /// <see cref="Resolve"/> never overrides a stored choice — so this only changes what a person who
    /// has never touched the rail meets on their first visit.
    /// </summary>
    public static bool ShippedDefault(RailPanel panel) => false;

    /// <summary>
    /// Whether to draw the panel open.
    /// </summary>
    /// <param name="userChoice">What this person chose, or null if they never chose.</param>
    /// <param name="platformDefault">What an operator set for people who have not chosen, or null.</param>
    public static bool Resolve(bool? userChoice, bool? platformDefault, RailPanel panel)
    {
        // An explicit choice is never overridden. Both values matter: somebody who deliberately
        // closed a panel has said something, and treating that "closed" as "no answer" would reopen
        // it on every visit against a default they cannot see.
        if (userChoice is { } chosen) return chosen;

        return platformDefault ?? ShippedDefault(panel);
    }

    /// <summary>
    /// Reads a stored setting value. Anything that is not plainly true or false is no answer at all
    /// rather than false — a setting cleared to an empty string means "follow the shipped default",
    /// and reading it as "closed" would hide a panel nobody chose to hide.
    /// </summary>
    public static bool? ParseSetting(string? stored) => stored?.Trim().ToLowerInvariant() switch
    {
        "true" => true,
        "false" => false,
        _ => null
    };

    /// <summary>How a choice is stored. Null clears it, which is how a person goes back to the default.</summary>
    public static string Format(bool? choice) => choice switch
    {
        true => "true",
        false => "false",
        _ => string.Empty
    };
}
