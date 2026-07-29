using System.Formats.Tar;
using System.IO.Compression;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Unpacks a source archive uploaded by <c>harbora deploy</c>.
///
/// This is the one place where a user's bytes are written to the panel's filesystem, so it is
/// deliberately paranoid: entry paths are resolved and checked to stay inside the destination
/// (a <c>../../</c> entry would otherwise let an authenticated tenant write anywhere the panel can),
/// links are refused outright, and both the entry count and the uncompressed size are capped so a
/// small upload cannot fill the disk.
/// </summary>
public static class SourceArchive
{
    /// <summary>Uncompressed ceiling. A source tree far above this is a mistake or an attack.</summary>
    public const long MaxUncompressedBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Entry ceiling — guards against an archive of millions of empty files.</summary>
    public const int MaxEntries = 200_000;

    public sealed record Result(int Files, long Bytes);

    public static async Task<Result> ExtractAsync(Stream gzippedTar, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination);

        await using var gz = new GZipStream(gzippedTar, CompressionMode.Decompress);
        await using var reader = new TarReader(gz, leaveOpen: true);

        var files = 0;
        long bytes = 0;

        while (true)
        {
            TarEntry? entry;
            try
            {
                entry = await reader.GetNextEntryAsync(copyData: false, ct);
            }
            catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException)
            {
                // An empty folder, a truncated upload or a body that isn't a gzipped tar all land
                // here. Say so plainly instead of surfacing "unable to read beyond the end of the
                // stream" as a deployment error.
                throw new InvalidOperationException(
                    "The uploaded source archive is empty or not a valid .tar.gz. Check that the " +
                    "folder you pushed actually contains files.", ex);
            }
            if (entry is null) break;

            if (++files > MaxEntries)
                throw new InvalidOperationException($"Archive has more than {MaxEntries:N0} entries.");

            // Links can point anywhere, including outside the build context. Source trees don't need
            // them, so refusing is cheaper than trying to validate targets.
            if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                continue;

            var target = ResolveSafePath(root, entry.Name);
            if (target is null) continue;   // "./" and similar no-ops

            if (entry.EntryType is TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                continue;   // devices, fifos — never part of a source tree

            bytes += entry.Length;
            if (bytes > MaxUncompressedBytes)
                throw new InvalidOperationException(
                    $"Archive expands to more than {MaxUncompressedBytes / (1024 * 1024)} MB.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var file = File.Create(target);
            if (entry.DataStream is not null)
                await entry.DataStream.CopyToAsync(file, ct);
        }

        return new Result(files, bytes);
    }

    /// <summary>
    /// Maps an archive entry name to an absolute path inside <paramref name="root"/>, or null when
    /// the entry is a no-op. Throws when the entry tries to escape.
    /// </summary>
    public static string? ResolveSafePath(string root, string entryName)
    {
        var relative = entryName.Replace('\\', '/').TrimStart('/');

        // "./" prefixes are how tar normally records a directory-relative archive.
        while (relative.StartsWith("./", StringComparison.Ordinal)) relative = relative[2..];
        if (relative.Length == 0 || relative is "." or "./") return null;

        if (Path.IsPathRooted(entryName) || entryName.StartsWith('/'))
            throw new InvalidOperationException($"Archive entry '{entryName}' has an absolute path.");

        var full = Path.GetFullPath(Path.Combine(root, relative));

        // The decisive check: after resolving "..", the entry must still be under the destination.
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            throw new InvalidOperationException($"Archive entry '{entryName}' escapes the build directory.");

        return full;
    }
}
