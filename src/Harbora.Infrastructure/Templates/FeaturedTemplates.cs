namespace Harbora.Infrastructure.Templates;

/// <summary>
/// Which ready-made apps are put in front of people, and in what order.
///
/// The dashboard used to take the first eight templates alphabetically. That is not a choice
/// anybody made — it is what happens when nobody is asked — so the apps an operator most wants
/// installed sat wherever their name put them, and a template added later could quietly push one
/// off the page.
/// </summary>
public static class FeaturedTemplates
{
    /// <summary>How many the dashboard has room for.</summary>
    public const int Slots = 8;

    /// <summary>The stored form: keys in the operator's order, comma separated.</summary>
    public static string Format(IEnumerable<string> keys) =>
        string.Join(",", keys.Select(k => k?.Trim().ToLowerInvariant())
            .Where(k => !string.IsNullOrEmpty(k))
            .Distinct(StringComparer.Ordinal));

    /// <summary>The keys as stored, in order, ignoring anything empty.</summary>
    public static IReadOnlyList<string> Parse(string? stored) =>
        (stored ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(k => k.ToLowerInvariant())
        .Distinct(StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// What to show, in order.
    /// </summary>
    /// <param name="chosen">The operator's list. Empty falls back to the order given.</param>
    /// <param name="available">Every key that really exists, in the order to use as a fallback.</param>
    /// <remarks>
    /// A chosen key that no longer exists is skipped rather than leaving a hole: a template can be
    /// withdrawn, and the dashboard should not show a gap where it was — or worse, a tile that leads
    /// nowhere.
    /// </remarks>
    public static IReadOnlyList<string> Resolve(
        IReadOnlyCollection<string> chosen, IReadOnlyList<string> available, int slots = Slots)
    {
        if (slots <= 0) return [];

        var exists = available.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var picked = chosen.Where(exists.Contains).Take(slots).ToList();

        // Not padded from the rest. If somebody chose three, they chose three — filling the other
        // five with whatever came next alphabetically is the behaviour this replaces.
        return picked.Count > 0 ? picked : available.Take(slots).ToList();
    }
}
