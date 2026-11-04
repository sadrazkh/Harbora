namespace Harbora.Shared.Inspection;

/// <summary>
/// The variables a frontend's build reads and writes into the files it serves. A browser has no
/// environment, so these are decided at build time, and a value set only on the running container
/// does nothing at all.
///
/// <para>
/// The prefixes are each framework's own line between public and private, and they are the only
/// thing that may cross into a frontend's build. What is built there is sent to every visitor, so a
/// name without one of them must never reach it, whatever else it has been marked as. On the Node
/// image, where a Next.js client bundle has the same need, they reach the build too, unmarked.
/// </para>
/// </summary>
public static class PublicBuildVariables
{
    private static readonly string[] Prefixes =
        ["VITE_", "REACT_APP_", "NEXT_PUBLIC_", "PUBLIC_", "VUE_APP_", "GATSBY_", "NUXT_PUBLIC_"];

    /// <summary>Exact case, as the frameworks compare it. The name also has to be one a Dockerfile's <c>ARG</c> can declare.</summary>
    public static bool IsPublic(string name) =>
        Prefixes.Any(p => name.Length > p.Length && name.StartsWith(p, StringComparison.Ordinal)) &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>The public names in a folder's example env files. Their example values are not returned: an example is not a setting.</summary>
    public static IReadOnlyList<string> NamedInExampleFiles(string directory) =>
        ExampleEnvFiles.Read(new SourceTree(directory), out _).Select(v => v.Name).Where(IsPublic).ToList();
}
