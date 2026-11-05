namespace Harbora.Shared.Inspection;

/// <summary>
/// What a folder builds as: a Dockerfile, then exactly what <c>Buildpacks.Detect</c> would choose, then a
/// compose file. <c>Harbora.Shared</c> cannot call the buildpacks, so this is a copy of their order, and
/// <c>SourceInspectorParityTests</c> runs the two side by side so that it cannot quietly become a different
/// one. Only the question of whether a <c>package.json</c> counts is shared code: <see cref="NodeStartRule"/>.
/// </summary>
internal static class StackDetector
{
    /// <summary>The names the pipeline accepts as a Dockerfile it did not generate; <c>Dockerfile.harbora</c> is the CLI's inline one.</summary>
    public static readonly string[] DockerfileNames = ["Dockerfile", "Dockerfile.harbora"];

    /// <summary>The names <c>DeploymentPipeline.LoadComposeAsync</c> looks for, in its order.</summary>
    private static readonly string[] ComposeNames = ["docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml"];

    private static readonly string[] PhpMarkers = ["composer.json", "index.php"];
    private static readonly string[] PythonManifests = ["requirements.txt", "pyproject.toml", "Pipfile"];
    private static readonly string[] StaticMarkers = ["index.html", "index.htm"];

    /// <summary><c>Buildpacks.PythonEntryCandidates</c>. <c>manage.py</c> is left out there on purpose, and here.</summary>
    private static readonly string[] PythonEntries =
        ["app.py", "main.py", "wsgi.py", "server.py", "bot.py", "run.py", "start.py", "__main__.py"];

    /// <summary>A .NET repository is built from its top even when every project sits in a subfolder, and these say so.</summary>
    private static readonly string[] DotNetRootMarkers = ["global.json", "Directory.Build.props", "Directory.Packages.props"];

    public static DetectedStack Detect(SourceTree app)
    {
        if (HasAny(app, DockerfileNames)) return DetectedStack.Dockerfile;

        // Buildpacks.Detect, in its order. Python is the one branch that can return "nothing": with no
        // entry file it answers null at once, so neither a package.json nor an index.html beside it
        // gets a turn. A package.json that cannot start anything comes after every other stack's
        // marker and still before a static site, which is where a frontend's own index.html is.
        var hasPackage = app.HasFile("package.json");
        if (hasPackage && NodeStartRule.CanStart(app)) return DetectedStack.Node;
        if (app.Csprojs().Count > 0) return DetectedStack.DotNet;
        if (app.HasFile("go.mod")) return DetectedStack.Go;
        if (HasAny(app, PhpMarkers)) return DetectedStack.Php;
        if (HasAny(app, PythonManifests))
        {
            if (HasAny(app, PythonEntries)) return DetectedStack.Python;
        }
        else if (hasPackage)
        {
            return DetectedStack.Node;
        }
        else if (HasAny(app, StaticMarkers))
        {
            return DetectedStack.Static;
        }

        return HasAny(app, ComposeNames) ? DetectedStack.Compose : DetectedStack.Unknown;
    }

    /// <summary>
    /// The one part of <c>Buildpacks.Detect</c> that needs the app's kind and start command, which the
    /// inspector only knows after reading the project files. A worker, or anything with its own start
    /// command, does not care that <c>npm start</c> has nothing to run, and a Python project with one
    /// has no need of an entry file.
    /// </summary>
    public static DetectedStack WithKnownKind(SourceTree app, DetectedStack stack, DetectedAppKind kind, string? startCommand)
    {
        var hasCommand = !string.IsNullOrWhiteSpace(startCommand);
        if (stack == DetectedStack.Unknown && hasCommand && IsPythonWithNoEntry(app)) return DetectedStack.Python;

        return stack == DetectedStack.Node && kind == DetectedAppKind.Web && !hasCommand &&
               !NodeStartRule.CanStart(app) && NodeStartRule.HasBuildScript(app)
            ? DetectedStack.Frontend
            : stack;
    }

    /// <summary>A Python project the image would have nothing to run for, unless something else says what to run.</summary>
    public static bool IsPythonWithNoEntry(SourceTree app) =>
        HasAny(app, PythonManifests) && !HasAny(app, PythonEntries);

