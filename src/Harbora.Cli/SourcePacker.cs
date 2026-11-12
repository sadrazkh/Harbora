using System.Formats.Tar;
using System.IO.Enumeration;
using System.IO.Compression;
using Harbora.Shared;

namespace Harbora.Cli;

/// <summary>
/// Packs the project folder into the gzipped tar that <c>harbora deploy</c> pushes to the server.
///
/// Exclusions matter more than they look: shipping <c>node_modules</c>, <c>.git</c> or a local
/// <c>.env</c> would turn a two-second push into a hundred-megabyte one and could send credentials
/// to the server. <c>.dockerignore</c> is honoured first (it is what the build actually uses), then
/// <c>.gitignore</c>, then a small built-in list so an unconfigured project still behaves. Env files
/// follow <see cref="EnvFiles"/>, the rule the panel applies to a zip as well.
/// </summary>
public static class SourcePacker
{
    /// <summary>
    /// Always skipped, even with no ignore file — heavy, machine-local, or secret, wherever in the
    /// tree it appears. Nobody puts real source inside a directory called <c>node_modules</c>, no
    /// matter how deep; the same is true of the rest of this list.
    /// </summary>
    public static readonly string[] AlwaysExclude =
    [
        ".git", ".hg", ".svn",
        "node_modules", "bower_components",
        "bin", "obj",
        ".next", ".nuxt",
        ".venv", "venv", "__pycache__", ".pytest_cache",
        ".idea", ".vs", ".vscode",
        ".DS_Store", "Thumbs.db",
        // Never uploaded, whatever an ignore file says. As a folder name ".env" is just as often a
        // Python virtualenv, which is the other reason it stays on this list.
        .. EnvFiles.NeverUploaded,
        ".terraform", ".gradle"
    ];

    /// <summary>
    /// The reason an env file holding real values gives when it is left out. The rest of the rule,
    /// and the way back in, is <see cref="EnvFiles"/>.
    /// </summary>
    public const string EnvFileRule = "built-in rule '.env.*'";

    /// <summary>
    /// Also skipped with no ignore file, but only when the name is the very first path segment —
    /// i.e. a direct child of the folder being packed. "build", "dist", "target" and "vendor" are
    /// exactly the build-output names an unconfigured project uses by default (npm's <c>./build</c>
    /// or <c>./dist</c>, Cargo's <c>./target</c>, a PHP/Go <c>./vendor</c>, Nuxt's <c>./.output</c>) —
    /// but unlike <see cref="AlwaysExclude"/>'s tool-specific names, every one of these is also a
    /// perfectly ordinary *source* directory name anywhere deeper in a tree (a "build" helper-script
    /// folder, a vendored-customization folder called "vendor"). Matching them at any depth is what
    /// silently dropped DriveUnion's own <c>Scripts/build/copy-fonts.mjs</c> and broke its image build
    /// with no error anywhere in the log — matching them only at the root still catches the common
    /// unconfigured-project case without doing that. A project whose real output lives somewhere else
    /// should say so in .dockerignore, which is honoured first (see <see cref="LoadIgnorePatterns"/>);
    /// this list is only the backstop for a project that names nothing.
    /// </summary>
    public static readonly string[] RootOnlyExclude =
    [
        "build", "dist", "target", "vendor", ".output"
    ];

    /// <summary>
    /// A file the packer left out of the archive, and which rule did it — so a customer can find out
    /// *what* went missing, not just how many. An upload that only ever said "Unpacked 215 entries"
    /// left no way to discover that 130 of a project's 345 tracked files had been silently dropped.
    /// </summary>
    /// <param name="EnvFile">
    /// An env file kept on this machine because it holds real values, which a push names on its own
    /// with what to do about it: a build may have been reading it. Not one that went with the folder
    /// it was in, such as anything under <c>node_modules</c>.
    /// </param>
    public sealed record ExcludedEntry(string Path, string Reason, bool EnvFile = false);

    public sealed record Packed(string ArchivePath, int Files, long Bytes, IReadOnlyList<ExcludedEntry> Excluded);

    public static Task<Packed> PackAsync(string projectDir, CancellationToken ct = default) =>
        PackAsync(projectDir, new ProjectConfig(), ct);

    public static async Task<Packed> PackAsync(string projectDir, ProjectConfig config, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(projectDir);
        var ignore = LoadIgnorePatterns(root);
        // harbora.yml adds to the ignore files rather than replacing them.
        ignore.AddRange(config.Ignore);

        var archivePath = Path.Combine(Path.GetTempPath(), $"harbora-{Guid.NewGuid():N}.tar.gz");
        var files = 0;
        long bytes = 0;
        var excluded = new List<ExcludedEntry>();

        await using (var output = File.Create(archivePath))
        await using (var gz = new GZipStream(output, CompressionLevel.Optimal))
        await using (var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: true))
        {
            // An inline Dockerfile from harbora.yml is written into the archive, so a project can
            // describe its build without keeping a Dockerfile in the repository.
            if (config.DockerfileLines.Count > 0)
            {
                var generated = Path.Combine(Path.GetTempPath(), $"harbora-df-{Guid.NewGuid():N}");
                await File.WriteAllLinesAsync(generated, config.DockerfileLines, ct);
                try
                {
                    await tar.WriteEntryAsync(generated, "Dockerfile.harbora", ct);
                    files++;
                }
                finally { try { File.Delete(generated); } catch { /* temp */ } }
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (Classify(relative, ignore) is { } exclusion)
                {
                    excluded.Add(exclusion);
                    continue;
                }

                await tar.WriteEntryAsync(file, relative, ct);
                files++;
                bytes += new FileInfo(file).Length;
            }
        }

