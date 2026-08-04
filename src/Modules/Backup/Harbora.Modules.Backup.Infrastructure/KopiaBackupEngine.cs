using System.Text.Json;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>
/// Kopia adapter: content-addressed, deduplicated storage with real snapshot history.
///
/// <para>
/// Driven through the CLI rather than <c>kopia server</c>. The server is built around a long-lived
/// process holding an unlocked repository, which would keep the repository password resident in the
/// panel for its whole uptime and add a second authenticated control surface to protect. A CLI
/// invocation is short-lived, takes its password out-of-band, and re-opens the repository each time.
/// The adapter depends on <see cref="IEngineProcessRunner"/>, so an API-server implementation can
/// replace process execution without this class changing.
/// </para>
/// </summary>
public sealed class KopiaBackupEngine(
    IEngineProcessRunner runner,
    IRepositoryCredentialReader credentials,
    IOptions<KopiaOptions> options,
    ILogger<KopiaBackupEngine> logger) : IBackupEngine
{
    private readonly KopiaOptions _options = options.Value;

    public BackupEngineKind Kind => BackupEngineKind.Kopia;

    public async Task<BackupRepositoryResult> CreateRepositoryAsync(
        CreateBackupRepositoryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Type is not BackupRepositoryType.Local)
            return new BackupRepositoryResult(false, request.RepositoryId, false, null,
                "Kopia repositories are currently limited to local paths. Object-storage backends " +
                "take their credentials as command-line arguments, which would expose them to every " +
                "process on the host — use the built-in engine for S3-compatible destinations.");

        if (string.IsNullOrWhiteSpace(request.LocalPath))
            return new BackupRepositoryResult(false, request.RepositoryId, false, null,
                "A local repository needs a path.");

        Directory.CreateDirectory(_options.ConfigDirectory);
        Directory.CreateDirectory(_options.CacheDirectory);
        Directory.CreateDirectory(request.LocalPath);

        var environment = Password(request.Password);

        // Connect first. Creating a repository that already exists would orphan every snapshot in
        // it, so "already there" must be an ordinary outcome rather than something to force past.
        var connect = await runner.RunAsync(new EngineCommand(
            _options.BinaryPath,
            KopiaCommands.ConnectFilesystemRepository(_options, request.RepositoryId, request.LocalPath),
            environment,
            Timeout: _options.MetadataCommandTimeout), cancellationToken);

        if (connect.Succeeded)
        {
            logger.LogInformation("Connected to existing Kopia repository {RepositoryId}.", request.RepositoryId);
            return new BackupRepositoryResult(true, request.RepositoryId, AlreadyExisted: true,
                EngineRepositoryId: request.RepositoryId.ToString("N"));
        }

        var create = await runner.RunAsync(new EngineCommand(
            _options.BinaryPath,
            KopiaCommands.CreateFilesystemRepository(_options, request.RepositoryId, request.LocalPath),
            environment,
            Timeout: _options.MetadataCommandTimeout), cancellationToken);

        if (!create.Succeeded)
            return new BackupRepositoryResult(false, request.RepositoryId, false, null,
                $"The repository could not be created. {create.Diagnostic}");

        return new BackupRepositoryResult(true, request.RepositoryId, AlreadyExisted: false,
            EngineRepositoryId: request.RepositoryId.ToString("N"));
    }

    public async Task<BackupSnapshotResult> CreateSnapshotAsync(
        CreateBackupSnapshotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Directory.Exists(request.SourcePath))
            return new BackupSnapshotResult(false, request.SnapshotId,
                Error: $"There is nothing to back up at {request.SourcePath}.");

        var result = await runner.RunAsync(new EngineCommand(
            _options.BinaryPath,
            KopiaCommands.CreateSnapshot(_options, request.RepositoryId, request.SourcePath, request.Tags),
            Password(request.Password),
            Timeout: _options.CommandTimeout), cancellationToken);

        if (!result.Succeeded)
            return new BackupSnapshotResult(false, request.SnapshotId,
                Error: $"The snapshot failed. {result.Diagnostic}");

        var manifest = KopiaOutput.ReadSnapshotManifest(result.StandardOutput);
        if (manifest.SnapshotId is null)
            return new BackupSnapshotResult(false, request.SnapshotId,
                Error: "The snapshot reported success but the engine returned no snapshot id, so " +
                       "there is nothing that could later be restored.");

        return new BackupSnapshotResult(
            true,
            request.SnapshotId,
            manifest.SnapshotId,
            manifest.OriginalSizeBytes,
            manifest.StoredSizeBytes,
            manifest.DeduplicatedSizeBytes,
            manifest.FilesCount,
            Warnings: manifest.Warnings);
    }

    public async Task<RestoreResult> RestoreAsync(
        RestoreBackupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The destination is confined by the caller before it gets here; re-checked because this is
        // the last point before bytes are written and the check is cheap.
        if (string.IsNullOrWhiteSpace(request.DestinationPath))
            return new RestoreResult(false, Error: "A restore needs a destination.");

        Directory.CreateDirectory(request.DestinationPath);

        // Refusing to write into a non-empty directory is what makes Fail actually mean fail. Kopia
        // would otherwise merge its output into whatever is already there.
        if (request.ConflictStrategy is RestoreConflictStrategy.Fail
            && Directory.EnumerateFileSystemEntries(request.DestinationPath).Any())
        {
            return new RestoreResult(false, Error:
                "The destination already contains files and the conflict strategy is 'Fail'. " +
                "Nothing was changed. Choose another destination, or an explicit overwrite strategy.");
        }

        var result = await runner.RunAsync(new EngineCommand(
            _options.BinaryPath,
            KopiaCommands.Restore(_options, request.RepositoryId, request.EngineSnapshotId, request.DestinationPath),
            Password(request.Password),
            Timeout: _options.CommandTimeout), cancellationToken);

        if (!result.Succeeded)
            return new RestoreResult(false, Error: $"The restore failed. {result.Diagnostic}");

        var (files, bytes) = MeasureRestored(request.DestinationPath);
        return new RestoreResult(true, files, bytes, request.DestinationPath);
    }

    public async Task<BackupRepositoryHealthResult> CheckHealthAsync(
        Guid repositoryId, CancellationToken cancellationToken)
    {
        var password = await credentials.GetPasswordAsync(repositoryId, cancellationToken);
        if (password is null)
            return new BackupRepositoryHealthResult(false, false,
                Error: "The repository password could not be decrypted. The master key may have changed.",
                CheckedAt: DateTimeOffset.UtcNow);

        var result = await runner.RunAsync(new EngineCommand(
            _options.BinaryPath,
            KopiaCommands.RepositoryStatus(_options, repositoryId),
            Password(password),
            Timeout: _options.MetadataCommandTimeout), cancellationToken);

        if (!result.Succeeded)
            return new BackupRepositoryHealthResult(false, false,
                Error: result.Diagnostic, CheckedAt: DateTimeOffset.UtcNow);

        return new BackupRepositoryHealthResult(true, true, CheckedAt: DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<EngineSnapshot>> ListSnapshotsAsync(
        ListSnapshotsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await runner.RunAsync(new EngineCommand(
            _options.BinaryPath,
            KopiaCommands.ListSnapshots(_options, request.RepositoryId, request.TargetRef),
            Password(request.Password),
            Timeout: _options.MetadataCommandTimeout), cancellationToken);

        if (!result.Succeeded)
            throw new InvalidOperationException($"The snapshot list could not be read. {result.Diagnostic}");

        return KopiaOutput.ReadSnapshotList(result.StandardOutput);
    }

    public async Task<IReadOnlyList<EngineEntry>> BrowseSnapshotAsync(
        BrowseSnapshotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await runner.RunAsync(new EngineCommand(
            _options.BinaryPath,
            KopiaCommands.BrowseSnapshot(_options, request.RepositoryId, request.EngineSnapshotId, request.RelativePath),
            Password(request.Password),
            Timeout: _options.MetadataCommandTimeout), cancellationToken);

        if (!result.Succeeded)
            throw new InvalidOperationException($"That snapshot could not be listed. {result.Diagnostic}");

        return KopiaOutput.ReadDirectoryListing(result.StandardOutput, request.RelativePath);
    }

    public async Task<EngineOperationResult> DeleteSnapshotAsync(
        DeleteSnapshotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await runner.RunAsync(new EngineCommand(
            _options.BinaryPath,
            KopiaCommands.DeleteSnapshot(_options, request.RepositoryId, request.EngineSnapshotId),
            Password(request.Password),
            Timeout: _options.MetadataCommandTimeout), cancellationToken);

        return result.Succeeded
            ? new EngineOperationResult(true)
            : new EngineOperationResult(false, result.Diagnostic);
    }

    /// <summary>The password reaches the engine only here, and only through the environment.</summary>
    private static Dictionary<string, string> Password(string password) =>
        new() { [KopiaCommands.PasswordVariable] = password };

    private static (long Files, long Bytes) MeasureRestored(string destination)
    {
        try
        {
            long files = 0, bytes = 0;
            foreach (var file in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories))
            {
                files++;
                bytes += new FileInfo(file).Length;
            }
            return (files, bytes);
        }
        catch (Exception)
        {
            // Counting is reporting, not correctness. A restore that worked is not failed because
            // the directory could not be walked afterwards.
            return (0, 0);
        }
    }
}

