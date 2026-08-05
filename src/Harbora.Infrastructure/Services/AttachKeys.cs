namespace Harbora.Infrastructure.Services;

/// <summary>
/// Which environment variables an attach should write.
///
/// A database hands an application a fixed set of names — <c>DATABASE_URL</c>, <c>PGHOST</c>,
/// <c>REDIS_URL</c>. That works exactly once. Attaching a second PostgreSQL overwrote the first
/// one's values under the same names, so the application silently lost a database it was still
/// using: nothing failed at attach time, nothing failed at deploy time, and the first query after
/// the next release went to the wrong server.
///
/// An environment that holds several databases and a broker is the ordinary case people asked for,
/// so the names have to be able to hold more than one of a kind. Each service also writes a set
/// prefixed with its own name, and the unprefixed set is kept for whichever service already holds
/// it — so every application that exists today keeps working, and the second attach is the one that
/// has to be read from a new name.
/// </summary>
public static class AttachKeys
{
    /// <summary>
    /// The prefix a service's own variables carry, including the trailing underscore.
    ///
    /// Environment variable names are conventionally uppercase and cannot start with a digit, so a
    /// service called "2nd-cache" becomes <c>_2ND_CACHE_</c> rather than something a shell refuses
    /// to export.
    /// </summary>
    public static string PrefixFor(string serviceName)
    {
        var cleaned = new string((serviceName ?? string.Empty).Trim().ToUpperInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')
            .ToArray())
            .Trim('_');

        while (cleaned.Contains("__")) cleaned = cleaned.Replace("__", "_");

        // A name with nothing usable in it still needs a prefix, or its variables collide with the
        // unprefixed set they were meant to be distinguishable from.
        if (cleaned.Length == 0) cleaned = "SERVICE";

        return char.IsAsciiDigit(cleaned[0]) ? $"_{cleaned}_" : $"{cleaned}_";
    }

    /// <summary>
    /// The variables to write, keyed by their final name.
    /// </summary>
    /// <param name="wanted">What this service wants to hand over, under its conventional names.</param>
    /// <param name="existing">
    /// What the application already has, decrypted. A key whose value cannot be read is passed as
    /// null and treated as belonging to somebody else — the safe direction, since the alternative is
    /// overwriting a database somebody is still using.
    /// </param>
    /// <param name="serviceName">Used for the prefix.</param>
    public static IReadOnlyDictionary<string, string> For(
        IReadOnlyDictionary<string, string> wanted,
        IReadOnlyDictionary<string, string?> existing,
        string serviceName)
    {
        var prefix = PrefixFor(serviceName);
        var final = new Dictionary<string, string>(StringComparer.Ordinal);

        // Always. These are the names that can hold more than one database, and they are what the
        // page tells somebody to read for the second one.
        foreach (var (key, value) in wanted) final[prefix + key] = value;

        // The unprefixed set goes to whoever already holds it. Absent means nobody does, which is
        // the first attach and by far the common case; equal means this service holds it already,
        // which is a re-attach after a password rotation.
        var claimedByAnother = wanted.Any(w =>
            existing.TryGetValue(w.Key, out var current) && current != w.Value);

        if (!claimedByAnother)
            foreach (var (key, value) in wanted) final[key] = value;

        return final;
    }

    /// <summary>
    /// Whether this attach had to fall back to prefixed names only, so the page can say so.
    ///
    /// Worth saying: somebody attaching a second database and then reading <c>DATABASE_URL</c> in
    /// their code would get the first one, and nothing about the screen would have suggested it.
    /// </summary>
    public static bool IsPrefixedOnly(
        IReadOnlyDictionary<string, string> wanted,
        IReadOnlyDictionary<string, string?> existing) =>
        wanted.Any(w => existing.TryGetValue(w.Key, out var current) && current != w.Value);
}
