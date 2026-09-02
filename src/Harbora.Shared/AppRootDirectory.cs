namespace Harbora.Shared;

/// <summary>
/// An app's <b>root directory</b>: the sub-path within the repository or upload that the build runs
/// from. A monorepo holding <c>api/</c>, <c>web/</c> and <c>worker/</c> deploys as three apps by
/// giving each one a different root directory.
///
/// <para>
/// Everything the build looks up resolves under it — the Dockerfile, buildpack stack detection, and
/// the Docker build context itself. The release command needs no separate rule: it runs inside the
/// built image, so its working directory is whatever <c>WORKDIR</c> the Dockerfile (or the generated
/// buildpack) under this root directory set.
/// </para>
///
/// <para>
/// <b>The upload is deliberately not narrowed to it.</b> Sub-directory builds routinely need a
/// <c>packages/</c> or <c>shared/</c> folder and a lockfile at the repository root, so
/// <c>SourcePacker</c> keeps packing the whole tree and this setting only moves where the build
/// starts. The consequence — files above the root directory are uploaded but are <i>not</i> in the
/// Docker build context, so <c>COPY</c> cannot reach them — is the one genuinely surprising part, and
/// is reported by name by <c>harbora doctor</c> and in the deploy log rather than discovered as a
/// build failure.
/// </para>
///
/// <para>
/// Persisted as <c>App.BuildContextPath</c>, which has always meant exactly this and has been in the
/// schema since the initial migration; this type is the validation and normalisation that column
/// never had.
/// </para>
/// </summary>
public static class AppRootDirectory
{
    /// <summary>What the column holds for "build from the repository root".</summary>
    public const string RepositoryRoot = ".";

    /// <summary>Names the repository root itself — null, empty, "." or "./" all mean this.</summary>
    public static bool IsRepositoryRoot(string? value) => Normalise(value).Length == 0;

    /// <summary>
    /// The stored form: forward slashes, no leading <c>./</c>, no surrounding slashes. An empty
    /// string means the repository root.
    ///
    /// <para>
    /// Deliberately <b>not</b> <c>TrimStart('.', '/', '\\')</c>, which is what the pipeline used to
    /// do: that turns <c>../other</c> into <c>other</c> and quietly builds a different directory than
    /// the one that was asked for. A leading <c>./</c> is stripped only as a whole segment, so a
    /// <c>..</c> segment survives normalisation and reaches <see cref="Validate"/>, which refuses it.
    /// </para>
    /// </summary>
    public static string Normalise(string? value)
    {
        var path = (value ?? "").Trim().Replace('\\', '/');

        // Strip "./" segments from the front, whole segments only.
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        path = path.Trim('/');
        if (path == ".") path = "";

        return path;
    }

    /// <summary>
    /// Whether this is a root directory Harbora will accept, without touching the filesystem — so the
    /// panel can refuse a bad value at the point it is typed.
    ///
    /// <para>
    /// Containment is <see cref="PathGuard.ValidateArchiveEntry"/>'s job, not a second hand-rolled
    /// scan: it already refuses a rooted path, a drive-qualified one and any <c>..</c> segment, and
    /// resolves the result to confirm rather than trusting the string. Reusing it is what keeps the
    /// two from drifting apart.
    /// </para>
    ///
    /// <para>
    /// The rootedness of the value the customer actually typed is checked <b>before</b>
    /// <see cref="Normalise"/> ever runs on it, not after. <c>Normalise</c> trims a <i>surrounding</i>
    /// slash on purpose — "api/" is a harmless way to type "api" — but that same trim turns a genuinely
    /// absolute path like <c>/etc/passwd</c> into the ordinary-looking relative name
    /// <c>etc/passwd</c>, which <c>PathGuard</c> would then wave through: not a refusal, a silent
    /// reinterpretation of a different path than the one that was typed. An absolute path has to be
    /// refused for being absolute — never quietly re-read as its own relative shadow.
    /// </para>
    /// </summary>
    public static PathRejection Validate(string? value)
    {
        var raw = (value ?? "").Trim().Replace('\\', '/');
        if (raw.Length > 0 && (raw[0] == '/' || (raw.Length >= 2 && raw[1] == ':')))
            return PathRejection.Rooted;

        var path = Normalise(value);
        if (path.Length == 0) return PathRejection.None;

        var check = PathGuard.ValidateArchiveEntry(ProbeRoot, path);
        return check.Allowed ? PathRejection.None : check.Rejection;
    }