/// <summary>
/// Reads Kopia's JSON output.
///
/// <para>
/// Deliberately forgiving. The snapshot id is required — without it there is nothing that could be
/// restored later, so its absence is a real failure. Everything else is statistics: a field Kopia
/// renamed between releases should show as "unknown size" on a screen, not turn a successful backup
/// into a failed one.
/// </para>
/// </summary>
internal static class KopiaOutput
{
    internal sealed record SnapshotManifest(
        string? SnapshotId,
        long OriginalSizeBytes,
        long StoredSizeBytes,
        long DeduplicatedSizeBytes,
        long FilesCount,
        IReadOnlyList<string>? Warnings);

    internal static SnapshotManifest ReadSnapshotManifest(string json)
    {
        var root = TryParse(json);
        if (root is null) return new SnapshotManifest(null, 0, 0, 0, 0, null);

        var element = root.Value;
        if (element.ValueKind == JsonValueKind.Array)
            element = element.EnumerateArray().FirstOrDefault();

        var id = ReadString(element, "id") ?? ReadString(element, "snapshotID");
        var stats = TryGet(element, "stats");

        return new SnapshotManifest(
            id,
            ReadLong(stats, "totalSize", "totalFileSize", "bytes"),
            ReadLong(stats, "packedSize", "storedSize", "uploadedBytes"),
            ReadLong(stats, "dedupedSize", "cachedBytes", "excludedTotalSize"),
            ReadLong(stats, "fileCount", "totalFileCount", "files"),
            ReadWarnings(stats));
    }

