using Harbora.Modules.Backup.Domain;

namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>
/// Builds Kopia argument lists.
///
/// <para>
/// Separated from the engine and kept pure so the property that matters — a hostile value stays ONE
/// argument and never becomes syntax — can be asserted directly in tests rather than inferred from
/// a process that would have to run.
/// </para>
///
/// <para><b>Version.</b> These flags are written against the Kopia release pinned in
/// <c>deploy/backup-sync.compose.yml</c>. Kopia's CLI is stable but not frozen, and a flag that was
/// renamed produces a non-zero exit with a message nobody expects. Before enabling
/// <c>Features:Backup</c> against a different Kopia build, run the repository-create and
/// snapshot-create paths once against it — that is what
/// <c>docs/backup-sync/MERGE_GUIDE.md</c> lists as a pre-enable step.</para>
/// </summary>
public static class KopiaCommands
{
    /// <summary>
    /// Environment variable Kopia reads the repository password from.
    ///
    /// <para>
    /// The password is passed this way and never as <c>--password=…</c>. A command line is readable
    /// by any local user through <c>/proc/&lt;pid&gt;/cmdline</c>, which would make the repository
    /// password visible to every process on the host (THREAT_MODEL T3).
    /// </para>
    /// </summary>
    public const string PasswordVariable = "KOPIA_PASSWORD";

    /// <summary>
    /// Flags that precede every subcommand: an explicit config file and cache directory.
    ///
    /// <para>
    /// Given explicitly rather than relying on Kopia's defaults, which live under the invoking
    /// user's HOME. Two operations against different repositories would otherwise share one config
    /// file, and "which repository is currently connected" would become a race.
    /// </para>
    /// </summary>
    public static List<string> Global(KopiaOptions options, Guid repositoryId)
    {
        ArgumentNullException.ThrowIfNull(options);

        return
        [
            $"--config-file={ConfigFileFor(options, repositoryId)}",
            $"--cache-directory={options.CacheDirectory}",
            // Kopia is being driven by a program, not a terminal. Progress bars and colour codes are
            // noise in a captured stream and make output harder to parse.
            "--no-progress"
        ];
    }

    /// <summary>One config file per repository, named by its id — which is a Guid, so never hostile.</summary>
    public static string ConfigFileFor(KopiaOptions options, Guid repositoryId) =>
        Path.Combine(options.ConfigDirectory, $"{repositoryId:N}.config");

    /// <summary>
    /// Create a filesystem repository.
    ///
    /// <para>
    /// Filesystem is the only backend wired to Kopia in this branch. Object-storage backends take
    /// their access key and secret as command-line flags, which is precisely the disclosure T3
    /// forbids — so S3-family repositories use the native engine, whose credential handling is
    /// already encrypted end to end. Resolving that is listed as follow-up work in the merge guide.
    /// </para>
    /// </summary>
    public static List<string> CreateFilesystemRepository(
        KopiaOptions options, Guid repositoryId, string path)
    {
        var safePath = RequireAbsolutePath(path);

        return
        [
            .. Global(options, repositoryId),
            "repository", "create", "filesystem",
            // "--flag=value" form, so a value that begins with "-" cannot be read as the next flag.
            $"--path={safePath}"
        ];
    }

    /// <summary>Connect to a filesystem repository that already exists.</summary>
    public static List<string> ConnectFilesystemRepository(
        KopiaOptions options, Guid repositoryId, string path)
    {
        var safePath = RequireAbsolutePath(path);

        return
        [
            .. Global(options, repositoryId),
            "repository", "connect", "filesystem",
            $"--path={safePath}"
        ];
    }

    /// <summary>Repository status, as JSON, for the health check.</summary>
    public static List<string> RepositoryStatus(KopiaOptions options, Guid repositoryId) =>
    [
        .. Global(options, repositoryId),
        "repository", "status", "--json"
    ];

    /// <summary>
    /// Snapshot a directory.
    ///
    /// <para>
    /// The source path is placed after <c>--</c> so a path that begins with a hyphen is read as a
    /// path rather than as a flag. Paths come from repository configuration and volume names, both
    /// of which are validated, but the separator costs nothing and removes the question.
    /// </para>
    /// </summary>
    public static List<string> CreateSnapshot(
        KopiaOptions options,
        Guid repositoryId,
        string sourcePath,
        IReadOnlyDictionary<string, string>? tags = null)
    {
        var safeSource = RequireAbsolutePath(sourcePath);

        List<string> arguments =
        [
            .. Global(options, repositoryId),
            "snapshot", "create", "--json"
        ];

        foreach (var (key, value) in tags ?? new Dictionary<string, string>())
        {
            // Tag keys and values reach the engine, so they are held to the same allowlist as every
            // other name. A tag is metadata, not a place to smuggle syntax.
            EngineArgumentGuard.Require(key, EngineArgumentGuard.IsSafeSnapshotId, "Tag name");
            EngineArgumentGuard.Require(value, EngineArgumentGuard.IsSafeName, "Tag value");
            arguments.Add($"--tags={key}:{value}");
        }

        arguments.Add("--");
        arguments.Add(safeSource);
        return arguments;
    }

