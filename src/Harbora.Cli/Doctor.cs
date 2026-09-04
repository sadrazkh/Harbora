using System.Text.Json;
using System.Text.RegularExpressions;

namespace Harbora.Cli;

/// <summary>
/// The checks behind <c>harbora doctor</c>, and the ones <c>harbora deploy</c> now runs on itself
/// before uploading anything.
///
/// <para>
/// This exists because of a real, avoidable incident: DriveUnion's own deploy failed twice, and the
/// owner could not have worked out why — the upload reported "Unpacked 215 entries" with no sign 130
/// files were missing, the build was reported healthy while it was failing inside the image, and the
/// error the owner actually saw ("No such image") named the wrong thing entirely. All three of those
/// are fixed elsewhere (<see cref="SourcePacker"/>'s exclusion reporting, the Docker build-stream
/// check, the exit-code fix). This class is the preflight that would have caught the *cause* — a file
/// the build needs, sitting in a folder the packer treats as build output — before any of that ran.
/// </para>
///
/// <para>
/// Every check says what it looked at and what it concluded, never just "OK" for something it did not
/// really verify — a doctor that fakes confidence is this codebase's defining defect class, the same
/// one <see cref="SourcePacker"/>'s old exclusion list was.
/// </para>
/// </summary>
public static class Doctor
{
    public enum Level { Ok, Warn, Fail }

    /// <param name="Name">Which check this is, shown as a heading in the report.</param>
    /// <param name="Level">Whether it passed, is worth a look, or would break the deploy.</param>
    /// <param name="Detail">What was looked at and what was concluded — always a full sentence.</param>
    public sealed record Check(string Name, Level Level, string Detail);

    private static readonly Regex ScriptPathPattern =
        new(@"[A-Za-z0-9_./-]+\.(?:mjs|cjs|js|jsx|ts|tsx|py|rb|sh)\b", RegexOptions.Compiled);

    // Matches docs/cli-deploy.md §5 "How the build is chosen": Node, .NET, Go, PHP, Python, static.
    private static readonly (string Stack, string[] Markers)[] StackMarkers =
    [
        ("Node (package.json)", ["package.json"]),
        ("PHP (composer.json/index.php)", ["composer.json", "index.php"]),
        ("Python (requirements.txt/pyproject.toml/Pipfile)", ["requirements.txt", "pyproject.toml", "Pipfile"]),
        ("static (index.html)", ["index.html"])
    ];

    // ---- manifest -----------------------------------------------------------------------------

    /// <summary>
    /// <c>harbora.yml</c>: does it name an app to deploy. Reused by both the standalone command
    /// (against <paramref name="config"/>'s own <c>app:</c>) and <c>deploy</c> (against whatever it
    /// already resolved from flags/config/prompt), so a name given on the command line never trips
    /// this even when the file itself has none.
    /// </summary>
    public static Check CheckManifest(ProjectConfig config, string? resolvedApp)
    {
        var app = string.IsNullOrWhiteSpace(resolvedApp) ? config.App : resolvedApp;
        if (string.IsNullOrWhiteSpace(app))
            return new("harbora.yml", Level.Fail,
                "no app: in harbora.yml and none given on the command line — deploy fails with " +
                "\"No app specified\". Run `harbora init`, or pass the slug: `harbora deploy <slug>`.");

        var server = string.IsNullOrWhiteSpace(config.Server)
            ? "not set — uses whichever server you last logged into, or --server"
            : config.Server;
        return new("harbora.yml", Level.Ok, $"app: {app}; server: {server}");
    }

    // ---- build: Dockerfile / context / COPY sources / $PORT / stack detection ----------------