    internal static IReadOnlyList<EngineSnapshot> ReadSnapshotList(string json)
    {
        var root = TryParse(json);
        if (root is null || root.Value.ValueKind != JsonValueKind.Array) return [];

        var snapshots = new List<EngineSnapshot>();
        foreach (var item in root.Value.EnumerateArray())
        {
            var id = ReadString(item, "id") ?? ReadString(item, "snapshotID");
            if (id is null) continue;

            var stats = TryGet(item, "stats");
            snapshots.Add(new EngineSnapshot(
                id,
                ReadDate(item, "startTime", "endTime") ?? DateTimeOffset.MinValue,
                ReadString(TryGet(item, "source"), "path") ?? "",
                ReadLong(stats, "totalSize", "totalFileSize"),
                ReadLong(stats, "fileCount", "totalFileCount")));
        }

        return snapshots.OrderByDescending(s => s.CreatedAt).ToList();
    }

    internal static IReadOnlyList<EngineEntry> ReadDirectoryListing(string json, string parentPath)
    {
        var root = TryParse(json);
        if (root is null || root.Value.ValueKind != JsonValueKind.Array) return [];

        var entries = new List<EngineEntry>();
        foreach (var item in root.Value.EnumerateArray())
        {
            var name = ReadString(item, "name");
            if (name is null) continue;

            // Kopia marks directories with a mode string beginning 'd', the same convention ls uses.
            var type = ReadString(item, "type") ?? ReadString(item, "mode") ?? "";
            var isDirectory = type.StartsWith('d');

            entries.Add(new EngineEntry(
                name,
                string.IsNullOrEmpty(parentPath) ? name : $"{parentPath.TrimEnd('/')}/{name}",
                isDirectory,
                ReadLong(item, "size"),
                ReadDate(item, "mtime", "modTime") ?? DateTimeOffset.MinValue));
        }

        // Directories first, then names — the order a file browser is expected to show.
        return entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static JsonElement? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? TryGet(JsonElement? element, string property) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(property, out var found)
            ? found
            : null;

    private static string? ReadString(JsonElement? element, string property) =>
        TryGet(element, property) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static long ReadLong(JsonElement? element, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (TryGet(element, property) is { ValueKind: JsonValueKind.Number } value
                && value.TryGetInt64(out var number))
                return number;
        }
        return 0;
    }

    private static DateTimeOffset? ReadDate(JsonElement? element, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (ReadString(element, property) is { } text
                && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                return parsed;
        }
        return null;
    }

    private static IReadOnlyList<string>? ReadWarnings(JsonElement? stats)
    {
        var count = ReadLong(stats, "errorCount", "ignoredErrorCount");
        return count > 0
            ? [$"{count} file(s) could not be read and were skipped."]
            : null;
    }
}
