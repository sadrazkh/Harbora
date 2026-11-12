using System.IO.Enumeration;

namespace Harbora.Shared;

/// <summary>
/// Which env files may leave a developer's machine: the one rule the CLI's packer and the panel's zip
/// filter both apply, so that <c>harbora deploy</c> and a zip dropped on <c>/launch</c> cannot disagree
/// about a file of secrets.
///
/// <para>
/// An env file holds real values unless it is an example. <c>.env</c> and every <c>.env.&lt;name&gt;</c>
/// stay home by default. Only <c>.env</c> and <c>.env.local</c> used to: a folder that was never in
/// Git has no ignore file to name the rest, so <c>.env.production</c> and <c>.env.staging</c> went to
/// the server with every push. The examples still go, because they are where Launch learns a
/// project's variable names.
/// </para>
///
/// <para>
/// Vite, Next.js and Create React App read <c>.env.production</c> when they build, and some projects
/// commit one on purpose with public values, so leaving it out changes what such a build produces.
/// That is why a <c>!</c> line in the ignore list lets one through, and why whatever leaves one out
/// names it. <c>.env</c> and <c>.env.local</c> are the exception and never go: the first is on the
/// do-not-change list, and the second is the file <c>harbora env pull</c> fills with an app's
/// decrypted secrets.
/// </para>
/// </summary>
public static class EnvFiles
{
    /// <summary>The example files the inspector reads variable names from (<c>ExampleEnvFiles</c>), in its order.</summary>
    public static IReadOnlyList<string> ExampleNames { get; } =
        [".env.example", ".env.sample", ".env.template", ".env.dist", "env.example", "example.env", "sample.env"];

    /// <summary>Kept back whatever any ignore file says.</summary>
    public static IReadOnlyList<string> NeverUploaded { get; } = [".env", ".env.local"];

    /// <summary>
    /// The last word of an example's name: <c>.env.local.example</c>, which Next.js's own examples ship,
    /// holds no more than <c>.env.example</c> does.
    /// </summary>
    private static readonly string[] ExampleSuffixes = ["example", "sample", "template", "dist"];

    /// <summary>A file with no real values in it, uploaded like any other source file.</summary>
    public static bool IsExample(string fileName) =>
        ExampleNames.Contains(fileName, StringComparer.OrdinalIgnoreCase)
        || (fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
            && ExampleSuffixes.Contains(fileName[(fileName.LastIndexOf('.') + 1)..], StringComparer.OrdinalIgnoreCase));

    /// <summary><c>.env</c>, or <c>.env.</c> and anything, that is not an example. <c>.envrc</c> is not one.</summary>
    public static bool HoldsRealValues(string fileName) =>
        (fileName.Equals(".env", StringComparison.OrdinalIgnoreCase)
         || fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
        && !IsExample(fileName);

    /// <summary>One whose name a <c>!</c> line can bring back, real or example. <see cref="NeverUploaded"/> is asked about first.</summary>
    public static bool IsEnvFile(string fileName) => HoldsRealValues(fileName) || IsExample(fileName);

    /// <summary>
    /// Whether the file at <paramref name="relativePath"/> stays on the machine it is on.
    /// </summary>
    /// <param name="relativePath">Forward slashes, from the project's root.</param>
    /// <param name="ignoreEntries">The project's ignore list as <see cref="IgnoreFile.Entries"/> reads it.</param>
    public static bool StaysHome(string relativePath, IEnumerable<string> ignoreEntries)
    {
        var name = FileName(relativePath);
        if (NeverUploaded.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        return HoldsRealValues(name) && !IsLetThrough(relativePath, ignoreEntries);
    }

    /// <summary>
    /// Whether a <c>!</c> line in the ignore list names this file. A name with no slash matches at any
    /// depth, the way <c>.gitignore</c> reads one; a path matches from the project's root; <c>*</c> and
    /// <c>?</c> are wildcards. Only an env file is ever brought back this way: every other <c>!</c> line
    /// is still not read, as it never was.
    /// </summary>
    public static bool IsLetThrough(string relativePath, IEnumerable<string> ignoreEntries)
    {
        var path = relativePath.Replace('\\', '/').TrimStart('/');
        var name = FileName(path);
        if (!IsEnvFile(name)) return false;

        foreach (var entry in ignoreEntries)
        {
            if (entry.Length < 2 || entry[0] != '!') continue;

            var pattern = entry[1..].Trim().Replace('\\', '/').Trim('/');
            if (pattern.Length == 0) continue;

            var anyDepth = !pattern.Contains('/');
            var matches = pattern.Contains('*') || pattern.Contains('?')
                ? FileSystemName.MatchesSimpleExpression(pattern, path)
                  || (anyDepth && FileSystemName.MatchesSimpleExpression(pattern, name))
                : path.Equals(pattern, StringComparison.OrdinalIgnoreCase)
                  || (anyDepth && name.Equals(pattern, StringComparison.OrdinalIgnoreCase));

            if (matches) return true;
        }

        return false;
    }

    private static string FileName(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        return path[(path.LastIndexOf('/') + 1)..];
    }
}
