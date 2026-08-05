namespace Harbora.Infrastructure.Storage;

/// <summary>One thing inside a volume.</summary>
/// <param name="Name">The entry's own name, never a path.</param>
/// <param name="IsDirectory">Whether opening it lists more things.</param>
/// <param name="SizeBytes">Size in bytes; meaningless for a directory and reported as 0.</param>
/// <param name="ModifiedAt">Last modification, or null when the runtime gave something unreadable.</param>
public sealed record VolumeEntry(string Name, bool IsDirectory, long SizeBytes, DateTimeOffset? ModifiedAt);

/// <summary>
/// The commands that read and write inside a volume, and the parser for what they print.
///
/// Everything here runs in a throwaway container with the volume mounted — the same trick that
/// measures a volume's size, because the platform has no other way to touch a named volume's
/// contents without giving itself a path on the host.
///
/// The rule that makes this safe is that **every script is a constant**. Paths and file contents are
/// passed as positional arguments and referenced as <c>"$1"</c>, never concatenated into the script
/// text. A filename is attacker-controlled input in a multi-tenant platform, and a script built by
/// string interpolation is a shell waiting for one with a quote in it.
/// </summary>
public static class VolumeFileCommands
{
    /// <summary>The image these run in. Small, and already pulled for measuring volumes.</summary>
    public const string HelperImage = "alpine:3.20";

    /// <summary>Where the volume is mounted inside the helper.</summary>
    public const string MountRoot = "/data";

    /// <summary>
    /// Entries directly inside a directory, one per line as <c>type|size|mtime|name</c>.
    ///
    /// The name goes last and is not escaped, because a filename may legitimately contain the
    /// separator; the parser splits a fixed number of times so the remainder is the name whatever
    /// is in it.
    /// </summary>
    private const string ListScript =
        "cd -- \"$1\" 2>/dev/null || exit 3; " +
        "for f in * .*; do " +
        "[ \"$f\" = \".\" ] && continue; [ \"$f\" = \"..\" ] && continue; " +
        "[ -e \"$f\" ] || continue; " +
        "if [ -d \"$f\" ]; then printf 'd|0|%s|%s\\n' \"$(stat -c %Y -- \"$f\")\" \"$f\"; " +
        "else printf 'f|%s|%s|%s\\n' \"$(stat -c %s -- \"$f\")\" \"$(stat -c %Y -- \"$f\")\" \"$f\"; fi; " +
        "done";

    // base64 without wrapping, so the whole file is one line the caller can decode.
    private const string ReadScript = "[ -f \"$1\" ] || exit 4; base64 -w 0 -- \"$1\"";

    // The directory is created first, so writing into a folder that does not exist yet works the
    // way somebody uploading into a new path expects.
    private const string WriteScript =
        "mkdir -p -- \"$(dirname -- \"$1\")\" && printf %s \"$2\" | base64 -d > \"$1\"";

    private const string DeleteScript = "rm -rf -- \"$1\"";

    private const string MakeDirectoryScript = "mkdir -p -- \"$1\"";

    /// <summary>Lists one directory. The path is absolute, inside the helper's mount.</summary>
    public static IReadOnlyList<string> Listing(string absoluteDirectory) =>
        ["sh", "-c", ListScript, "sh", absoluteDirectory];

    /// <summary>Reads one file out as base64.</summary>
    public static IReadOnlyList<string> Read(string absoluteFile) =>
        ["sh", "-c", ReadScript, "sh", absoluteFile];

    /// <summary>Writes one file from base64, creating its directory.</summary>
    public static IReadOnlyList<string> Write(string absoluteFile, string base64Content) =>
        ["sh", "-c", WriteScript, "sh", absoluteFile, base64Content];

    /// <summary>Removes a file or a directory and everything under it.</summary>
    public static IReadOnlyList<string> Delete(string absolutePath) =>
        ["sh", "-c", DeleteScript, "sh", absolutePath];

    /// <summary>Creates a directory.</summary>
    public static IReadOnlyList<string> MakeDirectory(string absolutePath) =>
        ["sh", "-c", MakeDirectoryScript, "sh", absolutePath];

    /// <summary>
    /// Reads what <see cref="Listing"/> printed.
    ///
    /// A line it cannot make sense of is skipped rather than guessed at: the alternative is an
    /// entry with an invented name or size appearing in somebody's file list.
    /// </summary>
    public static IReadOnlyList<VolumeEntry> ParseListing(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];

        var entries = new List<VolumeEntry>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Exactly three splits, so everything after the third separator is the name — a
            // filename may legitimately contain one, and splitting on all of them would truncate it.
            var parts = line.TrimEnd('\r').Split('|', 4);
            if (parts.Length != 4) continue;

            var isDirectory = parts[0] == "d";
            if (!isDirectory && parts[0] != "f") continue;

            if (!long.TryParse(parts[1], out var size) || size < 0) continue;
            if (parts[3].Length == 0) continue;

            entries.Add(new VolumeEntry(
                parts[3],
                isDirectory,
                isDirectory ? 0 : size,
                // An unreadable timestamp is null rather than the epoch, which would show every
                // file as modified in 1970 and sort them all to one end.
                long.TryParse(parts[2], out var epoch) && epoch > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(epoch)
                    : null));
        }

        // Directories first, then by name — the order every file browser uses, decided here so the
        // page cannot sort it differently from the download links it draws.
        return entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
