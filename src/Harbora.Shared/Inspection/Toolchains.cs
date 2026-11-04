namespace Harbora.Shared.Inspection;

/// <summary>
/// Which version of a language's tools a project says it needs, for the generated images. Public and
/// here rather than in the buildpacks because reading a stranger's project file safely (nothing over
/// 1 MiB, no links followed, no XML entities) is what <see cref="SourceTree"/> is for.
/// </summary>
public static class Toolchains
{
    /// <summary>
    /// The major version a .NET project targets, from the project and then from the
    /// <c>Directory.Build.props</c> beside the build, which is where a multi-project repository usually
    /// keeps it. Null when neither says plainly.
    /// </summary>
    /// <param name="directory">The root of the build.</param>
    /// <param name="project">The project file, relative to <paramref name="directory"/>.</param>
    public static int? DotNetMajor(string directory, string project)
    {
        var tree = new SourceTree(directory);
        return DotNetManifest.TargetMajor(tree, project.Replace('\\', '/'))
               ?? DotNetManifest.TargetMajor(tree, "Directory.Build.props");
    }

    /// <summary>The language version a <c>go.mod</c> asks for, or null when it does not say.</summary>
    public static Version? GoLanguage(string directory) => GoManifest.LanguageVersion(new SourceTree(directory));

    /// <summary>The package to build when it is not the top of the module. See <see cref="GoManifest.SoleCommand"/>.</summary>
    public static string? GoCommand(string directory) => GoManifest.SoleCommand(new SourceTree(directory));
}
