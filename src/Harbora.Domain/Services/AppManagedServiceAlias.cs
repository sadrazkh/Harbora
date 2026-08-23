namespace Harbora.Domain.Services;

/// <summary>
/// The name an <see cref="AppManagedService"/> attachment is reached under, made collision-proof by
/// construction rather than by "last write wins".
///
/// <para>
/// <see cref="Harbora.Domain.Storage.AppStorageBucket"/> accepts a second attach silently outranking
/// the first one's shared keys, because a bucket's env vars are a single fixed set and the doc
/// comment on that type says plainly this is the accepted trade-off. A database is different: two
/// databases attached to the same app is the ordinary case (an app with its own database plus a
/// shared one, or blue/green migration), and a customer who reads <c>DATABASE_URL</c> for the wrong
/// one because the second attach silently took the name is exactly the defect this plan exists to
/// remove. So every attachment gets a name that cannot collide with a sibling already on the same
/// app — checked here, at resolution time, rather than discovered by an app breaking later.
/// </para>
/// </summary>
public static class AppManagedServiceAlias
{
    /// <summary>
    /// Uppercase, ASCII letters/digits only, non-empty, never starting with a digit — the same shape
    /// <c>Harbora.Infrastructure.Services.AttachKeys.PrefixFor</c> already enforces for its prefix
    /// (that method keeps the trailing underscore because it returns a ready-to-concatenate prefix;
    /// this one does not, because <see cref="Resolve"/> needs the bare alias to compare and to
    /// possibly append "_2" to).
    /// </summary>
    public static string Sanitize(string? raw)
    {
        var cleaned = new string((raw ?? string.Empty).Trim().ToUpperInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')
            .ToArray())
            .Trim('_');

        while (cleaned.Contains("__")) cleaned = cleaned.Replace("__", "_");

        if (cleaned.Length == 0) cleaned = "SERVICE";
        return char.IsAsciiDigit(cleaned[0]) ? $"_{cleaned}" : cleaned;
    }

    /// <summary>
    /// The alias this attachment will actually carry: <paramref name="requested"/> if the customer
    /// typed one, otherwise the service's own name, sanitised — and then, if that already belongs to
    /// another attachment on the same app, suffixed with "_2", "_3", … until it does not. An app is
    /// never left unable to attach a second database of the same name; it is only ever given the next
    /// free alias instead of a silent collision.
    /// </summary>
    public static string Resolve(string? requested, string serviceName, IEnumerable<string?> existingAliases)
    {
        var basis = Sanitize(string.IsNullOrWhiteSpace(requested) ? serviceName : requested);
        var taken = existingAliases
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!.ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);

        if (!taken.Contains(basis)) return basis;

        var i = 2;
        while (taken.Contains($"{basis}_{i}")) i++;
        return $"{basis}_{i}";
    }
}
