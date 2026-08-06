namespace Harbora.Infrastructure.Maintenance;

/// <summary>
/// Comparing the running panel's version against a release tag from GitHub.
///
/// A pure rule because the interesting part is not the HTTP call — it is deciding what counts as
/// "newer", and that is exactly the thing that quietly gets wrong: "v0.10.0" is newer than
/// "v0.9.0", string comparison says otherwise; a "-rc.1" pre-release is not a stable upgrade; a tag
/// that means nothing must never be announced as an update. The check runs at most once a day and
/// only when an operator has switched it on, so the arithmetic is the whole risk surface.
/// </summary>
public static class PanelVersion
{
    /// <summary>
    /// Whether <paramref name="latestTag"/> is a released version strictly newer than
    /// <paramref name="current"/>. False for anything unparseable, equal, older, or a pre-release —
    /// a banner urging an upgrade to a broken tag is worse than no banner.
    /// </summary>
    public static bool IsNewer(string? current, string? latestTag)
    {
        if (!TryParse(current, out var mine)) return false;
        if (!TryParse(latestTag, out var theirs)) return false;

        // A pre-release is not an upgrade an operator should be nudged toward unprompted.
        if (theirs.PreRelease) return false;

        return theirs.CompareTo(mine) > 0;
    }

    /// <summary>The three numbers of a SemVer tag, with a leading "v" and a pre-release suffix tolerated.</summary>
    public static bool TryParse(string? tag, out Version version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(tag)) return false;

        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        // Build metadata (+…) is not part of precedence; a pre-release (-…) is noted but not ordered.
        var plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];

        var preRelease = false;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            preRelease = true;
            text = text[..dash];
        }

        var parts = text.Split('.');
        if (parts.Length is < 1 or > 3) return false;

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        }

        version = new Version(numbers[0], numbers[1], numbers[2], preRelease);
        return true;
    }

    /// <summary>A parsed version, ordered numerically field by field.</summary>
    public readonly record struct Version(int Major, int Minor, int Patch, bool PreRelease)
    {
        public int CompareTo(Version other)
        {
            if (Major != other.Major) return Major.CompareTo(other.Major);
            if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
            return Patch.CompareTo(other.Patch);
        }
    }
}
