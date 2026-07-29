using System.Formats.Tar;
using System.IO.Enumeration;
using System.IO.Compression;

namespace Harbora.Cli;

/// <summary>
/// Packs the project folder into the gzipped tar that <c>harbora deploy</c> pushes to the server.
///
/// Exclusions matter more than they look: shipping <c>node_modules</c>, <c>.git</c> or a local
/// <c>.env</c> would turn a two-second push into a hundred-megabyte one and could send credentials
/// to the server. <c>.dockerignore</c> is honoured first (it is what the build actually uses), then
/// <c>.gitignore</c>, then a small built-in list so an unconfigured project still behaves.
/// </summary>
public static class SourcePacker
{
    /// <summary>Always skipped, even with no ignore file — heavy, machine-local, or secret.</summary>
    public static readonly string[] AlwaysExclude =
    [
        ".git", ".hg", ".svn",
        "node_modules", "bower_components", "vendor",
        "bin", "obj", "target", "dist", "build", ".next", ".nuxt", ".output",
        ".venv", "venv", "__pycache__", ".pytest_cache",
        ".idea", ".vs", ".vscode",
        ".DS_Store", "Thumbs.db",
        ".env", ".env.local",
        ".terraform", ".gradle"
    ];

    public sealed record Packed(string ArchivePath, int Files, long Bytes);

    public static async Task<Packed> PackAsync(string projectDir, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(projectDir);
        var ignore = LoadIgnorePatterns(root);

        var archivePath = Path.Combine(Path.GetTempPath(), $"harbora-{Guid.NewGuid():N}.tar.gz");
        var files = 0;
        long bytes = 0;

        await using (var output = File.Create(archivePath))
        await using (var gz = new GZipStream(output, CompressionLevel.Optimal))
        await using (var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (IsExcluded(relative, ignore)) continue;

                await tar.WriteEntryAsync(file, relative, ct);
                files++;
                bytes += new FileInfo(file).Length;
            }
        }

        return new Packed(archivePath, files, bytes);
    }

    /// <summary>
    /// Reads .dockerignore, else .gitignore. Supports the common subset: comments, blank lines,
    /// directory and wildcard entries. Deliberately not a full gitignore implementation — anything
    /// it misses is included, which is the safe direction to be wrong in.
    /// </summary>
    public static List<string> LoadIgnorePatterns(string root)
    {
        foreach (var name in new[] { ".dockerignore", ".gitignore" })
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path)) continue;

            return File.ReadAllLines(path)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith('!'))
                .Select(l => l.Replace('\\', '/').Trim('/'))
                .Where(l => l.Length > 0)
                .ToList();
        }
        return [];
    }

    public static bool IsExcluded(string relativePath, IReadOnlyCollection<string> ignore)
    {
        var segments = relativePath.Split('/');

        // Built-ins match any path segment: "node_modules" anywhere in the tree, not just at the root.
        if (segments.Any(s => AlwaysExclude.Contains(s, StringComparer.OrdinalIgnoreCase)))
            return true;

        foreach (var pattern in ignore)
        {
            if (pattern.Contains('*'))
            {
                if (segments.Any(s => FileSystemName.MatchesSimpleExpression(pattern, s)) ||
                    FileSystemName.MatchesSimpleExpression(pattern, relativePath))
                    return true;
            }
            else if (relativePath.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                     relativePath.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase) ||
                     segments.Contains(pattern, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
