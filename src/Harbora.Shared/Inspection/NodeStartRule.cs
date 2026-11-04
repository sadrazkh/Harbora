namespace Harbora.Shared.Inspection;

/// <summary>
/// Whether a <c>package.json</c> is an app or only a frontend's build tooling, which decides whether it
/// wins stack detection. A Laravel, Flask or ASP.NET repository usually has one for its assets, with
/// a <c>build</c> script and no way to start anything. Letting it win builds the app as Node, in an
/// image with no PHP, Python or .NET in it, where <c>npm start</c> fails on every deploy.
///
/// <para>
/// One rule, here, for the three things that have to agree on it: <c>Buildpacks.Detect</c>, which
/// decides the build; the inspector, which proposes it; and <c>harbora doctor</c>, which reports it.
/// All three can reach this project, so they share the code rather than a test holding copies together.
/// </para>
/// </summary>
public static class NodeStartRule
{
    /// <summary>What the deploy log and the confirm screen both say, so that they cannot say different things.</summary>
    public const string AssetsNotBuilt =
        "The package.json here has a build script, and the generated image does not run it: the frontend's assets are not built. To build them, add a Dockerfile.";

    /// <summary>
    /// A template's <c>start</c> that only runs a dev server, as a template writes it. Exact, with no
    /// flags: <c>ng serve --host 0.0.0.0</c> is somebody who deliberately made it reachable, and it may
    /// be working today.
    /// </summary>
    private static readonly HashSet<string> DevServers = new(StringComparer.Ordinal)
    {
        "react-scripts start", "react-app-rewired start", "craco start",
        "ng serve",
        "gatsby develop",
        "vue-cli-service serve",
        "vite",
        "webpack serve", "webpack-dev-server"
    };

    /// <summary>
    /// True for a <c>scripts.start</c>, or a <c>server.js</c> beside the file, which is what
    /// <c>npm start</c> runs when there is no script. A <c>start</c> in <see cref="DevServers"/> beside a
    /// <c>build</c> script does not count: a site that has a build should be built and served, not run
    /// from its development server. Most of these cannot serve in a container at all (they exit when
    /// stdin closes or bind to localhost). <c>vue-cli-service serve</c> may run today, and moves to the
    /// built site on its next deploy. All of that is from memory; a real project on CI confirms it.
    ///
    /// <para>
    /// A <c>package.json</c> it cannot read has no start script, whether malformed, over the inspector's
    /// 1 MiB limit or reached through a link. That only changes anything when another stack's marker is
    /// there too: alone, it stays Node, as it always did.
    /// </para>
    /// </summary>
    public static bool CanStart(string directory) => CanStart(new SourceTree(directory));

    /// <summary>A frontend that expects a build step. Only worth saying when something other than Node was chosen.</summary>
    public static bool HasBuildScript(string directory) => HasBuildScript(new SourceTree(directory));

    internal static bool CanStart(SourceTree folder) =>
        folder.HasFile("server.js") ||
        (NodeManifest.Script(folder, "start") is { } start &&
         !(DevServers.Contains(start.Trim()) && HasBuildScript(folder)));

    internal static bool HasBuildScript(SourceTree folder) => NodeManifest.Script(folder, "build") is not null;
}
