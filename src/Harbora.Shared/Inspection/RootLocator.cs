namespace Harbora.Shared.Inspection;

/// <param name="Directory">Where the app lives, relative to the inspected root; null means the root itself.</param>
/// <param name="Candidates">Two or more folders that each look like an app. Empty otherwise.</param>
internal sealed record LocatedRoot(string? Directory, IReadOnlyList<string> Candidates);

/// <summary>Works out which folder is the app: a GitHub zip wraps it in one folder, a monorepo holds it in one of several.</summary>
internal static class RootLocator
{
    private const int MaxDescent = 8;

    /// <summary>
    /// A folder of tests or docs often has a project file of its own, and telling someone with a
    /// <c>src/</c> and a <c>tests/</c> that they have two apps is a question with one answer. Dropped
    /// only when something else is left: a site that lives in <c>docs/</c> is still that site.
    /// </summary>
    private static readonly HashSet<string> RarelyTheApp = new(StringComparer.OrdinalIgnoreCase)
    {
        "test", "tests", "spec", "specs", "e2e", "doc", "docs", "example", "examples", "sample", "samples", "demo", "demos"
    };

    public static LocatedRoot Locate(SourceTree tree)
    {
        var path = "";
        var here = tree;
        for (var i = 0; i < MaxDescent && !StackDetector.HoldsSomething(here); i++)
        {
            if (here.SoleChildFolder() is not { } only) break;
            path = Join(path, only);
            here = here.Scope(only);
        }

        if (StackDetector.HoldsSomething(here)) return new LocatedRoot(path.Length == 0 ? null : path, []);

        var apps = here.AppFolders().Where(f => StackDetector.LooksLikeAnApp(here.Scope(f))).ToList();
        var likely = apps.Where(f => !RarelyTheApp.Contains(f)).ToList();
        if (likely.Count > 0) apps = likely;

        return apps.Count switch
        {
            0 => new LocatedRoot(null, []),
            1 => new LocatedRoot(Join(path, apps[0]), []),
            _ => new LocatedRoot(null, apps.Select(a => Join(path, a)).OrderBy(a => a, StringComparer.Ordinal).ToList())
        };
    }

    private static string Join(string path, string folder) => path.Length == 0 ? folder : path + "/" + folder;
}
