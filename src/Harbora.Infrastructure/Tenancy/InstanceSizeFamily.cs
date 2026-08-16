namespace Harbora.Infrastructure.Tenancy;

/// <summary>
/// Which kind of machine a resource tier is — general purpose, or weighted towards processor,
/// memory or disk.
///
/// <para>
/// <b>A family belongs to the tier, not to the server.</b> A memory-heavy tier is memory-heavy
/// wherever it runs; a server either offers it or does not. Labelling the server instead would
/// create a second source of truth that its own offers could contradict — a box badged
/// "memory-optimised" while offering only general tiers — and nothing would report the
/// disagreement. The badges a server wears are therefore derived from the families it offers.
/// </para>
///
/// <para>
/// A string rather than an enum, because the provider can already add sizes of their own and a
/// family nobody anticipated must not be a migration. The consequence is handled rather than
/// ignored: see <see cref="Label"/>.
/// </para>
/// </summary>
public static class InstanceSizeFamily
{
    /// <summary>The ordinary tier, and what every tier that predates the column is.</summary>
    public const string General = "general";

    public const string Cpu = "cpu";
    public const string Memory = "memory";
    public const string Storage = "storage";

    /// <summary>
    /// The families this build ships, in the order a chooser should offer them.
    ///
    /// <para>
    /// Deliberately not alphabetical. General comes first because it is what most people want, and a
    /// strip sorted by name would open on "cpu" and put the specialist tier in front of the common
    /// one.
    /// </para>
    /// </summary>
    private static readonly string[] KnownOrder = [General, Cpu, Memory, Storage];

    /// <inheritdoc cref="KnownOrder"/>
    public static IReadOnlyList<string> Known => KnownOrder;

    /// <summary>
    /// The family to store. Blank becomes <see cref="General"/>, and anything else is normalised the
    /// way a size's key is.
    ///
    /// <para>
    /// Blank is <see cref="General"/> rather than a fifth family with an empty name: every tier that
    /// predates this column has nothing in it, and they are ordinary tiers, not nameless ones. That
    /// is what stops an install upgrading into a chooser with a blank tab on it.
    /// </para>
    ///
    /// <para>
    /// Normalised through <see cref="InstanceSizeKey.Normalise"/> rather than by a second rule of its
    /// own. "Memory" stored on one size and "memory" on the next would split one tab into two, each
    /// holding half the tiers — the same failure a key with a capital in it causes, so it gets the
    /// same answer.
    /// </para>
    /// </summary>
    public static string Normalise(string? family) =>
        InstanceSizeKey.Normalise(family) ?? General;

    /// <summary>
    /// How a family reads to somebody choosing a tier.
    ///
    /// <para>
    /// <b>A family this code has never heard of reads under its own key.</b> Not hidden, because
    /// hiding it would take a priced tier off the chooser — capacity a customer cannot buy and an
    /// operator cannot see they are not selling. And not folded into <see cref="General"/> either,
    /// because that would file the tier under a family it is not in, which is a smaller lie told
    /// more confidently.
    /// </para>
    /// </summary>
    public static string Label(string? family, bool isFa) => Normalise(family) switch
    {
        General => isFa ? "عمومی" : "General purpose",
        Cpu => isFa ? "بهینهٔ پردازنده" : "CPU-optimised",
        Memory => isFa ? "بهینهٔ حافظه" : "Memory-optimised",
        Storage => isFa ? "بهینهٔ ذخیره‌سازی" : "Storage-optimised",
        var unknown => unknown
    };

    /// <summary>
    /// The distinct families among the tiers given, ordered for a tab strip: the ones this build
    /// knows in <see cref="Known"/> order, then the rest by name.
    ///
    /// <para>
    /// The unknown ones are ordered among themselves rather than left in whatever order the rows
    /// arrived in, so the strip does not reshuffle between requests — a tab that moves is a tab
    /// somebody clicks by mistake.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Present(IEnumerable<string?> families)
    {
        // Normalised on the way in rather than trusted: these arrive off rows that may predate the
        // column or have been written by an older build, and one blank among them would otherwise
        // become a tab with no name on it.
        var distinct = families.Select(Normalise).Distinct(StringComparer.Ordinal).ToList();

        return distinct
            .OrderBy(f => Array.IndexOf(KnownOrder, f) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(f => f, StringComparer.Ordinal)
            .ToList();
    }
}
