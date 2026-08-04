using System.Formats.Tar;
using System.IO.Compression;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Infrastructure.Backups;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// This file needs both Harbora.Application.Abstractions (IBackupStorage, ISecretProtector) and the
// module's own contracts, and each declares a type called IBackupEngine — the platform's
// target-oriented service, and this module's storage-engine port. They are different layers, not
// competitors (ARCHITECTURE.md § 2). Aliased rather than left to resolution order so a reader can
// see which one is meant without checking the using list.
using IBackupEngine = Harbora.Modules.Backup.Contracts.IBackupEngine;

namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>
/// Harbora's built-in engine behind the repository/snapshot port.
///
/// <para>
/// Reuses the pieces the platform already trusts — <c>ArchiveCipher</c> for AES-GCM with a key
/// derived from the master key, and <c>IBackupStorage</c> for local placement and S3 upload — rather
/// than reimplementing either. What is new here is only the shape: producing an artifact per
/// snapshot into a repository, instead of per backup into a destination.
/// </para>
/// <para>
/// The default engine, and the one every repository gets unless Kopia is explicitly chosen. It has
/// no deduplication and no repository-side history: for this format, Harbora's own
/// <c>BackupSnapshots</c> table IS the index, which is why <see cref="ListSnapshotsAsync"/> reads
/// from the database rather than from storage.
/// </para>
/// </summary>
public sealed class HarboraNativeBackupEngine(
    HarboraDbContext db,
    IBackupStorage storage,
    IRepositoryCredentialReader credentials,
    IRepositoryDestinationFactory destinations,
    ISecretProtector protector,
    IOptions<BackupModuleOptions> moduleOptions,
    ILogger<HarboraNativeBackupEngine> logger) : IBackupEngine
{
    private readonly BackupModuleOptions _options = moduleOptions.Value;

    /// <summary>Same purpose string the existing engine uses, so both read each other's archives.</summary>
    private const string ArchiveKeyPurpose = "backup-archive";

    public BackupEngineKind Kind => BackupEngineKind.Native;

    public async Task<BackupRepositoryResult> CreateRepositoryAsync(
        CreateBackupRepositoryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            if (request.Type is BackupRepositoryType.Local)
            {
                if (string.IsNullOrWhiteSpace(request.LocalPath))
                    return new BackupRepositoryResult(false, request.RepositoryId, false, null,
                        "A local repository needs a path.");

                var existed = Directory.Exists(request.LocalPath)
                              && Directory.EnumerateFileSystemEntries(request.LocalPath).Any();

                Directory.CreateDirectory(request.LocalPath);
                return new BackupRepositoryResult(true, request.RepositoryId, existed,
                    EngineRepositoryId: request.RepositoryId.ToString("N"));
            }

            // Remote repository: prove it is reachable and writable now, rather than discovering at
            // 3am that the bucket name was wrong. A repository that has never been written to is
            // indistinguishable from one that works, right up until the first backup fails.
            var probe = await WriteProbeAsync(request, cancellationToken);
            return probe;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Repository {RepositoryId} could not be created.", request.RepositoryId);
            return new BackupRepositoryResult(false, request.RepositoryId, false, null, ex.Message);
        }
    }

    public async Task<BackupSnapshotResult> CreateSnapshotAsync(
        CreateBackupSnapshotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Directory.Exists(request.SourcePath))
            return new BackupSnapshotResult(false, request.SnapshotId,
                Error: $"There is nothing to back up at {request.SourcePath}.");

        var repository = await LoadRepositoryAsync(request.RepositoryId, cancellationToken);
        if (repository is null)
            return new BackupSnapshotResult(false, request.SnapshotId, Error: "That repository no longer exists.");

        Directory.CreateDirectory(_options.StagingDirectory);

        // Named by snapshot id: a Guid, so the key can never carry anything hostile into a path or
        // an object name, and two snapshots can never collide.
        var key = $"{request.RepositoryId:N}/{request.SnapshotId:N}.tar.gz";
        var stagedPath = Path.Combine(_options.StagingDirectory, $"{request.SnapshotId:N}.tar.gz");
        var encryptedPath = stagedPath + ArchiveCipher.Extension;

        try
        {
            var originalBytes = await WriteArchiveAsync(request.SourcePath, stagedPath, cancellationToken);

            // Encrypted before it leaves staging: a volume archive holds raw application data, so
            // anyone who reaches the destination bucket otherwise reads it in the clear.
            await using (var plain = File.OpenRead(stagedPath))
            await using (var cipher = File.Create(encryptedPath))
                await ArchiveCipher.EncryptAsync(plain, cipher, protector.DeriveKey(ArchiveKeyPurpose), cancellationToken);

            var destination = destinations.ToDestination(repository, await CredentialsFor(repository, cancellationToken));
            var (_, storedBytes) = await storage.PutFileAsync(
                destination, key + ArchiveCipher.Extension, encryptedPath, cancellationToken);

            var files = Directory.EnumerateFiles(request.SourcePath, "*", SearchOption.AllDirectories).LongCount();

            return new BackupSnapshotResult(
                true,
                request.SnapshotId,
                EngineSnapshotId: request.SnapshotId.ToString("N"),
                OriginalSizeBytes: originalBytes,
                StoredSizeBytes: storedBytes,
                // No dedup in this format. Reported as zero rather than as a guess — a saving that
                // was never made should not appear on a dashboard.
                DeduplicatedSizeBytes: 0,
                FilesCount: files);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Snapshot {SnapshotId} failed.", request.SnapshotId);
            return new BackupSnapshotResult(false, request.SnapshotId, Error: ex.Message);
        }
        finally
        {
            // Staging copies are plaintext application data. They go whatever happened.
            Delete(stagedPath);
            Delete(encryptedPath);
        }
    }

    public async Task<RestoreResult> RestoreAsync(
        RestoreBackupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var repository = await LoadRepositoryAsync(request.RepositoryId, cancellationToken);
        if (repository is null) return new RestoreResult(false, Error: "That repository no longer exists.");

        if (!Guid.TryParseExact(request.EngineSnapshotId, "N", out var snapshotId))
            return new RestoreResult(false, Error: "That snapshot reference is not valid for this engine.");

        var key = $"{request.RepositoryId:N}/{snapshotId:N}.tar.gz{ArchiveCipher.Extension}";
        var destination = destinations.ToDestination(repository, await CredentialsFor(repository, cancellationToken));

        string? fetched = null, decrypted = null;
        try
        {
            fetched = await storage.GetToLocalAsync(destination, key, cancellationToken);
            decrypted = await DecryptAsync(fetched, cancellationToken);

            var extraction = await ExtractAsync(
                decrypted, request.DestinationPath, request.Entries, request.ConflictStrategy, cancellationToken);

            return extraction;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restore of snapshot {SnapshotId} failed.", snapshotId);
            return new RestoreResult(false, Error: ex.Message);
        }
        finally
        {
            // Never leave a decrypted copy on disk after the operation that needed it.
            if (decrypted is not null && !string.Equals(decrypted, fetched, StringComparison.Ordinal))
                Delete(decrypted);
        }
    }

    public async Task<BackupRepositoryHealthResult> CheckHealthAsync(
        Guid repositoryId, CancellationToken cancellationToken)
    {
        var repository = await LoadRepositoryAsync(repositoryId, cancellationToken);
        if (repository is null)
            return new BackupRepositoryHealthResult(false, false,
                Error: "That repository no longer exists.", CheckedAt: DateTimeOffset.UtcNow);

        try
        {
            if (repository.Type is BackupRepositoryType.Local)
            {
                var path = repository.BasePath ?? "";
                var reachable = Directory.Exists(path);
                return new BackupRepositoryHealthResult(
                    reachable, reachable,
                    Error: reachable ? null : $"The directory {path} is not there.",
                    CheckedAt: DateTimeOffset.UtcNow);
            }

            var probe = await WriteProbeAsync(new CreateBackupRepositoryRequest(
                repository.Id, repository.Name, repository.Type, "",
                repository.BasePath, repository.Endpoint, repository.Bucket, repository.Region,
                repository.BasePath, await CredentialsFor(repository, cancellationToken)), cancellationToken);

            return new BackupRepositoryHealthResult(
                probe.Succeeded, probe.Succeeded, Error: probe.Error, CheckedAt: DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return new BackupRepositoryHealthResult(false, false,
                Error: ex.Message, CheckedAt: DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Reads from Harbora's own snapshot table.
    ///
    /// <para>
    /// Unlike Kopia, this format keeps no index inside the repository — the artifacts are opaque
    /// files with Guid names. The database row is therefore the authoritative record of what a
    /// repository contains, and asking storage would only tell us which files exist, not what they
    /// were taken from.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<EngineSnapshot>> ListSnapshotsAsync(
        ListSnapshotsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var query = db.BackupSnapshots.IgnoreQueryFilters()
            .Where(s => s.RepositoryId == request.RepositoryId
                        && s.EngineSnapshotId != null
                        && (s.Status == BackupSnapshotStatus.Completed
                            || s.Status == BackupSnapshotStatus.CompletedWithWarnings));

        if (!string.IsNullOrWhiteSpace(request.TargetRef))
            query = query.Where(s => s.TargetRef == request.TargetRef);

        var rows = await query.OrderByDescending(s => s.CreatedAt).ToListAsync(cancellationToken);

        return rows.Select(s => new EngineSnapshot(
            s.EngineSnapshotId!,
            s.CompletedAt ?? s.CreatedAt,
            s.TargetRef,
            s.OriginalSizeBytes,
            s.FilesCount)).ToList();
    }

    public async Task<IReadOnlyList<EngineEntry>> BrowseSnapshotAsync(
        BrowseSnapshotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var repository = await LoadRepositoryAsync(request.RepositoryId, cancellationToken);
        if (repository is null) return [];

        if (!Guid.TryParseExact(request.EngineSnapshotId, "N", out var snapshotId)) return [];

        var key = $"{request.RepositoryId:N}/{snapshotId:N}.tar.gz{ArchiveCipher.Extension}";
        var destination = destinations.ToDestination(repository, await CredentialsFor(repository, cancellationToken));

        string? fetched = null, decrypted = null;
        try
        {
            fetched = await storage.GetToLocalAsync(destination, key, cancellationToken);
            decrypted = await DecryptAsync(fetched, cancellationToken);
            return ListTarLevel(decrypted, request.RelativePath);
        }
        finally
        {
            if (decrypted is not null && !string.Equals(decrypted, fetched, StringComparison.Ordinal))
                Delete(decrypted);
        }
    }

    public async Task<EngineOperationResult> DeleteSnapshotAsync(
        DeleteSnapshotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var repository = await LoadRepositoryAsync(request.RepositoryId, cancellationToken);
        if (repository is null) return new EngineOperationResult(false, "That repository no longer exists.");

        if (!Guid.TryParseExact(request.EngineSnapshotId, "N", out var snapshotId))
            return new EngineOperationResult(false, "That snapshot reference is not valid for this engine.");

        try
        {
            var key = $"{request.RepositoryId:N}/{snapshotId:N}.tar.gz{ArchiveCipher.Extension}";
            var destination = destinations.ToDestination(repository, await CredentialsFor(repository, cancellationToken));
            await storage.DeleteAsync(destination, key, cancellationToken);
            return new EngineOperationResult(true);
        }
        catch (Exception ex)
        {
            return new EngineOperationResult(false, ex.Message);
        }
    }

    // --- internals -------------------------------------------------------------------------

    private async Task<BackupRepository?> LoadRepositoryAsync(Guid repositoryId, CancellationToken ct) =>
        // Unfiltered: reached from background jobs that run unscoped. The caller has already
        // established the repository belongs to the tenant that asked.
        await db.BackupRepositories.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == repositoryId, ct);

    private async Task<RepositoryCredentials?> CredentialsFor(BackupRepository repository, CancellationToken ct) =>
        repository.Type is BackupRepositoryType.Local
            ? null
            : await credentials.GetCredentialsAsync(repository.Id, ct);

    private async Task<BackupRepositoryResult> WriteProbeAsync(
        CreateBackupRepositoryRequest request, CancellationToken ct)
    {
        var repository = new BackupRepository
        {
            Id = request.RepositoryId,
            Name = request.Name,
            Type = request.Type,
            Endpoint = request.Endpoint,
            Bucket = request.Bucket,
            Region = request.Region,
            BasePath = request.BasePath ?? request.LocalPath
        };

        var destination = destinations.ToDestination(repository, request.Credentials);
        var probePath = Path.Combine(Path.GetTempPath(), $"harbora-probe-{Guid.CreateVersion7():N}");
        var probeKey = $"{request.RepositoryId:N}/.harbora-probe";

        try
        {
            await File.WriteAllTextAsync(probePath, "harbora", ct);
            await storage.PutFileAsync(destination, probeKey, probePath, ct);
            await storage.DeleteAsync(destination, probeKey, ct);
            return new BackupRepositoryResult(true, request.RepositoryId, false,
                EngineRepositoryId: request.RepositoryId.ToString("N"));
        }
        catch (Exception ex)
        {
            return new BackupRepositoryResult(false, request.RepositoryId, false, null,
                $"The repository could not be written to: {ex.Message}");
        }
        finally
        {
            Delete(probePath);
        }
    }

    /// <summary>Tar + gzip a directory, returning the uncompressed byte count.</summary>
    private static async Task<long> WriteArchiveAsync(string sourcePath, string archivePath, CancellationToken ct)
    {
        await using (var file = File.Create(archivePath))
        await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
            await TarFile.CreateFromDirectoryAsync(sourcePath, gzip, includeBaseDirectory: false, ct);

        long total = 0;
        foreach (var entry in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(entry).Length; }
            catch (Exception) { /* a file that vanished mid-walk is not a failed backup */ }
        }
        return total;
    }

    private async Task<string> DecryptAsync(string path, CancellationToken ct)
    {
        if (!await ArchiveCipher.IsEncryptedArchiveAsync(path, ct)) return path;

        var target = path.EndsWith(ArchiveCipher.Extension, StringComparison.Ordinal)
            ? path[..^ArchiveCipher.Extension.Length]
            : path + ".plain";

        await using (var cipher = File.OpenRead(path))
        await using (var plain = File.Create(target))
            await ArchiveCipher.DecryptAsync(cipher, plain, protector.DeriveKey(ArchiveKeyPurpose), ct);

        return target;
    }

    /// <summary>
    /// Extract, entry by entry, with every name confined to the destination.
    ///
    /// <para>
    /// Written by hand rather than with <c>TarFile.ExtractToDirectory</c> so that each entry passes
    /// <see cref="PathGuard"/> first, so the conflict strategy is honoured per entry, and so only
    /// regular files and directories are materialised — a symlink or device node in a snapshot is
    /// skipped rather than recreated (THREAT_MODEL T2, T8).
    /// </para>
    /// </summary>
    private async Task<RestoreResult> ExtractAsync(
        string archivePath,
        string destinationPath,
        IReadOnlyList<string>? entries,
        RestoreConflictStrategy conflictStrategy,
        CancellationToken ct)
    {
        var root = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(root);

        var wanted = entries is { Count: > 0 }
            ? entries.Select(e => e.Replace('\\', '/').Trim('/')).ToHashSet(StringComparer.Ordinal)
            : null;

        var warnings = new List<string>();
        long restoredFiles = 0, restoredBytes = 0, entryCount = 0;

        await using var file = File.OpenRead(archivePath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var tar = new TarReader(gzip);

        while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } entry)
        {
            if (++entryCount > _options.MaxRestoreEntryCount)
                throw new InvalidOperationException(
                    $"This archive contains more than {_options.MaxRestoreEntryCount:N0} entries and was " +
                    "refused. Restore a subdirectory instead.");

            var name = NormaliseEntryName(entry.Name);
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (wanted is not null && !wanted.Any(w => name == w || name.StartsWith(w + "/", StringComparison.Ordinal)))
                continue;

            var check = PathGuard.ValidateArchiveEntry(root, name);
            if (!check.Allowed)
            {
                // Named, not silently dropped. An entry trying to escape the destination is worth
                // telling the operator about — it is either a broken archive or an attack.
                warnings.Add($"Refused '{entry.Name}' ({check.Rejection}).");
                continue;
            }

            var target = check.ResolvedPath!;

            if (entry.EntryType is TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                warnings.Add($"Skipped '{entry.Name}' — {entry.EntryType} entries are not restored.");
                continue;
            }

            if (File.Exists(target))
            {
                switch (conflictStrategy)
                {
                    case RestoreConflictStrategy.Fail:
                        throw new InvalidOperationException(
                            $"'{name}' already exists at the destination and the conflict strategy is " +
                            "'Fail'. Nothing further was written.");
                    case RestoreConflictStrategy.Skip:
                        continue;
                    case RestoreConflictStrategy.Rename:
                        target = NextFreeName(target);
                        break;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var output = File.Create(target))
            {
                if (entry.DataStream is { } data) await data.CopyToAsync(output, ct);
                restoredBytes += output.Length;
            }

            if (restoredBytes > _options.MaxRestoreExpandedBytes)
                throw new InvalidOperationException(
                    "This restore expanded past its size limit and was stopped. The archive may be " +
                    "larger than expected, or the limit may need raising.");

            restoredFiles++;
        }

        return new RestoreResult(true, restoredFiles, restoredBytes, root,
            Warnings: warnings.Count > 0 ? warnings : null);
    }

    private static IReadOnlyList<EngineEntry> ListTarLevel(string archivePath, string relativePath)
    {
        var prefix = relativePath.Replace('\\', '/').Trim('/');
        if (prefix.Length > 0) prefix += "/";

        var seen = new Dictionary<string, EngineEntry>(StringComparer.Ordinal);

        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);

        while (tar.GetNextEntry() is { } entry)
        {
            var name = NormaliseEntryName(entry.Name);
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var remainder = name[prefix.Length..].TrimEnd('/');
            if (remainder.Length == 0) continue;

            // Only this level: anything deeper is folded into the directory that contains it, so a
            // 200k-entry archive still browses one screen at a time.
            var slash = remainder.IndexOf('/');
            var isDirectory = slash >= 0 || entry.EntryType is TarEntryType.Directory;
            var displayName = slash >= 0 ? remainder[..slash] : remainder;

            seen.TryAdd(displayName, new EngineEntry(
                displayName,
                prefix + displayName,
                isDirectory,
                isDirectory ? 0 : entry.Length,
                entry.ModificationTime));
        }

        return seen.Values
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Normalise a tar entry name without destroying the evidence in it.
    ///
    /// <para>
    /// Tar conventionally prefixes entries with <c>./</c>, and that prefix alone is stripped. An
    /// earlier version used <c>TrimStart('.', '/')</c>, which treats those as a character set and
    /// therefore turned <c>../../escaped.txt</c> into <c>escaped.txt</c>: the traversal never
    /// reached <see cref="PathGuard"/>, nothing escaped, and the hostile entry was silently restored
    /// under a different name with no warning to anyone. Neutralising an attack is not the same as
    /// detecting it, and only one of the two is worth reporting.
    /// </para>
    /// <para>
    /// A leading <c>/</c> is deliberately left in place so a rooted entry is rejected as
    /// <see cref="PathRejection.Rooted"/> rather than quietly reinterpreted as relative.
    /// </para>
    /// </summary>
    private static string NormaliseEntryName(string raw)
    {
        var name = raw.Replace('\\', '/');
        while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
        return name;
    }

    private static string NextFreeName(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(directory, $"{stem} ({Guid.CreateVersion7():N}){extension}");
    }

    private static void Delete(string? path)
    {
        if (path is null) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception) { /* best effort — a leftover staging file is not worth failing over */ }
    }
}