    /// <summary>Something directly in this folder says the app is here. Project files further down do not count: see <see cref="LooksLikeAnApp"/>.</summary>
    public static bool HoldsSomething(SourceTree folder) =>
        folder.HasFile("package.json") || HasAny(folder, StaticMarkers) || HoldsSomethingToRun(folder);

    /// <summary>What says an app is here even though a <c>package.json</c> or an <c>index.html</c> might be there for some other reason.</summary>
    private static bool HoldsSomethingToRun(SourceTree folder) =>
        HasAny(folder, DockerfileNames) || HasAny(folder, ComposeNames) || HasAny(folder, PhpMarkers) ||
        HasAny(folder, PythonManifests) || HasAny(folder, DotNetRootMarkers) ||
        folder.HasFile("go.mod") ||
        folder.FileNames().Any(n =>
            n.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
            n.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
            n.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A <c>package.json</c> that can start nothing, and nothing else in this folder that can: the root
    /// of a JavaScript monorepo, holding its build tooling while the apps live further down. Not a Laravel
    /// with a <c>resources/</c> tree, whose <c>composer.json</c> says otherwise, and not a Vite project
    /// with its <c>index.html</c> beside it, or a Procfile saying how this folder runs.
    /// </summary>
    public static bool IsOnlyTooling(SourceTree folder) =>
        folder.HasFile("package.json") && !NodeStartRule.CanStart(folder) &&
        !HasAny(folder, StaticMarkers) && !folder.HasFile("Procfile") && !HoldsSomethingToRun(folder);

    /// <summary>A folder that could be run by itself, which a folder holding only an <c>index.html</c> usually cannot: that is a <c>public/</c>.</summary>
    public static bool CouldRunByItself(SourceTree folder) =>
        folder.HasFile("package.json") || HoldsSomethingToRun(folder) || folder.Csprojs().Count > 0;

    /// <summary>
    /// Whether a candidate folder would count as an app. A nested project does, because the pipeline
    /// would find it from there, but it is not enough to stop the search at the folder above it:
    /// that would make <c>backend/</c> and <c>frontend/</c> one .NET app with a stray folder beside it.
    /// </summary>
    public static bool LooksLikeAnApp(SourceTree folder) => HoldsSomething(folder) || folder.Csprojs().Count > 0;

    public static IEnumerable<InspectionNote> NotesFor(SourceTree app, DetectedStack stack, bool severalApps)
    {
        if (stack == DetectedStack.Compose) yield return InspectionNotes.ComposeRunsOwnServices();

        var django = app.HasFile("manage.py");

        // With an entry file beside it the buildpack still runs `python wsgi.py`, which for Django
        // defines an application and exits. The stack stands, since that is what would be built.
        if (stack == DetectedStack.Python && django) yield return InspectionNotes.DjangoUnsupported();

        if (stack == DetectedStack.Unknown && !severalApps)
        {
            if (!HasAny(app, PythonManifests)) yield return InspectionNotes.NoStackRecognised();
            else if (django) yield return InspectionNotes.DjangoUnsupported();
            else yield return InspectionNotes.PythonEntryMissing(PythonEntries);
        }

        // Node beside another stack's marker can only mean its package.json can start something. That
        // is usually the right call and sometimes a dev server's script, and only a person can tell.
        if (stack == DetectedStack.Node &&
            (app.Csprojs().Count > 0 || app.HasFile("go.mod") || HasAny(app, PhpMarkers) || HasAny(app, PythonManifests)))
            yield return InspectionNotes.StackAmbiguous();

        // The other side of that decision: the package.json was passed over, and whatever its build
        // script produces will be missing from a page that otherwise works. A Dockerfile builds what
        // it likes, and Unknown has bigger news.
        if (stack is DetectedStack.DotNet or DetectedStack.Go or DetectedStack.Php or DetectedStack.Python &&
            NodeStartRule.HasBuildScript(app))
            yield return InspectionNotes.FrontendAssetsNotBuilt();
    }

    private static bool HasAny(SourceTree folder, string[] names) => names.Any(folder.HasFile);
}
