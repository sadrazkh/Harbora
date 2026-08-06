namespace Harbora.Infrastructure.Storage;

/// <summary>
/// Whether an object key from a web form is one this browser may touch.
///
/// S3 keys are freer than filesystem paths — a key may legitimately contain almost anything — but
/// the key is pasted into an <c>mc</c> argument that names a path inside a bucket, and mc resolves
/// <c>..</c> the way a shell does. A key that climbs out reaches another prefix, and on a shared
/// storage server another prefix can be another tenant's.
///
/// Deliberately stricter than S3 itself: this is the panel's own browser, not an API, and refusing
/// a key somebody could have made through the SDK is a much smaller failure than following one out
/// of the bucket. The refusal names what was wrong.
/// </summary>
public static class ObjectKey
{
    /// <summary>Beyond this, it is not a key somebody typed — it is an attempt at something.</summary>
    public const int MaxLength = 1024;

    /// <summary>
    /// The key as it should be used, or null when it must not be used at all.
    /// Leading and trailing slashes are trimmed so "photos/" and "/photos" mean one prefix.
    /// </summary>
    public static string? Normalise(string? key)
    {
        if (key is null) return null;
        if (key.Length > MaxLength) return null;

        // A NUL survives C# happily and truncates everything downstream that speaks C.
        if (key.Contains('\0')) return null;

        // Backslashes are refused rather than translated: on a key, a backslash is a legitimate
        // character, so turning it into a separator would silently address a different object.
        if (key.Contains('\\')) return null;

        // Control characters have no business in a key that came from a form, and they are how a
        // listing gets a line that is not the line it appears to be. This is also what refuses a
        // NUL, which C# carries happily and which truncates everything downstream that speaks C —
        // there is no separate check for it because this one already is that check.
        if (key.Any(char.IsControl)) return null;

        var segments = new List<string>();

        foreach (var segment in key.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            // Refused, not resolved. Resolving "a/../b" quietly turns a suspicious key into a
            // working one, and the interesting question — why was it there — never gets asked.
            if (segment == "..") return null;
            if (segment == ".") continue;
            if (segment.Trim().Length == 0) return null;

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }

    /// <summary>Whether a key is usable and names something (not the bucket root).</summary>
    public static bool IsUsableObject(string? key) => Normalise(key) is { Length: > 0 };

    /// <summary>
    /// The prefix to list. The empty string is the bucket root and is legitimate here — unlike an
    /// object key, where empty means "no object was named".
    /// </summary>
    public static string? NormalisePrefix(string? prefix) => Normalise(prefix ?? string.Empty);

    /// <summary>The parent of a prefix, or null at the root — what the "up one level" link needs.</summary>
    public static string? Parent(string? prefix)
    {
        var normalised = NormalisePrefix(prefix);
        if (string.IsNullOrEmpty(normalised)) return null;

        var slash = normalised.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalised[..slash];
    }
}
