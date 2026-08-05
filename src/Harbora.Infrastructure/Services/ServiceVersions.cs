using Harbora.Infrastructure.Templates;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// Which versions of a database engine are offered, once an operator has had their say.
///
/// The list was two entries per engine, written in C#: PostgreSQL was "16-alpine, 15-alpine" and
/// nothing else, forever. Offering 17, or keeping 14 for an application that needs it, took a
/// release — while the applications sitting next to them had a whole page for exactly this.
///
/// The shipped list stays as the fallback, so an operator who never opens the setting sees no
/// change, and an override that empties out falls back rather than leaving a dropdown with nothing
/// in it — which is a create form nobody can submit.
/// </summary>
public static class ServiceVersions
{
    /// <summary>
    /// Reads a stored override into a list. Entries that could not be a container tag are dropped
    /// rather than carried: they would appear in the dropdown and fail at the image pull.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return [];

        // Case-sensitive, because a container tag is. MinIO publishes
        // "RELEASE.2024-10-13T13-34-11Z"; folding case here would let one legitimate tag silently
        // swallow another that happens to differ only in capitalisation.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var versions = new List<string>();

        foreach (var piece in stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!ImageReference.IsUsableTag(piece)) continue;
            if (seen.Add(piece)) versions.Add(piece);
        }

        return versions;
    }

    /// <summary>How a list is stored. Round-trips through <see cref="Parse"/>.</summary>
    public static string Format(IEnumerable<string> versions) =>
        string.Join(",", Parse(string.Join(",", versions)));

    /// <summary>
    /// What the create form should offer: the operator's list when they have made one, and the
    /// shipped list otherwise. The order is theirs — the first entry is what a new database gets by
    /// default, so it is a decision and not something to re-sort afterwards.
    /// </summary>
    public static IReadOnlyList<string> Resolve(string? stored, IReadOnlyList<string> shipped)
    {
        var chosen = Parse(stored);
        return chosen.Count > 0 ? chosen : shipped;
    }

    /// <summary>
    /// The entries of a submitted list that are not usable tags, so the operator is told which ones
    /// rather than watching part of what they typed disappear.
    /// </summary>
    public static IReadOnlyList<string> Rejected(string? submitted)
    {
        if (string.IsNullOrWhiteSpace(submitted)) return [];

        return submitted
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(piece => !ImageReference.IsUsableTag(piece))
            .ToList();
    }
}