    /// <summary>List snapshots as JSON. <paramref name="sourcePath"/> null lists every source.</summary>
    public static List<string> ListSnapshots(KopiaOptions options, Guid repositoryId, string? sourcePath)
    {
        List<string> arguments =
        [
            .. Global(options, repositoryId),
            "snapshot", "list", "--json"
        ];

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            arguments.Add("--all");
            return arguments;
        }

        arguments.Add("--");
        arguments.Add(RequireAbsolutePath(sourcePath));
        return arguments;
    }

    /// <summary>Restore a snapshot, or an object inside it, into a directory.</summary>
    public static List<string> Restore(
        KopiaOptions options, Guid repositoryId, string engineSnapshotId, string destinationPath)
    {
        var safeSnapshot = EngineArgumentGuard.Require(
            engineSnapshotId, EngineArgumentGuard.IsSafeSnapshotId, "Snapshot id");
        var safeDestination = RequireAbsolutePath(destinationPath);

        return
        [
            .. Global(options, repositoryId),
            "restore",
            "--",
            safeSnapshot,
            safeDestination
        ];
    }

    /// <summary>List one directory level inside a snapshot, as JSON, for the restore browser.</summary>
    public static List<string> BrowseSnapshot(
        KopiaOptions options, Guid repositoryId, string engineSnapshotId, string relativePath)
    {
        var safeSnapshot = EngineArgumentGuard.Require(
            engineSnapshotId, EngineArgumentGuard.IsSafeSnapshotId, "Snapshot id");

        // An object reference is "<snapshot-id>/<path inside it>". The relative part is validated
        // as an archive entry first, so "../" cannot walk out of the snapshot being browsed.
        var target = safeSnapshot;
        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            var check = PathGuard.ValidateArchiveEntry("/snapshot-root", relativePath);
            if (!check.Allowed)
                throw new ArgumentException(
                    $"That path is not valid inside a snapshot ({check.Rejection}).", nameof(relativePath));

            target = $"{safeSnapshot}/{relativePath.Replace('\\', '/').Trim('/')}";
        }

        return
        [
            .. Global(options, repositoryId),
            "ls", "--json", "--", target
        ];
    }

    /// <summary>Delete one snapshot.</summary>
    public static List<string> DeleteSnapshot(KopiaOptions options, Guid repositoryId, string engineSnapshotId)
    {
        var safeSnapshot = EngineArgumentGuard.Require(
            engineSnapshotId, EngineArgumentGuard.IsSafeSnapshotId, "Snapshot id");

        return
        [
            .. Global(options, repositoryId),
            "snapshot", "delete", "--delete", "--", safeSnapshot
        ];
    }

    /// <summary>
    /// Reclaim space no snapshot references any more.
    ///
    /// <para>
    /// Kept as a separate maintenance command rather than folded into delete: garbage collection
    /// rewrites repository structure, and it should run on a schedule under a lock, not as a
    /// side-effect of someone removing one snapshot.
    /// </para>
    /// </summary>
    public static List<string> Maintenance(KopiaOptions options, Guid repositoryId) =>
    [
        .. Global(options, repositoryId),
        "maintenance", "run", "--full"
    ];

    /// <summary>
    /// Paths handed to the engine must be absolute and free of traversal.
    ///
    /// <para>
    /// A relative path would be resolved against the panel process's working directory, which is not
    /// a location any caller intended and is not the same in a container as on a host.
    /// </para>
    /// </summary>
    private static string RequireAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A path is required.", nameof(path));

        if (path.Contains('\0'))
            throw new ArgumentException("A path may not contain a null byte.", nameof(path));

        if (!Path.IsPathRooted(path))
            throw new ArgumentException($"'{path}' must be an absolute path.", nameof(path));

        var full = Path.GetFullPath(path);

        // GetFullPath already collapses "..", so a surviving segment means something pathological.
        if (full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s == ".."))
            throw new ArgumentException($"'{path}' contains a parent-directory segment.", nameof(path));

        return full;
    }
}
