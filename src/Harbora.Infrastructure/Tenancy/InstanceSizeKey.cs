namespace Harbora.Infrastructure.Tenancy;

/// <summary>
/// The key a resource tier is known by.
///
/// It is not a display name. Plans store it in a comma-separated allow list, apps and databases
/// store it on themselves, and it is compared case-insensitively in half a dozen places — so a key
/// with a space, a comma or a capital in it is a key that works everywhere except the one place it
/// is split on, or matched in, and the failure there is a tier silently reading as "no limit".
/// </summary>
public static class InstanceSizeKey
{
    /// <summary>
    /// Docker-ish and URL-safe, and short enough to read in a dropdown.
    ///
    /// <para>
    /// The figure lives on the entity, because the schema bounds two columns to it and the data layer
    /// cannot reach in here to ask for it. This is the name the normaliser and its tests use.
    /// </para>
    /// </summary>
    public const int MaxLength = Harbora.Domain.Tenancy.InstanceSize.KeyMaxLength;

    /// <summary>
    /// The key to store, or null when what was typed cannot become one.
    ///
    /// Normalises rather than refuses where the intent is unambiguous — "Extra Large" is plainly
    /// meant to be `extra-large` — and refuses where it is not, rather than storing an empty key
    /// that would match every resource with no size set.
    /// </summary>
    public static string? Normalise(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        var cleaned = new string(key.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray())
            .Trim('-');

        // Runs of separators collapse: "extra   large" and "extra---large" are one name typed two
        // ways, and storing both would put two tiers in the list that read identically.
        while (cleaned.Contains("--")) cleaned = cleaned.Replace("--", "-");

        if (cleaned.Length == 0) return null;

        // A comma can never appear — Plan.AllowedSizeKeys is a comma-separated list, and a key
        // containing one would silently become two entries, neither of which matches anything.
        return cleaned.Length > MaxLength ? cleaned[..MaxLength].Trim('-') : cleaned;
    }
}
