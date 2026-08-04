namespace Harbora.Infrastructure.Templates;

/// <summary>
/// A tag read as a version: its numeric parts and whatever variant name follows them.
/// </summary>
/// <param name="Parts">
/// <c>16.4.1</c> → 16, 4, 1. The count matters as much as the values: a repository publishes
/// <c>16</c>, <c>16.4</c> and <c>16.4-alpine</c> for the same release, and treating them as three
/// versions offers a customer the same software three times under different names.
/// </param>
/// <param name="Variant">
/// <c>alpine</c> in <c>16-alpine</c>, null in <c>16</c>. A different base image, not a different
/// release.
/// </param>
public sealed record TagVersion(IReadOnlyList<int> Parts, string? Variant) : IComparable<TagVersion>
{
    public int CompareTo(TagVersion? other)
    {
        if (other is null) return 1;

        for (var i = 0; i < Math.Max(Parts.Count, other.Parts.Count); i++)
        {
            var mine = i < Parts.Count ? Parts[i] : 0;
            var theirs = i < other.Parts.Count ? other.Parts[i] : 0;
            if (mine != theirs) return mine.CompareTo(theirs);
        }

        return 0;
    }
}

/// <summary>
/// Reading a registry tag as something that can be compared.
///
/// Deliberately strict. A registry is full of tags that are not releases — <c>latest</c>,
/// <c>edge</c>, <c>main</c>, a commit hash, a release candidate — and every one of them, offered to
/// a customer as a version, is a moving or unfinished image presented as a considered choice.
/// Anything this cannot read confidently is not a version.
/// </summary>
public static class RegistryTag
{
    /// <summary>
    /// Suffixes that mark something not finished. A customer offered a release candidate as a
    /// version has no way to know it is one.
    /// </summary>
    private static readonly string[] PreReleaseMarkers =
        ["rc", "alpha", "beta", "pre", "preview", "snapshot", "canary", "test"];

    /// <summary>The tag as a version, or null when it is not one.</summary>
    public static TagVersion? Parse(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var value = tag.Trim();

        // A leading v is decoration on the same number.
        if (value.Length > 1 && (value[0] == 'v' || value[0] == 'V') && char.IsDigit(value[1]))
            value = value[1..];

        var dash = value.IndexOf('-');
        var variant = dash < 0 ? null : value[(dash + 1)..];
        var numbers = dash < 0 ? value : value[..dash];

        if (variant is not null)
        {
            if (variant.Length == 0) return null;

            // "1.2.3-rc1" and "1.2.3-beta.2" are both unfinished. Checked by prefix because the
            // number that follows varies and is not what makes it a pre-release.
            if (PreReleaseMarkers.Any(m => variant.StartsWith(m, StringComparison.OrdinalIgnoreCase)))
                return null;
        }

        var pieces = numbers.Split('.');
        if (pieces.Length == 0 || pieces.Length > 4) return null;

        var parts = new List<int>(pieces.Length);
        foreach (var piece in pieces)
        {
            // This one line is what rejects `latest`, `main`, `edge`, `nightly` and a commit hash:
            // none of them is a run of digits. An explicit list of those names was written first and
            // then deleted, because mutation testing showed removing it changed nothing — every
            // entry was already refused here, and a guard no test can reach is a guard nobody knows
            // is broken.
            //
            // int.TryParse alone would accept "+5" and " 5", and guessing at those is how a commit
            // hash becomes release 5.
            if (piece.Length == 0 || !piece.All(char.IsAsciiDigit)) return null;
            if (!int.TryParse(piece, out var number)) return null;
            parts.Add(number);
        }

        return new TagVersion(parts, variant);
    }

    /// <summary>Whether two tags describe the same kind of thing: same depth, same variant.</summary>
    public static bool SameShape(TagVersion a, TagVersion b) =>
        a.Parts.Count == b.Parts.Count
        && string.Equals(a.Variant, b.Variant, StringComparison.OrdinalIgnoreCase);
}
