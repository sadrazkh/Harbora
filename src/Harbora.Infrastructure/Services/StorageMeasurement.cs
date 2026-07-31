namespace Harbora.Infrastructure.Services;

/// <summary>
/// How big a database's data actually is.
///
/// Docker reports nothing useful about a volume's size, so it is measured the only way available:
/// walking the directory from inside a container. That costs real time on a large volume, which is
/// why the answer is stored with the moment it was taken rather than recomputed for every page.
/// </summary>
public static class StorageMeasurement
{
    /// <summary>The command, run with the volume mounted read-only at /data.</summary>
    public static IReadOnlyList<string> Command =>
        // -s for the total only, -b for bytes rather than a rounded human figure. cut, because du
        // prints the path alongside the number.
        ["sh", "-c", "du -sb /data | cut -f1"];

    /// <summary>
    /// Reads the byte count out of the command's output, or null when it cannot be trusted.
    ///
    /// Null rather than zero on anything unexpected: "0 bytes" is a plausible-looking figure that
    /// would be shown as fact, while nothing at all is honest about not knowing.
    /// </summary>
    public static long? Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Docker frames the output of a container with no TTY, so the digits arrive with control
            // bytes stuck to them — stripped here rather than assumed absent. Observed on a real
            // server: the number came back unparseable and the size was reported as unknown.
            var line = new string(raw.Where(c => !char.IsControl(c)).ToArray()).Trim();

            // The image pull writes to the same stream, so the size is found rather than assumed to
            // be the first thing said. Only an all-digit line counts — "sha256:16…" has digits too.
            if (line.Length > 0 && line.All(char.IsAsciiDigit)
                && long.TryParse(line, System.Globalization.NumberStyles.None,
                                 System.Globalization.CultureInfo.InvariantCulture, out var bytes))
                return bytes;
        }

        return null;
    }

    /// <summary>A size for a screen. Deliberately not "0 B" for unknown — see <see cref="Parse"/>.</summary>
    public static string Describe(long? bytes) => bytes switch
    {
        null => "—",
        < 1024 => $"{bytes} B",
        < 1024L * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
    };
}
