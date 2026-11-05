using System.Formats.Tar;
using System.IO.Compression;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Unpacks a source archive: a gzipped tar pushed by <c>harbora deploy</c>, or a zip somebody dropped
/// on <c>/launch</c>. Which one is decided by its first bytes rather than its name, so everything that
/// already hands a <c>SourceArchivePath</c> to the pipeline works for both with no other change.
///
/// This is the one place where a user's bytes are written to the panel's filesystem, so it is
/// deliberately paranoid: entry paths are resolved and checked to stay inside the destination
/// (a <c>../../</c> entry would otherwise let an authenticated tenant write anywhere the panel can),
/// links are refused outright, and both the entry count and the uncompressed size are capped so a
/// small upload cannot fill the disk.
/// </summary>
public static class SourceArchive
{
    /// <summary>What git gives a file with its executable bit set, which is what a clone of the same repository has.</summary>
    private const UnixFileMode Executable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private static readonly string[] SystemLitter = [".DS_Store", "Thumbs.db", "desktop.ini"];

    /// <summary>Uncompressed ceiling. A source tree far above this is a mistake or an attack.</summary>
    public const long MaxUncompressedBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Entry ceiling — guards against an archive of millions of empty files.</summary>
    public const int MaxEntries = 200_000;

    public sealed record Result(int Files, long Bytes);

    public static async Task<Result> ExtractAsync(Stream archive, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination);

        // Both formats have to be looked at first, and a zip's directory is at its end: a stream that
        // cannot seek is put somewhere that can. Every caller today hands over a file, so this costs
        // nothing unless somebody later passes a request body straight through.
        await using var spool = archive.CanSeek ? null : await SpoolAsync(archive, ct);
        var source = spool ?? archive;