    /// <summary>
    /// Everything about the build that can be checked from local files alone: the context exists,
    /// the Dockerfile exists (or the project auto-detects), every <c>COPY</c> source it names exists
    /// under the context, and the Dockerfile honours <c>$PORT</c>.
    /// </summary>
    /// <returns>
    /// The checks, and the project-root-relative paths the Dockerfile's <c>COPY</c> lines named —
    /// handed to <see cref="CheckUploadAsync"/> so it can cross-reference them against what the
    /// packer would actually exclude.
    /// </returns>
    public static (List<Check> Checks, List<string> ReferencedPaths) CheckBuild(string projectDir, ProjectConfig config)
    {
        var checks = new List<Check>();
        var referenced = new List<string>();

        if (!string.IsNullOrWhiteSpace(config.Image))
        {
            checks.Add(new("Build", Level.Ok, $"image: {config.Image} — releasing an existing image; nothing is built."));
            return (checks, referenced);
        }

        var contextRel = string.IsNullOrWhiteSpace(config.Context) ? "." : config.Context!;

        // Refused by name before anything is resolved against the local disk: a "../other" or an
        // absolute path is not a sub-directory of the project this manifest belongs to, whatever
        // happens to sit at that path on the machine running `harbora doctor`. Same rule the panel's
        // own root-directory field enforces (Harbora.Shared.AppRootDirectory) — one containment check,
        // not two that could drift apart.
        var rootRejection = Harbora.Shared.AppRootDirectory.Validate(contextRel);
        if (rootRejection != Harbora.Shared.PathRejection.None)
        {
            checks.Add(new("Build context", Level.Fail,
                Harbora.Shared.AppRootDirectory.Explain(contextRel, rootRejection)));
            return (checks, referenced);
        }

        var contextDir = Path.GetFullPath(Path.Combine(projectDir, contextRel));

        if (!Directory.Exists(contextDir))
        {
            checks.Add(new("Build context", Level.Fail,
                $"context '{contextRel}' does not exist under {projectDir} — nothing can be built from it."));
            return (checks, referenced);
        }

        // The context exists on disk, but that is not the same question as "would it exist in the
        // upload" — SourcePacker treats a handful of names (build, dist, target, vendor, .output) as
        // build output whenever they sit at the project root, exactly the DriveUnion incident this
        // preflight exists to catch one layer up. A root directory named after one of them would be
        // packed as zero files and silently build from nothing (or fall through to the wrong context).
        var topSegment = contextRel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } segs
            ? segs[0] : "";
        if (topSegment.Length > 0 && SourcePacker.RootOnlyExclude.Contains(topSegment, StringComparer.OrdinalIgnoreCase))
        {
            checks.Add(new("Build context", Level.Fail,
                $"root directory '{contextRel}' shares its name with a rule the uploader always excludes at " +
                $"the project root ('{topSegment}') — none of its files would reach the server. Rename the " +
                "folder, or move the app's source out of it."));
            return (checks, referenced);
        }
        checks.Add(new("Build context", Level.Ok, $"context '{contextRel}' resolves to {contextDir}."));

        if (config.DockerfileLines.Count > 0)
        {
            checks.Add(new("Dockerfile", Level.Ok,
                $"dockerfileLines: in harbora.yml defines the build inline ({config.DockerfileLines.Count} line(s)) — " +
                "no Dockerfile file is read."));
            CheckPort(string.Join('\n', config.DockerfileLines), checks);
            return (checks, referenced);
        }

        var dockerfileRel = string.IsNullOrWhiteSpace(config.Dockerfile) ? "Dockerfile" : config.Dockerfile!;
        var dockerfilePath = Path.Combine(contextDir, dockerfileRel);

        if (!File.Exists(dockerfilePath))
        {
            if (!string.IsNullOrWhiteSpace(config.Branch))
            {
                checks.Add(new("Dockerfile", Level.Ok,
                    $"no {dockerfileRel} in the working tree, but branch: {config.Branch} is set — the build reads " +
                    "the Dockerfile from that branch's committed content, which this check does not have."));
                return (checks, referenced);
            }

            var stack = DetectStack(contextDir);
            checks.Add(stack is null
                ? new Check("Dockerfile", Level.Fail,
                    $"no {dockerfileRel} in '{contextRel}', and no recognised stack marker (Node/.NET/Go/PHP/Python/" +
                    "static) — deploy fails with \"the stack couldn't be auto-detected\". Add a Dockerfile, or " +
                    "dockerfileLines: in harbora.yml.")
                : new Check("Dockerfile", Level.Ok,
                    $"no {dockerfileRel}; auto-detected as {stack} — Harbora generates a build and sets ENV PORT " +
                    "for you."));
            return (checks, referenced);
        }

        checks.Add(new("Dockerfile", Level.Ok,
            $"found at {Path.GetRelativePath(projectDir, dockerfilePath).Replace('\\', '/')}."));

