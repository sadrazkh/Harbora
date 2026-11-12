namespace Harbora.Shared;

/// <summary>
/// A project's ignore file as an upload reads it, on the developer's machine and in the panel alike:
/// <c>.dockerignore</c> if there is one, because it is what the build itself reads, else
/// <c>.gitignore</c>, and never both.
/// </summary>
public static class IgnoreFile
{
    /// <summary>In precedence order. The first that exists is the only one read.</summary>
    public static IReadOnlyList<string> Names { get; } = [".dockerignore", ".gitignore"];

    /// <summary>
    /// The lines that say something: comments and blank lines dropped, separators forward, no slash at
    /// either end. A <c>!</c> line keeps its <c>!</c>, because one naming an env file lets that file
    /// through (<see cref="EnvFiles.IsLetThrough"/>); a pattern match never treats it as an exclusion.
    /// </summary>
    public static List<string> Entries(IEnumerable<string> lines)
    {
        var entries = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var negated = line[0] == '!';
            var pattern = (negated ? line[1..] : line).Trim().Replace('\\', '/').Trim('/');
            if (pattern.Length == 0) continue;

            entries.Add(negated ? "!" + pattern : pattern);
        }
        return entries;
    }
}