    /// <summary>
    /// An absolute path that is never created or written to. <see cref="PathGuard.ResolveWithin"/>
    /// only does path arithmetic — it never touches the disk — so any absolute root gives the same
    /// answer, and a fixed one keeps <see cref="Validate"/> a pure function of its argument.
    /// </summary>
    private static readonly string ProbeRoot =
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "harbora-root-directory-probe"));

    /// <summary>
    /// Why a root directory was refused, naming the value and what was wrong with it. Never
    /// "invalid path": the customer has to be able to tell a typo from a rule.
    /// </summary>
    public static string Explain(string? value, PathRejection why)
    {
        var shown = string.IsNullOrWhiteSpace(value) ? "(empty)" : value.Trim();
        return why switch
        {
            PathRejection.None => $"root directory '{shown}' is valid.",
            PathRejection.ParentTraversal =>
                $"root directory '{shown}' points outside the repository with '..'. It must name a " +
                "folder inside it, like 'api' or 'services/worker'.",
            PathRejection.Rooted =>
                $"root directory '{shown}' is an absolute path. It must be relative to the repository " +
                "root, like 'api' or 'services/worker'.",
            PathRejection.EscapesRoot =>
                $"root directory '{shown}' resolves outside the repository. It must name a folder " +
                "inside it, like 'api' or 'services/worker'.",
            PathRejection.InvalidCharacter =>
                $"root directory '{shown}' contains a character that cannot appear in a path.",
            PathRejection.Empty =>
                "root directory is empty. Leave it blank to build from the repository root.",
            _ => $"root directory '{shown}' was refused ({why})."
        };
    }

    /// <summary>
    /// Resolves the root directory against a source tree that is on disk, refusing by name both a
    /// path that breaks the rules and one that simply is not there.
    ///
    /// <para>
    /// The missing-directory case is the important one. The pipeline used to fall back to the source
    /// root whenever the sub-path did not exist, so a typo in the root directory built the wrong
    /// tree and reported success — this codebase's defining defect class. It is now an error that
    /// names the directory that was wanted and lists the ones that are actually there.
    /// </para>
    /// </summary>
    /// <param name="sourceRoot">The unpacked repository or upload.</param>
    /// <param name="value">The app's stored root directory.</param>
    /// <param name="resolved">The directory the build should run from, when this returns true.</param>
    /// <param name="error">A full sentence naming what was wrong, when this returns false.</param>
    public static bool TryResolve(
        string sourceRoot, string? value, out string resolved, out string? error)
    {
        resolved = sourceRoot;
        error = null;

        var rejection = Validate(value);
        if (rejection != PathRejection.None)
        {
            error = Explain(value, rejection);
            return false;
        }

        var path = Normalise(value);
        if (path.Length == 0) return true;

        var candidate = Path.GetFullPath(Path.Combine(sourceRoot, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!Directory.Exists(candidate))
        {
            error =
                $"root directory '{path}' does not exist in this {Describe(sourceRoot)}. " +
                Available(sourceRoot);
            return false;
        }

        resolved = candidate;
        return true;
    }

    private static string Describe(string sourceRoot) =>
        Directory.Exists(sourceRoot) ? "source tree" : "source tree (which is itself missing)";

    /// <summary>
    /// Names the folders that <i>are</i> at the top of the source tree, so a wrong root directory can
    /// be corrected from the message alone rather than by guessing.
    /// </summary>
    private static string Available(string sourceRoot)
    {
        if (!Directory.Exists(sourceRoot)) return "The source tree could not be read.";

        string[] names;
        try
        {
            names = Directory.EnumerateDirectories(sourceRoot)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToArray()!;
        }
        catch (IOException) { return "The source tree could not be listed."; }
        catch (UnauthorizedAccessException) { return "The source tree could not be listed."; }

        return names.Length == 0
            ? "It contains no sub-directories at all."
            : $"Top-level directories present: {string.Join(", ", names)}.";
    }
}