        var lines = File.ReadAllLines(dockerfilePath);
        CheckCopySources(lines, projectDir, contextDir, contextRel, checks, referenced);
        CheckPort(string.Join('\n', lines), checks);

        return (checks, referenced);
    }

    /// <summary>
    /// A .NET project (a <c>*.csproj</c> anywhere under the context) is checked separately from the
    /// four exact-filename stacks: it is a glob, not a name, and unlike the others is not itself a
    /// reason to fall back to a Dockerfile — see <c>docs/cli-deploy.md</c>'s own worked example
    /// (DriveUnion) for why a .NET app with a front-end build step needs one anyway.
    /// </summary>
    private static string? DetectStack(string contextDir)
    {
        foreach (var (stack, markers) in StackMarkers)
            if (markers.Any(m => File.Exists(Path.Combine(contextDir, m))))
                return stack;

        if (Directory.EnumerateFiles(contextDir, "*.csproj", SearchOption.AllDirectories).Any())
            return ".NET (*.csproj)";

        if (File.Exists(Path.Combine(contextDir, "go.mod")))
            return "Go (go.mod)";

        return null;
    }

    private static void CheckPort(string dockerfileText, List<Check> checks)
    {
        var honoursPort =
            dockerfileText.Contains("$PORT", StringComparison.Ordinal) ||
            dockerfileText.Contains("${PORT", StringComparison.Ordinal) ||
            Regex.IsMatch(dockerfileText, @"(?im)^\s*ENV\s+PORT\b");

        checks.Add(honoursPort
            ? new Check("$PORT", Level.Ok, "the build references PORT — Harbora sets this env var for the container.")
            : new Check("$PORT", Level.Warn,
                "the build never references $PORT/${PORT}. Harbora sets PORT for the container to listen on; " +
                "if the app binds to a fixed port instead, the deploy succeeds and the app 502s — the panel's own " +
                "documented failure mode (docs/cli-deploy.md §8)."));
    }

    /// <summary>
    /// Parses every <c>COPY</c> line (skipping <c>--from=</c> multi-stage copies, which read from an
    /// earlier stage, not the local context) and checks each named source exists under
    /// <paramref name="contextDir"/>. Sources are collected as project-root-relative paths for the
    /// upload cross-check, whether or not they exist — a source that does not exist is reported here
    /// directly and is also, correctly, not "missing from the upload": it was never going to be there.
    /// </summary>
    private static void CheckCopySources(
        IReadOnlyList<string> dockerfileLines, string projectDir, string contextDir, string contextRel,
        List<Check> checks, List<string> referenced)
    {
        for (var i = 0; i < dockerfileLines.Count; i++)
        {
            var line = dockerfileLines[i].Trim();
            if (!line.StartsWith("COPY", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("--from=", StringComparison.OrdinalIgnoreCase)) continue;

            var tokens = ParseCopyArgs(line[4..].Trim());
            if (tokens.Count < 2) continue;   // malformed, or a form this parser does not cover — don't guess

            foreach (var source in tokens.Take(tokens.Count - 1))
            {
                if (source is "." or "./") continue;      // the whole context — covered by the upload check
                if (source.Contains('*')) continue;        // a glob; not resolved here

                var full = Path.GetFullPath(Path.Combine(contextDir, source));
                if (!File.Exists(full) && !Directory.Exists(full))
                {
                    checks.Add(new("Dockerfile COPY", Level.Fail,
                        $"line {i + 1}: `COPY {source}` — no such file or directory under the build context " +
                        $"('{contextRel}')."));
                    continue;
                }

                referenced.Add(Path.GetRelativePath(projectDir, full).Replace('\\', '/'));
            }
        }
    }

    private static List<string> ParseCopyArgs(string rest)
    {
        rest = rest.Trim();
        if (rest.StartsWith('['))
        {
            try { return JsonSerializer.Deserialize<List<string>>(rest) ?? []; }
            catch (JsonException) { return []; }
        }

        var tokens = new List<string>();
        foreach (Match m in Regex.Matches(rest, "\"[^\"]*\"|'[^']*'|\\S+"))
        {
            var token = m.Value.Trim('"', '\'');
            if (token.StartsWith("--", StringComparison.Ordinal)) continue;   // --chown=, --chmod=
            tokens.Add(token);
        }
        return tokens;
    }

    // ---- upload: what SourcePacker would actually pack and exclude ----------------------------

    /// <summary>
    /// Packs the project the same way <c>harbora deploy --push</c> would, reports what it would send
    /// and what it would drop, and cross-references the Dockerfile's <c>COPY</c> sources plus every
    /// <c>package.json</c> script against that exclusion list — the check that would have caught
    /// DriveUnion's incident before anything uploaded.
    /// </summary>
    /// <param name="referencedFromDockerfile">Root-relative paths <see cref="CheckBuild"/> found in COPY lines.</param>
    public static async Task<List<Check>> CheckUploadAsync(
        string projectDir, ProjectConfig config, IReadOnlyList<string> referencedFromDockerfile, CancellationToken ct)
    {
        var checks = new List<Check>();
        var packed = await SourcePacker.PackAsync(projectDir, config, ct);
        try
        {
            checks.Add(new("Upload", Level.Ok,
                $"a --push upload (or one from an app with no server-side Git remote) would pack {packed.Files} " +
                $"file(s) ({packed.Bytes / 1024.0 / 1024:0.#} MB)" +
                (packed.Excluded.Count == 0
                    ? "; nothing is excluded."
                    : $"; excludes {packed.Excluded.Count} file(s) — " + string.Join(", ",
                        packed.Excluded.GroupBy(e => e.Reason, StringComparer.Ordinal)
                            .OrderByDescending(g => g.Count())
                            .Select(g => $"{g.Count()} by {g.Key}")) + ".") +
                " Server-side Git pulls are unaffected — this rule set only applies to an uploaded archive."));

            var excludedByPath = packed.Excluded
                .GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Reason, StringComparer.OrdinalIgnoreCase);

            foreach (var path in referencedFromDockerfile.Distinct(StringComparer.OrdinalIgnoreCase))
                if (excludedByPath.TryGetValue(path, out var reason))
                    checks.Add(new("Upload / Dockerfile COPY", Level.Fail,
                        $"the Dockerfile COPYs {path}, but the upload would exclude it ({reason}) — the build " +
                        "will fail with that file missing, the way DriveUnion's did."));

            foreach (var (packageJson, script, path, reason) in FindExcludedScriptReferences(projectDir, excludedByPath))
                checks.Add(new("Upload / package.json", Level.Fail,
                    $"{packageJson} script '{script}' runs {path}, but the upload would exclude it ({reason}) — " +
                    "the exact DriveUnion regression: an ordinary source file inside a folder the packer, or your " +
                    "own ignore file, treats as build output."));
        }
        finally { try { File.Delete(packed.ArchivePath); } catch { /* temp file, best effort */ } }

        return checks;
    }

    /// <summary>
    /// Every <c>package.json</c> under the project (excluding <c>node_modules</c> and the like) has
    /// its <c>scripts</c> values scanned for path-like tokens — <c>node Scripts/build/copy-fonts.mjs</c>
    /// finds <c>Scripts/build/copy-fonts.mjs</c> — resolved relative to that <c>package.json</c>'s own
    /// directory (scripts run from there, not from the project root), then checked against what the
    /// packer would exclude.
    /// </summary>
    private static IEnumerable<(string PackageJson, string Script, string Path, string Reason)> FindExcludedScriptReferences(
        string projectDir, IReadOnlyDictionary<string, string> excludedByPath)
    {
        foreach (var pkgPath in Directory.EnumerateFiles(projectDir, "package.json", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(projectDir, pkgPath).Replace('\\', '/');
            if (rel.Split('/').Any(s => SourcePacker.AlwaysExclude.Contains(s, StringComparer.OrdinalIgnoreCase)))
                continue;   // node_modules etc. — not the project's own manifest

            JsonElement scripts;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(pkgPath));
                if (!doc.RootElement.TryGetProperty("scripts", out var found)) continue;
                scripts = found.Clone();
            }
            catch (JsonException) { continue; }   // a malformed package.json is a different problem to report

            var pkgDir = Path.GetDirectoryName(pkgPath)!;
            foreach (var script in scripts.EnumerateObject())
            {
                if (script.Value.ValueKind != JsonValueKind.String) continue;

                foreach (Match m in ScriptPathPattern.Matches(script.Value.GetString() ?? ""))
                {
                    var full = Path.GetFullPath(Path.Combine(pkgDir, m.Value));
                    var rootRelative = Path.GetRelativePath(projectDir, full).Replace('\\', '/');
                    if (excludedByPath.TryGetValue(rootRelative, out var reason))
                        yield return (rel, script.Name, rootRelative, reason);
                }
            }
        }
    }

    // ---- .env.local: gitignored? (4.1, 2026-09-04 local-dev-parity plan) ---------------------

    /// <summary>
    /// <c>harbora env pull</c> writes an app's real, decrypted secret values into <c>.env.local</c>.
    /// Warns when that file exists but nothing in <c>.gitignore</c> would stop <c>git add .</c> from
    /// picking it up — the entire point of the command is that a developer stops copying a credential
    /// by hand; writing it into a file that then gets committed to a shared remote would make things
    /// worse, not better.
    ///
    /// <para>
    /// Deliberately checks <c>.gitignore</c> alone, never <c>.dockerignore</c>: <c>.env.local</c> is
    /// already one of <see cref="SourcePacker.AlwaysExclude"/>'s built-in names, so it never reaches an
    /// uploaded archive regardless of either ignore file — this check is about the separate, real risk
    /// of a git commit picking it up, which nothing about the upload path protects against.
    /// </para>
    ///
    /// <para>Silent (no check at all) when <c>.env.local</c> does not exist — a project that has never
    /// run <c>env pull</c> has nothing here worth reporting on, and an "OK, nothing to check" line would
    /// only be noise next to every other check that verified something real.</para>
    /// </summary>
    public static List<Check> CheckEnvLocal(string projectDir)
    {
        var envLocalPath = Path.Combine(projectDir, ".env.local");
        if (!File.Exists(envLocalPath)) return [];

        var gitignorePath = Path.Combine(projectDir, ".gitignore");
        var patterns = File.Exists(gitignorePath)
            ? File.ReadAllLines(gitignorePath)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith('!'))
                .Select(l => l.Replace('\\', '/').Trim('/'))
                .Where(l => l.Length > 0)
                .ToList()
            : [];

        return SourcePacker.MatchingIgnorePattern(".env.local", patterns) is not null
            ? [new Check("Environment", Level.Ok, ".env.local exists and is excluded by .gitignore.")]
            : [new Check("Environment", Level.Warn,
                ".env.local exists but nothing in .gitignore excludes it — `harbora env pull` writes real, " +
                "decrypted secret values into that file, and an untracked-but-not-ignored file is one " +
                "`git add .` away from reaching a shared remote. Add `.env.local` to .gitignore.")];
    }

    // ---- auth: a session for this server, not expired --------------------------------------------

    /// <summary>
    /// Reuses <c>whoami</c> — the same endpoint <c>harbora status</c>/<c>whoami</c> already call —
    /// rather than inventing a second way to ask the server whether a token still works.
    /// </summary>
    public static async Task<Check> CheckAuthAsync(ApiClient? api, string? server, CancellationToken ct = default)
    {
        if (api is null)
            return new("Auth", Level.Fail,
                $"not logged in{(server is null ? "" : $" for {server}")} — run `harbora login`, or pass " +
                "--server/--token.");

        try
        {
            var me = await api.GetAsync("whoami", ct);
            var email = me.ValueKind == JsonValueKind.Object && me.TryGetProperty("email", out var e)
                ? e.GetString() : null;
            return new("Auth", Level.Ok, $"signed in as {email ?? "(unknown)"} on {api.Server}.");
        }
        catch (Exception ex)
        {
            var message = ServerError.Message(ex.Message);
            var expired = message.Contains("401", StringComparison.Ordinal) ||
                          message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
                          message.Contains("Invalid", StringComparison.OrdinalIgnoreCase);

            return expired
                ? new("Auth", Level.Fail, $"the stored session for {api.Server} was refused: {message} — run " +
                                           "`harbora login` again.")
                : new("Auth", Level.Warn, $"could not verify the session against {api.Server}: {message} — this " +
                                           "may be transient network trouble; not confirmed either way.");
        }
    }
}