        return new Packed(archivePath, files, bytes, excluded);
    }

    /// <summary>
    /// Reads .dockerignore, else .gitignore. Supports the common subset: comments, blank lines,
    /// directory and wildcard entries. Deliberately not a full gitignore implementation — anything
    /// it misses is included, which is the safe direction to be wrong in. A <c>!</c> line is kept,
    /// with its <c>!</c>, for one purpose only: one naming an env file lets that file through.
    /// </summary>
    public static List<string> LoadIgnorePatterns(string root)
    {
        foreach (var name in IgnoreFile.Names)
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path)) continue;

            return IgnoreFile.Entries(File.ReadAllLines(path));
        }
        return [];
    }

    public static bool IsExcluded(string relativePath, IReadOnlyCollection<string> ignore) =>
        DescribeExclusion(relativePath, ignore) is not null;

    /// <summary>
    /// The same decision <see cref="IsExcluded"/> makes, but naming which rule matched instead of
    /// just yes/no — a built-in name, a root-only built-in name, or the exact .dockerignore/.gitignore
    /// pattern — so a dropped file can be explained rather than just counted.
    /// </summary>
    public static string? DescribeExclusion(string relativePath, IReadOnlyCollection<string> ignore) =>
        Classify(relativePath, ignore)?.Reason;

    private static ExcludedEntry? Classify(string relativePath, IReadOnlyCollection<string> ignore)
    {
        var segments = relativePath.Split('/');

        // Built-ins match any path segment: "node_modules" anywhere in the tree, not just at the root.
        var builtin = Array.FindIndex(segments, s => AlwaysExclude.Contains(s, StringComparer.OrdinalIgnoreCase));
        if (builtin >= 0)
            return new(relativePath, $"built-in rule '{segments[builtin]}'",
                EnvFile: builtin == segments.Length - 1 && EnvFiles.StaysHome(relativePath, []));

        // The ambiguous names only count at the root — see RootOnlyExclude's own comment for why.
        if (RootOnlyExclude.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
            return new(relativePath, $"built-in rule '{segments[0]}' (project root)");

        // A line naming an env file outranks the patterns around it, so ".env*" followed by
        // "!.env.example" sends the example, as Git and Docker would.
        if (EnvFiles.IsLetThrough(relativePath, ignore)) return null;
        if (EnvFiles.StaysHome(relativePath, ignore))
            return new(relativePath, EnvFileRule, EnvFile: true);

        var pattern = MatchingIgnorePattern(relativePath, ignore);
        return pattern is null ? null : new(relativePath, $"ignore pattern '{pattern}'");
    }

    /// <summary>
    /// Every env file under <paramref name="projectDir"/> that holds real values, and whether a push
    /// would send it, by the same decision <see cref="PackAsync(string, ProjectConfig, CancellationToken)"/>
    /// makes and without packing anything: for <c>harbora doctor</c>, which says so before a deploy
    /// does. One inside a folder that is left out anyway, such as <c>node_modules</c>, is not listed.
    /// </summary>
    public static IReadOnlyList<(string Path, bool Sent)> SurveyEnvFiles(string projectDir, ProjectConfig config)
    {
        var root = Path.GetFullPath(projectDir);
        var ignore = LoadIgnorePatterns(root);
        ignore.AddRange(config.Ignore);

        var found = new List<(string Path, bool Sent)>();
        foreach (var file in Directory.EnumerateFiles(root, ".env*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!EnvFiles.HoldsRealValues(Path.GetFileName(relative))) continue;

            switch (Classify(relative, ignore))
            {
                case null: found.Add((relative, true)); break;
                case { EnvFile: true }: found.Add((relative, false)); break;
            }
        }
        return found.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The first ignore-file pattern that matches <paramref name="relativePath"/>, or null — the
    /// wildcard/segment rules <see cref="DescribeExclusion"/> applies to a loaded .gitignore/.dockerignore,
    /// with none of <see cref="AlwaysExclude"/>/<see cref="RootOnlyExclude"/>'s built-ins layered on
    /// top. Exposed separately for <c>Doctor.CheckEnvLocal</c> (4.1, 2026-09-04 local-dev-parity plan):
    /// <c>.env.local</c> is itself one of those built-ins, so asking <see cref="DescribeExclusion"/>
    /// whether it is excluded would always say yes, whether or not a real <c>.gitignore</c> says
    /// anything about it at all.
    /// </summary>
    public static string? MatchingIgnorePattern(string relativePath, IReadOnlyCollection<string> patterns)
    {
        var segments = relativePath.Split('/');
        foreach (var pattern in patterns)
        {
            // A negation excludes nothing; the only one ever read is EnvFiles.IsLetThrough's.
            if (pattern.StartsWith('!')) continue;

            if (pattern.Contains('*'))
            {
                if (segments.Any(s => FileSystemName.MatchesSimpleExpression(pattern, s)) ||
                    FileSystemName.MatchesSimpleExpression(pattern, relativePath))
                    return pattern;
            }
            else if (relativePath.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                     relativePath.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase) ||
                     segments.Contains(pattern, StringComparer.OrdinalIgnoreCase))
            {
                return pattern;
            }
        }
        return null;
    }
}
