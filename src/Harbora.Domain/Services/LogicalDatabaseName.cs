namespace Harbora.Domain.Services;

/// <summary>
/// The name a <see cref="ManagedServiceDatabase"/> carries inside its engine, made collision-proof by
/// construction rather than by "last request wins" — the same idiom
/// <see cref="AppManagedServiceAlias"/> already uses for an attachment's alias, one level down: two
/// apps asking for a database called "app" on the same instance must not silently share one, or one
/// customer's rows would start appearing in the other's tables.
/// </summary>
public static class LogicalDatabaseName
{
    /// <summary>
    /// Postgres truncates an identifier past 63 bytes silently; MySQL/MariaDB refuse past 64. 63 is
    /// the shared bound every engine this feature supports can hold without truncation or refusal.
    /// </summary>
    public const int MaxLength = 63;

    /// <summary>
    /// Lowercase, ASCII letters/digits/underscore only, non-empty, never starting with a digit — a
    /// database name is a SQL identifier and this is the alphabet <c>DatabaseGrantSql.IsSafe</c>
    /// insists everything reaching a statement is drawn from. Lowercased (unlike
    /// <see cref="AppManagedServiceAlias.Sanitize"/>, which uppercases) to match the existing
    /// convention on this platform's own instances — <c>driveunion_db</c>, <c>kousar_kolie_db</c> —
    /// rather than introduce a second casing style database names have never used.
    /// </summary>
    public static string Sanitize(string? raw)
    {
        var cleaned = new string((raw ?? string.Empty).Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')
            .ToArray())
            .Trim('_');

        while (cleaned.Contains("__")) cleaned = cleaned.Replace("__", "_");

        if (cleaned.Length == 0) cleaned = "db";
        if (char.IsAsciiDigit(cleaned[0])) cleaned = $"db_{cleaned}";
        if (cleaned.Length > MaxLength) cleaned = cleaned[..MaxLength].TrimEnd('_');

        return cleaned;
    }

    /// <summary>
    /// The name this database will actually carry: <paramref name="requested"/> sanitised, or "db" if
    /// nothing usable was typed — and then, if that name already belongs to another database on the
    /// same instance, suffixed with "_2", "_3", … until it does not. An instance is never left unable
    /// to hold a second database of a wanted name; it is only ever given the next free one instead of
    /// a silent collision with a neighbour's rows.
    /// </summary>
    public static string Resolve(string? requested, IEnumerable<string?> existingNames)
    {
        var basis = Sanitize(requested);
        var taken = existingNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        if (!taken.Contains(basis)) return basis;

        // Room is reserved for the suffix rather than appended on top of an already-maximal name: a
        // 63-character basis plus "_2" would be 65 characters, which Postgres truncates back down to
        // a name indistinguishable from the very collision this method exists to avoid.
        var i = 2;
        string candidate;
        do
        {
            var suffix = $"_{i}";
            var trimmedBasis = basis.Length + suffix.Length > MaxLength
                ? basis[..(MaxLength - suffix.Length)].TrimEnd('_')
                : basis;
            candidate = $"{trimmedBasis}{suffix}";
            i++;
        } while (taken.Contains(candidate));

        return candidate;
    }
}
