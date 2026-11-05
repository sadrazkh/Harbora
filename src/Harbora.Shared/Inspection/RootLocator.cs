namespace Harbora.Shared.Inspection;

/// <param name="Directory">Where the app lives, relative to the inspected root; null means the root itself.</param>
/// <param name="Candidates">Two or more folders that each look like an app. Empty otherwise.</param>
internal sealed record LocatedRoot(string? Directory, IReadOnlyList<string> Candidates);

/// <summary>Works out which folder is the app: a GitHub zip wraps it in one folder, a monorepo holds it in one of several.</summary>
internal static class RootLocator
{
    private const int MaxDescent = 8;

    // Each one examined costs a listing, and no person is choosing from a longer list than this.
    private const int MaxFoldersExamined = 100;

    /// <summary>
    /// A folder of tests or docs often has a project file of its own, and telling someone with a
    /// <c>src/</c> and a <c>tests/</c> that they have two apps is a question with one answer. Dropped
    /// only when something else is left: a site that lives in <c>docs/</c> is still that site.
    /// </summary>
    private static readonly HashSet<string> RarelyTheApp = new(StringComparer.OrdinalIgnoreCase)
    {
        "test", "tests", "spec", "specs", "e2e", "doc", "docs", "example", "examples", "sample", "samples", "demo", "demos"
    };

    /// <summary>Beside a tooling root these are that root's own, whatever is in them, rather than apps of their own.</summary>
    private static readonly HashSet<string> PartOfTheRoot = new(StringComparer.OrdinalIgnoreCase)
    {
        "src", "lib", "libs", "shared", "common", "scripts", "tools", "config", "public", "static", "assets"
    };

    /// <summary>Folders a monorepo keeps its apps one level inside, each with no project file of its own.</summary>
    private static readonly string[] AppContainers = ["apps", "services"];

    /// <summary>Where the libraries usually are, and the apps too in an older monorepo. Read only when nothing else was found.</summary>
    private const string PackagesContainer = "packages";

    /// <summary>The folders a build tool's <c>build</c> script can sit above and be the app's own source, rather than apps beside it.</summary>
    private static readonly string[] OwnSourceFolders = ["src", "app", "pages"];

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

        if (StackDetector.HoldsSomething(here))
        {
            var thisFolder = new LocatedRoot(path.Length == 0 ? null : path, []);
            if (!StackDetector.IsOnlyTooling(here) || HasItsOwnSource(here)) return thisFolder;

            var behind = AppsBehindTooling(here);

            // Nothing below it that runs is an ordinary project with no start script. And the pipeline
            // already builds a lone .NET project from the top, so a tooling root above one has never failed
            // and there is nothing to move: that is the frontend assets of an ASP.NET app.
            if (behind.Count == 0 || behind is [var only] && here.Scope(only).Csprojs().Count > 0) return thisFolder;

            return Answer(path, behind);
        }

        var apps = here.AppFolders().Take(MaxFoldersExamined)
            .Where(f => StackDetector.LooksLikeAnApp(here.Scope(f))).ToList();
        var likely = apps.Where(f => !RarelyTheApp.Contains(f)).ToList();
        if (likely.Count > 0) apps = likely;

        return Answer(path, apps);
    }

    private static LocatedRoot Answer(string path, IReadOnlyList<string> apps) => apps.Count switch
    {
        0 => new LocatedRoot(null, []),
        1 => new LocatedRoot(Join(path, apps[0]), []),
        _ => new LocatedRoot(null, apps.Select(a => Join(path, a)).OrderBy(a => a, StringComparer.Ordinal).ToList())
    };

    /// <summary>
    /// A <c>build</c> script beside a folder of plain source is a project that builds itself, like a
    /// Create React App whose <c>functions/</c> happens to be a package too. A folder that holds an app
    /// is not its own source, which is how <c>app/package.json</c> stays a candidate.
    /// </summary>
    private static bool HasItsOwnSource(SourceTree here) =>
        NodeStartRule.HasBuildScript(here) &&
        OwnSourceFolders.Any(f => here.HasDirectory(f) && !StackDetector.LooksLikeAnApp(here.Scope(f)));

    /// <summary>
    /// What a monorepo's root is standing in front of: <c>client/</c> and <c>server/</c>, or
    /// <c>apps/web</c> and <c>apps/api</c>. Without this the root's <c>package.json</c> is taken for the
    /// app and the deploy fails at <c>npm start</c>, without the question ever being asked.
    /// </summary>
    private static IReadOnlyList<string> AppsBehindTooling(SourceTree here)
    {
        bool Runs(string folder) => here.HasDirectory(folder) && StackDetector.CouldRunByItself(here.Scope(folder));

        IEnumerable<string> Inside(string container) => here.HasDirectory(container)
            ? here.Scope(container).AppFolders().Take(MaxFoldersExamined).Select(f => Join(container, f)).Where(Runs)
            : [];

        var apps = here.AppFolders().Take(MaxFoldersExamined)
            .Where(f => !PartOfTheRoot.Contains(f) && !RarelyTheApp.Contains(f) && !AppContainers.Contains(f) && f != PackagesContainer)
            .Where(Runs)
            .Concat(AppContainers.SelectMany(Inside))
            .ToList();

        return apps.Count > 0 ? apps : Inside(PackagesContainer).ToList();
    }

    private static string Join(string path, string folder) => path.Length == 0 ? folder : path + "/" + folder;
}
