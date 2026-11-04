namespace Harbora.Shared.Inspection;

/// <summary>
/// What a folder builds as: a Dockerfile, then exactly what <c>Buildpacks.Detect</c> would choose, then a
/// compose file. <c>Harbora.Shared</c> cannot call the buildpacks, so this is a copy of their order, and
/// <c>SourceInspectorParityTests</c> runs the two side by side so that it cannot quietly become a different one.
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
        // entry file it answers null at once, so an index.html beside it does not make it a static site.
        if (app.HasFile("package.json")) return DetectedStack.Node;
        if (app.Csprojs().Count > 0) return DetectedStack.DotNet;
        if (app.HasFile("go.mod")) return DetectedStack.Go;
        if (HasAny(app, PhpMarkers)) return DetectedStack.Php;
        if (HasAny(app, PythonManifests))
        {
            if (HasAny(app, PythonEntries)) return DetectedStack.Python;
        }
        else if (HasAny(app, StaticMarkers))
        {
            return DetectedStack.Static;
        }

        return HasAny(app, ComposeNames) ? DetectedStack.Compose : DetectedStack.Unknown;
    }

    /// <summary>Something directly in this folder says the app is here. Project files further down do not count: see <see cref="LooksLikeAnApp"/>.</summary>
    public static bool HoldsSomething(SourceTree folder) =>
        HasAny(folder, DockerfileNames) || HasAny(folder, ComposeNames) || HasAny(folder, PhpMarkers) ||
        HasAny(folder, PythonManifests) || HasAny(folder, StaticMarkers) || HasAny(folder, DotNetRootMarkers) ||
        folder.HasFile("package.json") || folder.HasFile("go.mod") ||
        folder.FileNames().Any(n =>
            n.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
            n.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
            n.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));

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

        // A frontend's package.json beside a Laravel or Flask app wins detection and builds the wrong
        // thing: `npm start` in an image with no PHP. Parity says Node, so the person has to be told.
        if (stack == DetectedStack.Node && (app.HasFile("go.mod") || HasAny(app, PhpMarkers) || HasAny(app, PythonManifests)))
            yield return InspectionNotes.StackAmbiguous();
    }

    private static bool HasAny(SourceTree folder, string[] names) => names.Any(folder.HasFile);
}
