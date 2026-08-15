namespace Harbora.Infrastructure.Storage;

/// <summary>
/// A path inside one volume, and nothing outside it.
///
/// This is the only thing standing between a text box on a web page and the filesystem of the
/// machine the platform runs on. Every read, write and delete goes through it, so it is written to
/// refuse rather than to repair: a path it cannot make sense of is rejected, not guessed at.
///
/// The traps, in the order they bite:
///
/// <list type="bullet">
/// <item>An absolute path — a leading slash of any kind, including a bare <c>"/"</c>. Refused
/// outright rather than resolved into the volume root: silently stripping it would answer
/// <c>"/etc/passwd"</c> by quietly listing the volume's own <c>etc/passwd</c> instead, a different
/// question than the one that was typed. There is no path above the mount for a leading slash to
/// honestly mean "start from", and the root has its own spelling — leaving the path out
/// entirely — so refusing this costs no caller anything.</item>
/// <item><c>..</c> anywhere in the path, including spelled as a segment after a redundant slash.
/// Normalising first and checking afterwards is what makes this reliable — checking the raw string
/// for "<c>..</c>" also rejects a legitimate file called <c>..config</c>.</item>
/// <item>A backslash, which is a separator on the machine an operator is typing from and an
/// ordinary filename character on the Linux host this ends up running against.</item>
/// <item>A NUL byte, which ends the string for the C library underneath long after every check
/// written in C# has passed it.</item>
/// </list>
/// </summary>
public static class VolumePath
{
    /// <summary>Longest path this will accept, well inside the kernel's own limit.</summary>
    public const int MaxLength = 1024;

    /// <summary>
    /// The path relative to the volume root, using forward slashes and no leading one, or null when
    /// it is not a path inside the volume.
    ///
    /// An empty input is the root itself and is returned as an empty string — listing the root is
    /// an ordinary thing to do, and refusing it would mean the browser could never open. A leading
    /// slash is refused rather than treated the same way: every caller in this codebase builds
    /// "path" from a previously-normalised value or leaves it out for the root, so nothing here ever
    /// legitimately sends one, and a caller that types "/etc/passwd" meant something more specific
    /// than "the root".
    /// </summary>
    public static string? Normalise(string? path)
    {
        if (path is null) return null;
        if (path.Length > MaxLength) return null;

        // Before anything else: a NUL ends the string for the C library underneath, so everything
        // after it is invisible to every check below and visible to the kernel.
        if (path.Contains('\0')) return null;

        // A backslash is a separator where somebody is typing and a filename character where this
        // runs. Refused rather than translated: translating would silently turn one intended
        // filename into a different path.
        if (path.Contains('\\')) return null;

        // An absolute path. Splitting with RemoveEmptyEntries below would otherwise just drop the
        // leading empty segment and answer as if the path had been relative all along — see the
        // class doc for why that is the wrong kind of forgiving.
        if (path.StartsWith('/')) return null;

        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            // "." is redundant rather than dangerous, so it is dropped.
            if (segment == ".") continue;

            // ".." is refused outright rather than resolved. Resolving is how "a/../../b" escapes:
            // it cancels a segment that was never really there, and the arithmetic is easy to get
            // wrong. There is no legitimate use for it in a path somebody typed into a file
            // browser that already shows them where they are.
            if (segment == "..") return null;

            if (segment.Trim().Length == 0) return null;

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }

    /// <summary>
    /// The absolute path inside the container's mount, for a path already normalised.
    ///
    /// Takes the normalised form rather than the raw one on purpose: passing a raw path here is the
    /// mistake this signature is shaped to prevent.
    /// </summary>
    public static string Under(string mountRoot, string normalised)
    {
        var root = mountRoot.TrimEnd('/');
        return normalised.Length == 0 ? root : $"{root}/{normalised}";
    }

    /// <summary>The last segment, for showing a name in a list. Empty for the root.</summary>
    public static string NameOf(string normalised)
    {
        var slash = normalised.LastIndexOf('/');
        return slash < 0 ? normalised : normalised[(slash + 1)..];
    }

    /// <summary>
    /// The containing directory of a normalised path, or null at the root. Used to draw the "up"
    /// link, which is why it must never produce something above the root.
    /// </summary>
    public static string? ParentOf(string normalised)
    {
        if (normalised.Length == 0) return null;

        var slash = normalised.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalised[..slash];
    }
}
