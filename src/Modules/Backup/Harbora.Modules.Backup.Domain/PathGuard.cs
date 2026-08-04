namespace Harbora.Modules.Backup.Domain;

/// <summary>Why a path was refused. Distinct cases so the message shown can be specific.</summary>
public enum PathRejection
{
    None = 0,
    Empty = 1,

    /// <summary>An entry inside an archive that is rooted ("/etc/passwd", "C:\Windows").</summary>
    Rooted = 2,

    /// <summary>Contains a ".." segment.</summary>
    ParentTraversal = 3,

    /// <summary>Resolves outside the permitted root.</summary>
    EscapesRoot = 4,

    /// <summary>Contains a character that cannot appear in a path Harbora will write.</summary>
    InvalidCharacter = 5
}

public readonly record struct PathCheck(bool Allowed, PathRejection Rejection, string? ResolvedPath)
{
    public static PathCheck Ok(string resolved) => new(true, PathRejection.None, resolved);
    public static PathCheck Fail(PathRejection why) => new(false, why, null);
}

/// <summary>
/// Keeps restore output inside the directory it was promised to.
///
/// <para>
/// A snapshot is attacker-influenced data whenever an attacker could write a file into a backed-up
/// volume. Restoring it is therefore an extraction of untrusted names onto a trusted filesystem —
/// the classic Zip-Slip shape — and the only reliable defence is to resolve first and compare after,
/// never to inspect the string as supplied. See <c>docs/backup-sync/THREAT_MODEL.md</c> T2.
/// </para>
/// </summary>
public static class PathGuard
{
    /// <summary>
    /// Resolve <paramref name="candidate"/> and confirm it lands inside <paramref name="root"/>.
    ///
    /// <para>
    /// The root is normalised to end in a separator before comparing. Without that,
    /// <c>/var/restore-evil</c> passes a naive <c>StartsWith("/var/restore")</c> — a prefix match on
    /// path strings is not a containment check.
    /// </para>
    /// <para>
    /// Comparison is <see cref="StringComparison.Ordinal"/>. A culture-sensitive comparison can treat
    /// distinct byte sequences as equal, which is not a property to rely on when the answer decides
    /// whether something may be overwritten.
    /// </para>
    /// </summary>
    public static PathCheck ResolveWithin(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate))
            return PathCheck.Fail(PathRejection.Empty);

        string fullRoot, fullCandidate;
        try
        {
            fullRoot = Path.GetFullPath(root);
            fullCandidate = Path.GetFullPath(Path.IsPathRooted(candidate)
                ? candidate
                : Path.Combine(fullRoot, candidate));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return PathCheck.Fail(PathRejection.InvalidCharacter);
        }

        var normalisedRoot = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        // The root itself is a legitimate destination; every other path must sit strictly under it.
        if (string.Equals(fullCandidate + Path.DirectorySeparatorChar, normalisedRoot, StringComparison.Ordinal))
            return PathCheck.Ok(fullCandidate);

        return fullCandidate.StartsWith(normalisedRoot, StringComparison.Ordinal)
            ? PathCheck.Ok(fullCandidate)
            : PathCheck.Fail(PathRejection.EscapesRoot);
    }

    /// <summary>
    /// Validate a path taken from inside an archive or snapshot, before it is joined to anything.
    ///
    /// <para>
    /// Both <c>/</c> and <c>\</c> count as separators regardless of host OS. A tar entry named
    /// <c>..\..\windows\system32</c> is inert on Linux and a traversal on Windows, and a restore
    /// artifact can be moved between the two — so the stricter reading is the correct one, at the
    /// cost of refusing the rare Linux filename that genuinely contains a backslash.
    /// </para>
    /// </summary>
    public static PathCheck ValidateArchiveEntry(string root, string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath)) return PathCheck.Fail(PathRejection.Empty);

        if (entryPath.Contains('\0')) return PathCheck.Fail(PathRejection.InvalidCharacter);

        if (Path.IsPathRooted(entryPath) || entryPath.StartsWith('/') || entryPath.StartsWith('\\'))
            return PathCheck.Fail(PathRejection.Rooted);

        // A drive-qualified entry ("C:data") is rooted in intent even where IsPathRooted disagrees.
        if (entryPath.Length >= 2 && entryPath[1] == ':') return PathCheck.Fail(PathRejection.Rooted);

        var segments = entryPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return PathCheck.Fail(PathRejection.Empty);
        if (segments.Any(s => s == "..")) return PathCheck.Fail(PathRejection.ParentTraversal);

        // Checked anyway, rather than trusting the segment scan: the resolution is what the
        // filesystem will actually do, and it is the only thing that accounts for the host's own
        // normalisation rules.
        return ResolveWithin(root, Path.Combine(segments));
    }
}