        return await LooksLikeZipAsync(source, ct)
            ? await ExtractZipAsync(source, root, ct)
            : await ExtractTarGzAsync(source, root, ct);
    }

    private static async Task<FileStream> SpoolAsync(Stream source, CancellationToken ct)
    {
        var spool = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await source.CopyToAsync(spool, ct);
        spool.Position = 0;
        return spool;
    }

    /// <summary>
    /// <c>50 4B</c> is a zip; everything else goes down the tar path, where gzip junk and an empty body
    /// already end in the sentence a person can act on. The stream is put back where it was found.
    /// </summary>
    private static async Task<bool> LooksLikeZipAsync(Stream source, CancellationToken ct)
    {
        var start = source.Position;
        var magic = new byte[2];
        var read = await source.ReadAtLeastAsync(magic, magic.Length, throwOnEndOfStream: false, ct);
        source.Position = start;
        return read == magic.Length && magic[0] == 0x50 && magic[1] == 0x4B;
    }

    /// <summary>
    /// What a person can do about it, instead of "unable to read beyond the end of the stream" as a
    /// deployment error. An empty folder, a truncated upload and a body in neither format all land here.
    /// </summary>
    private static SourceArchiveRefusedException NotAnArchive(Exception? inner = null) => new(
        "The uploaded source archive is empty or not a valid .tar.gz or .zip. Check that the " +
        "folder you pushed actually contains files.",
        "فایل آپلودشده خالی است یا tar.gz یا zip معتبری نیست. ببین پوشه‌ای که فرستادی واقعاً فایلی داشته باشد.",
        inner);

    private static SourceArchiveRefusedException TooManyEntries() => new(
        $"Archive has more than {MaxEntries:N0} entries.",
        $"این فایل بیشتر از {MaxEntries:N0} مورد دارد.");

    internal static SourceArchiveRefusedException TooLarge() => new(
        $"Archive expands to more than {MaxUncompressedBytes / (1024 * 1024)} MB.",
        $"این فایل بعد از باز شدن بیشتر از {MaxUncompressedBytes / (1024 * 1024)} مگابایت می‌شود.");

    private static async Task<Result> ExtractTarGzAsync(Stream gzippedTar, string root, CancellationToken ct)
    {
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
                throw NotAnArchive(ex);
            }
            if (entry is null) break;

            if (++files > MaxEntries) throw TooManyEntries();

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
            if (bytes > MaxUncompressedBytes) throw TooLarge();

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var file = File.Create(target);
            if (entry.DataStream is not null)
                await entry.DataStream.CopyToAsync(file, ct);

            if ((entry.Mode & UnixFileMode.UserExecute) != 0) MarkExecutable(target);
        }

        return new Result(files, bytes);
    }

    /// <summary>
    /// The same guards as the tar path, with one more reason to be careful: a zip states each entry's
    /// size in a header nothing checks, so the ceiling is counted from what actually comes out of the
    /// decompressor rather than read from the entry.
    /// </summary>
    private static async Task<Result> ExtractZipAsync(Stream source, string root, CancellationToken ct)
    {
        try
        {
            using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);

            if (zip.Entries.Count > MaxEntries) throw TooManyEntries();

            var wrapper = SoleTopLevelFolder(zip.Entries);

            var files = 0;
            long bytes = 0;
            var buffer = new byte[81920];

            foreach (var entry in zip.Entries)
            {
                if (IsZipLink(entry)) continue;

                // Resolved before anything is skipped: "../../.env" is an escape, not a file to ignore.
                var target = ResolveSafePath(root, entry.FullName);
                if (target is null || IsLeftOutOfAnUpload(entry.FullName)) continue;

                if (wrapper is not null)
                {
                    // Null only for the folder's own entry. Resolved again, because taking a level off
                    // is exactly what could turn "shop/../x" into something that climbs out.
                    target = ResolveSafePath(root, Relative(entry.FullName)[(wrapper.Length + 1)..]);
                    if (target is null) continue;
                }

                if (IsZipDirectory(entry.FullName))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = entry.Open();
                await using var file = File.Create(target);
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    bytes += read;
                    if (bytes > MaxUncompressedBytes) throw TooLarge();
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }

                if (IsExecutableInZip(entry.ExternalAttributes)) MarkExecutable(target);

                files++;
            }

            // A zip of nothing but node_modules and .env is, for a build, an empty folder.
            if (files == 0) throw NotAnArchive();
            return new Result(files, bytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw NotAnArchive(ex);
        }
    }

    private static bool IsZipDirectory(string entryName) => entryName.EndsWith('/') || entryName.EndsWith('\\');

    /// <summary>
    /// The folder a zip wraps everything in, when it wraps everything: <c>my-shop-main/</c> from GitHub,
    /// or a folder somebody compressed. Taken off here so that nothing downstream ever sees it, because
    /// it is a fact about this one file: stored as the app's root it would fail on the next zip, which
    /// is called <c>repo-1.3/</c>, and on the next <c>harbora deploy</c>, which has no wrapper at all.
    /// Files beside it mean it is a real folder and stays. What is left out of an upload does not count
    /// as beside it, or every zip a Mac makes would keep its wrapper for the sake of <c>__MACOSX</c>.
    /// </summary>
    private static string? SoleTopLevelFolder(IEnumerable<ZipArchiveEntry> entries)
    {
        string? folder = null;
        foreach (var entry in entries)
        {
            if (IsZipLink(entry) || IsLeftOutOfAnUpload(entry.FullName)) continue;

            var name = Relative(entry.FullName);
            if (name.Length == 0) continue;

            var slash = name.IndexOf('/');
            if (slash < 0) return null;

            var top = name[..slash];
            if (top == ".." || (folder is not null && top != folder)) return null;
            folder = top;
        }

        return folder;
    }

    /// <summary>
    /// The owner's executable bit in a zip's Unix mode, and only that, which is all git itself keeps: a
    /// zip of a repository then comes out with the permissions a clone of it has. A file stored 0600 would
    /// otherwise be a 403 from an Apache running as www-data. A zip made on Windows carries no mode, which
    /// reads as not executable and leaves the file as it was created.
    /// </summary>
    internal static bool IsExecutableInZip(int externalAttributes) =>
        (((UnixFileMode)((externalAttributes >> 16) & 0x1FF)) & UnixFileMode.UserExecute) != 0;

    /// <summary>A <c>COPY</c> keeps the mode it finds, so <c>ENTRYPOINT ["./entrypoint.sh"]</c> depends on this. Windows has none to set.</summary>
    private static void MarkExecutable(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, Executable);
    }

    /// <summary>An entry's name the way the checks below read it: forward slashes, nothing in front.</summary>
    private static string Relative(string entryName)
    {
        var relative = entryName.Replace('\\', '/').TrimStart('/');

        // "./" prefixes are how tar normally records a directory-relative archive.
        while (relative.StartsWith("./", StringComparison.Ordinal)) relative = relative[2..];
        return relative;
    }

    /// <summary>
    /// A zip records a Unix mode in the top half of its external attributes, and that is the only place
    /// a symbolic link shows: extracted plainly it becomes a file whose content is the link's target.
    /// </summary>
    private static bool IsZipLink(ZipArchiveEntry entry)
    {
        const int fileTypeMask = 0xF000, symbolicLink = 0xA000;
        return ((entry.ExternalAttributes >> 16) & fileTypeMask) == symbolicLink;
    }

    /// <summary>
    /// What the CLI filters out on the client. A browser upload has no such step, and a <c>.env</c>
    /// usually holds real keys, so it stops here: at any depth, because a GitHub zip puts everything
    /// under one top-level folder. Only zips: the tar path is the CLI's, which has already done this.
    /// </summary>
    private static bool IsLeftOutOfAnUpload(string entryName)
    {
        var segments = entryName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;

        if (segments.Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase)
                              || s.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                              || s.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)))
            return true;

        // A folder that happens to be called ".env" holds no keys of its own; only a file does.
        return !IsZipDirectory(entryName)
            && (segments[^1].Equals(".env", StringComparison.OrdinalIgnoreCase)
                || segments[^1].Equals(".env.local", StringComparison.OrdinalIgnoreCase)
                || SystemLitter.Contains(segments[^1], StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Maps an archive entry name to an absolute path inside <paramref name="root"/>, or null when
    /// the entry is a no-op. Throws when the entry tries to escape.
    /// </summary>
    public static string? ResolveSafePath(string root, string entryName)
    {
        var relative = Relative(entryName);
        if (relative.Length == 0 || relative is "." or "./") return null;

        if (Path.IsPathRooted(entryName) || entryName.StartsWith('/'))
            throw new SourceArchiveRefusedException(
                $"Archive entry '{entryName}' has an absolute path.",
                $"مسیر «{entryName}» در این فایل مطلق است.");

        var full = Path.GetFullPath(Path.Combine(root, relative));

        // The decisive check: after resolving "..", the entry must still be under the destination.
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            throw new SourceArchiveRefusedException(
                $"Archive entry '{entryName}' escapes the build directory.",
                $"مسیر «{entryName}» در این فایل از پوشهٔ ساخت بیرون می‌زند.");

        return full;
    }
}

/// <summary>
/// An archive refused for what it is, worded for whoever sent it, in both languages: a person told a
/// file is empty when it is too large will send the same file again. Still an
/// <see cref="InvalidOperationException"/>, which is what every caller has always caught.
/// </summary>
public sealed class SourceArchiveRefusedException(string message, string messageFa, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    public string MessageFa { get; } = messageFa;
}
