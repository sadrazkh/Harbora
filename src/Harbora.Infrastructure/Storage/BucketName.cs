namespace Harbora.Infrastructure.Storage;

/// <summary>Why a bucket name cannot be used.</summary>
public enum BucketNameRefusal
{
    None = 0,
    Missing = 1,
    TooShort = 2,
    TooLong = 3,
    BadCharacters = 4,
    BadEnds = 5,
    LooksLikeAnAddress = 6,
    ReservedSuffix = 7
}

/// <summary>
/// Whether a name is one an S3 bucket can have.
///
/// The rules are not ours and cannot be relaxed: a name the storage server rejects fails at
/// provisioning time, after the row has been written and the person has been told they have a
/// bucket. Several of them look arbitrary until the reason is known, which is why they are written
/// down here rather than left to a regex somebody will "simplify" later:
///
/// <list type="bullet">
/// <item>Uppercase is refused rather than lowercased. A name silently changed is a name that does
/// not match what somebody put in their configuration file.</item>
/// <item>Something shaped like <c>192.168.1.1</c> is refused because path-style and virtual-host
/// addressing cannot both resolve it.</item>
/// <item><c>-s3alias</c> and <c>--ol-s3</c> are reserved suffixes; a bucket ending in one is
/// accepted by some servers and rejected by others, which is worse than a refusal.</item>
/// </list>
/// </summary>
public static class BucketName
{
    public const int MinLength = 3;
    public const int MaxLength = 63;

    private static readonly string[] ReservedSuffixes = ["-s3alias", "--ol-s3"];

    /// <summary>Why this name cannot be used, or <see cref="BucketNameRefusal.None"/>.</summary>
    public static BucketNameRefusal Check(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return BucketNameRefusal.Missing;

        // Deliberately not trimmed. A name with a space around it is a name somebody will type
        // differently the second time, and the difference is invisible.
        if (name.Length < MinLength) return BucketNameRefusal.TooShort;
        if (name.Length > MaxLength) return BucketNameRefusal.TooLong;

        foreach (var suffix in ReservedSuffixes)
            if (name.EndsWith(suffix, StringComparison.Ordinal)) return BucketNameRefusal.ReservedSuffix;

        // Lowercase letters, digits, hyphens. Periods are legal in S3 and refused here on purpose:
        // a bucket with a period cannot be reached over TLS by virtual-host addressing, because the
        // wildcard certificate does not cover the extra label.
        foreach (var c in name)
            if (!(char.IsAsciiDigit(c) || (c >= 'a' && c <= 'z') || c == '-'))
                return BucketNameRefusal.BadCharacters;

        if (!char.IsAsciiLetterOrDigit(name[0]) || !char.IsAsciiLetterOrDigit(name[^1]))
            return BucketNameRefusal.BadEnds;

        if (LooksLikeAnAddress(name)) return BucketNameRefusal.LooksLikeAnAddress;

        return BucketNameRefusal.None;
    }

    public static bool IsValid(string? name) => Check(name) == BucketNameRefusal.None;

    /// <summary>
    /// Four dot-separated numbers. Refused because path-style and virtual-host addressing cannot
    /// both resolve such a name — but periods are already refused above, so this only fires on the
    /// dashed shapes some clients normalise into addresses.
    /// </summary>
    private static bool LooksLikeAnAddress(string name)
    {
        var parts = name.Split('-');
        if (parts.Length != 4) return false;

        return parts.All(p => p.Length is > 0 and <= 3 && p.All(char.IsAsciiDigit));
    }

    /// <summary>
    /// A name derived from something a person already typed, for the create form to offer.
    ///
    /// A suggestion, never a silent correction: it fills the box and the person can see it before
    /// they submit. Correcting on submit is how somebody ends up with a bucket whose name is not
    /// the one in their configuration.
    /// </summary>
    public static string? Suggest(string? from)
    {
        if (string.IsNullOrWhiteSpace(from)) return null;

        var cleaned = new string(from.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'z') ? c : '-')
            .ToArray());

        while (cleaned.Contains("--")) cleaned = cleaned.Replace("--", "-");
        cleaned = cleaned.Trim('-');

        if (cleaned.Length > MaxLength) cleaned = cleaned[..MaxLength].Trim('-');

        return IsValid(cleaned) ? cleaned : null;
    }
}
