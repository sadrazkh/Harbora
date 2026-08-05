namespace Harbora.Infrastructure.Storage;

/// <summary>Why a mount path cannot be used.</summary>
public enum MountPathRefusal
{
    None = 0,
    Missing = 1,

    /// <summary>Not absolute. A relative mount is resolved against whatever the image's workdir is.</summary>
    NotAbsolute = 2,

    /// <summary>Contains <c>..</c>, a NUL, or a backslash.</summary>
    Unsafe = 3,

    /// <summary>The container's root, or a directory the operating system needs.</summary>
    Reserved = 4,

    TooLong = 5
}

/// <summary>
/// Where inside a container a volume may be mounted.
///
/// This decides what a person can overwrite in their own container, and the dangerous answers look
/// ordinary. Mounting an empty volume over <c>/etc</c> replaces the image's configuration with
/// nothing and the container stops resolving DNS; over <c>/</c> it does not start at all; over
/// <c>/proc</c> the runtime refuses in a way that reads as a platform fault rather than as a
/// choice somebody made.
///
/// Refused rather than corrected. A path quietly changed is a path that does not match what the
/// application was told to write to, and the mismatch shows up as missing data rather than as an
/// error.
/// </summary>
public static class MountPath
{
    public const int MaxLength = 255;

    /// <summary>
    /// Directories the container needs to be the image's own.
    ///
    /// Deliberately a short list of the ones that break a container outright. It is not a security
    /// boundary — the volume is the tenant's own container — it is a guard against the mounts whose
    /// failure is impossible to read from the outside.
    /// </summary>
    private static readonly string[] Reserved =
        ["/", "/bin", "/boot", "/dev", "/etc", "/lib", "/lib64", "/proc", "/sbin", "/sys", "/usr"];

    /// <summary>Why this path cannot be mounted, or <see cref="MountPathRefusal.None"/>.</summary>
    public static MountPathRefusal Check(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return MountPathRefusal.Missing;

        var value = path.Trim();

        if (value.Length > MaxLength) return MountPathRefusal.TooLong;
        if (!value.StartsWith('/')) return MountPathRefusal.NotAbsolute;

        // A backslash is not a separator here and a NUL ends the string at the kernel. Both are
        // refused rather than stripped: neither belongs in a path somebody typed on purpose.
        if (value.Contains('\0') || value.Contains('\\')) return MountPathRefusal.Unsafe;

        foreach (var segment in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (segment == ".." || segment == ".") return MountPathRefusal.Unsafe;

        // The subdirectory check matters as much as the exact one: mounting over /usr/bin breaks
        // the image just as thoroughly as mounting over /usr. A trailing separator needs no
        // normalising first — "/etc/" starts with "/etc/" — and a normalise here changed no
        // outcome, so it went out again rather than sitting in front of an authorisation decision
        // looking like it does something.
        foreach (var reserved in Reserved)
            if (value == reserved ||
                (reserved != "/" && value.StartsWith(reserved + "/", StringComparison.Ordinal)))
                return MountPathRefusal.Reserved;

        return MountPathRefusal.None;
    }

    public static bool IsValid(string? path) => Check(path) == MountPathRefusal.None;

    /// <summary>
    /// The path as it will be stored: trimmed, with any trailing separator removed so "/data" and
    /// "/data/" are one mount rather than two that collide at deploy time.
    /// </summary>
    public static string? Normalise(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        // The root trims to nothing and comes back as "/" below, so it needs no case of its own.
        var value = path.Trim().TrimEnd('/');

        return value.Length == 0 ? "/" : value;
    }

    /// <summary>
    /// The Docker volume name for a mount on an application.
    ///
    /// Named after the application rather than the path alone: two applications mounting "/data"
    /// must not share a volume, which would hand one of them the other's files.
    /// </summary>
    public static string VolumeNameFor(string appSlug, string mountPath)
    {
        // Trimmed at both ends, which makes "/data" and "/data/" the same tail without normalising
        // first.
        var tail = new string((mountPath ?? string.Empty).Trim('/').ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray())
            .Trim('-');

        while (tail.Contains("--")) tail = tail.Replace("--", "-");
        if (tail.Length == 0) tail = "root";

        return $"harbora-vol-{appSlug}-{tail}";
    }
}
