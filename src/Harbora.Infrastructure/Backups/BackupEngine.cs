using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Backups;

/// <summary>
/// Runs and restores backups. Volume/database backups tar the target's data volume through a
/// throwaway alpine container that shares the staging volume with the panel; config/platform
/// backups serialize metadata to gzipped JSON. Secret env values are stored as-is (ciphertext),
/// so backups never contain plaintext secrets.
/// </summary>
public sealed class BackupEngine(
    HarboraDbContext db,
    IDockerEngine docker,
    IBackupStorage storage,
    ISecretProtector protector,
    IJobQueue jobs,
    INotificationService notifications,
    BackupDeliveryService delivery,
    ISystemClock clock,
    IOptions<BackupOptions> options,
    ILogger<BackupEngine> logger) : IBackupEngine
{
    private readonly BackupOptions _opt = options.Value;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public async Task<Guid> QueueBackupAsync(Guid workspaceId, BackupType type, string targetRef, Guid destinationId, bool scheduled, CancellationToken ct)
    {
        var backup = new Backup
        {
            WorkspaceId = workspaceId, Type = type, TargetRef = targetRef,
            DestinationId = destinationId, Status = BackupStatus.Pending, IsScheduled = scheduled
        };
        db.Backups.Add(backup);
        await db.SaveChangesAsync(ct);

        var id = backup.Id;
        await jobs.EnqueueAsync(Harbora.Domain.Jobs.JobKind.Backup, id, ct);
        return id;
    }

    public async Task RunAsync(Guid backupId, CancellationToken ct)
    {
        var backup = await db.Backups.Include(b => b.Destination).FirstOrDefaultAsync(b => b.Id == backupId, ct);
        if (backup?.Destination is null) return;

        try
        {
            backup.Status = BackupStatus.Running;
            backup.StartedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);

            Directory.CreateDirectory(_opt.StagingDir);
            // InvariantCulture: this stamp goes into a FILENAME. The panel's default culture is
            // Persian, so the ambient calendar would write Jalali years (14050507) into artifact
            // names — inconsistent with backups taken from a background job, and unsortable.
            var stamp = clock.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var (key, stagedPath) = backup.Type switch
            {
                BackupType.AppConfig => await BackupAppConfigAsync(backup, stamp, ct),
                BackupType.FullPlatform => await BackupPlatformAsync(backup, stamp, ct),
                _ => await BackupVolumeAsync(backup, stamp, ct) // Database / Volume / Service
            };

            // Encrypt before the artifact leaves staging. The checksum is taken over the file we
            // actually store, so verification can detect corruption in transit or at rest without
            // needing the key.
            var (publishKey, publishPath) = await ProtectArtifactAsync(key, stagedPath, ct);

            backup.Checksum = await Sha256Async(publishPath, ct);
            var (artifactRef, size) = await storage.PutFileAsync(backup.Destination, publishKey, publishPath, ct);
            backup.ArtifactPath = artifactRef;
            backup.SizeBytes = size;

            // A copy goes out to Telegram/email before the staging file is removed. Delivery cannot
            // fail the backup: the artifact is already stored, and a chat being unreachable is not a
            // reason to call a successful backup failed.
            await delivery.DeliverAsync(backup, publishPath, ct);

            // Drop the staging copies if the destination stored the artifact elsewhere (S3, custom dir).
            if (!string.Equals(artifactRef, publishPath, StringComparison.OrdinalIgnoreCase) && File.Exists(publishPath))
                File.Delete(publishPath);
            if (!string.Equals(publishPath, stagedPath, StringComparison.OrdinalIgnoreCase) && File.Exists(stagedPath))
                File.Delete(stagedPath);

            backup.Status = BackupStatus.Completed;
            backup.FinishedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);

            await EnforceRetentionAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backup {Id} failed.", backupId);
            backup.Status = BackupStatus.Failed;
            backup.ErrorMessage = ex.Message;
            backup.FinishedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await notifications.NotifyAsync(backup.WorkspaceId, AlertEvent.BackupFailed, AlertSeverity.Warning,
                $"Backup failed: {backup.Type}", ex.Message, ct);
        }
    }

    // --- backup producers ---

    private async Task<(string Key, string Path)> BackupAppConfigAsync(Backup backup, string stamp, CancellationToken ct)
    {
        var appId = Guid.Parse(backup.TargetRef);
        var app = await db.Apps.Include(a => a.EnvironmentVariables).Include(a => a.Domains).Include(a => a.Volumes)
            .AsNoTracking().FirstAsync(a => a.Id == appId, ct);

        var snapshot = new
        {
            kind = "app-config", version = 1,
            app = new { app.Name, app.Slug, app.SourceType, app.ContainerPort, app.DockerfilePath, app.PrebuiltImage, app.GitRef },
            env = app.EnvironmentVariables.Select(e => new { e.Key, e.Value, e.IsSecret, e.AvailableAtBuild }),
            domains = app.Domains.Select(d => new { d.Host, d.SslEnabled, d.ForceHttps }),
            volumes = app.Volumes.Select(v => new { v.Name, v.MountPath, v.ReadOnly })
        };
        var key = $"appconfig-{app.Slug}-{stamp}.json.gz";
        return (key, await WriteGzJsonAsync(key, snapshot, ct));
    }

    private async Task<(string Key, string Path)> BackupPlatformAsync(Backup backup, string stamp, CancellationToken ct)
    {
        var snapshot = new
        {
            kind = "platform", version = 1, at = clock.UtcNow,
            settings = await db.Settings.Where(s => !s.IsSecret).Select(s => new { s.Key, s.Value }).ToListAsync(ct),
            apps = await db.Apps.Where(a => a.WorkspaceId == backup.WorkspaceId).Select(a => new { a.Name, a.Slug, a.SourceType }).ToListAsync(ct),
            routes = await db.Routes.Where(r => r.WorkspaceId == backup.WorkspaceId).Select(r => new { r.Host, r.PathPrefix, r.TargetService, r.TargetPort }).ToListAsync(ct),
            services = await db.ManagedServices.Where(s => s.WorkspaceId == backup.WorkspaceId).Select(s => new { s.Name, s.Type, s.Version }).ToListAsync(ct)
        };
        var key = $"platform-{stamp}.json.gz";
        return (key, await WriteGzJsonAsync(key, snapshot, ct));
    }

    private async Task<(string Key, string Path)> BackupVolumeAsync(Backup backup, string stamp, CancellationToken ct)
    {
        var (volumeName, label) = await ResolveVolumeAsync(backup.Type, backup.TargetRef, ct);
        var key = $"{backup.Type.ToString().ToLowerInvariant()}-{label}-{stamp}.tgz";

        var exit = await docker.RunOneOffAsync(new DockerOneOffRequest(
            _opt.HelperImage,
            ["sh", "-c", $"tar czf /backup/{key} -C /data ."],
            [(volumeName, "/data", true), (_opt.StagingVolume, "/backup", false)]),
            new Progress<string>(l => logger.LogDebug("backup: {Line}", l)), ct);

        if (exit != 0) throw new InvalidOperationException($"Volume archive failed (exit {exit}).");

        // The helper container writes into the staging volume BY NAME while the panel reads it via a
        // mount. If those resolve to different volumes — Compose prefixing the name was exactly this
        // bug — tar reports success and the archive lands somewhere the panel can never read. Say so,
        // instead of failing later with a bare "file not found".
        var staged = Path.Combine(_opt.StagingDir, key);
        if (!File.Exists(staged))
            throw new InvalidOperationException(
                $"The archive was created but is not visible at {staged}. The helper container mounts " +
                $"the volume '{_opt.StagingVolume}' while the panel reads {_opt.StagingDir}; check that " +
                "both resolve to the SAME docker volume (`docker volume ls`).");

        return (key, staged);
    }

    // --- restore ---

    public async Task RestoreAsync(Guid backupId, CancellationToken ct)
    {
        var backup = await db.Backups.Include(b => b.Destination).FirstAsync(b => b.Id == backupId, ct);
        if (backup.Status != BackupStatus.Completed || backup.ArtifactPath is null)
            throw new InvalidOperationException("Only completed backups can be restored.");

        var fetched = await storage.GetToLocalAsync(backup.Destination!, backup.ArtifactPath, ct);

        // Integrity gate. A volume restore does `rm -rf` before untarring, so restoring a corrupt or
        // truncated archive destroys the live data AND has nothing to put back. The checksum was
        // recorded at backup time precisely for this moment — verify before touching anything.
        await RequireIntactAsync(backup, fetched, ct);

        // Decrypt into a working copy; unencrypted legacy artifacts pass through unchanged.
        var localPath = await UnprotectArtifactAsync(fetched, ct);

        // A checksum only proves these are the bytes we stored — not that they form a usable archive.
        // The restore itself extracts before it swaps (see RestoreScript), so a bad archive is no
        // longer catastrophic; failing here still means the user learns immediately instead of after
        // a helper container has run.
        await ProbeArchiveAsync(backup.Type, localPath, ct);

        if (backup.Type is BackupType.AppConfig)
        {
            await RestoreAppConfigAsync(backup, localPath, ct);
            return;
        }
        if (backup.Type is BackupType.FullPlatform)
        {
            await RestorePlatformAsync(localPath, ct);
            return;
        }

        // Volume/database restore: stop the container, wipe + untar the volume, restart.
        var (volumeName, _) = await ResolveVolumeAsync(backup.Type, backup.TargetRef, ct);
        var fileName = Path.GetFileName(localPath);
        var stagedCopy = Path.Combine(_opt.StagingDir, fileName);
        if (!string.Equals(Path.GetFullPath(localPath), Path.GetFullPath(stagedCopy), StringComparison.OrdinalIgnoreCase))
            File.Copy(localPath, stagedCopy, overwrite: true);

        var containerName = await ContainerForTargetAsync(backup.Type, backup.TargetRef, ct);
        if (containerName is not null) await StopIfRunning(containerName, ct);

        // The swap below can undo a failed restore, but not a successful restore of the WRONG backup.
        // This snapshot is what covers that case.
        await SnapshotBeforeRestoreAsync(volumeName, ct);

        var exit = await docker.RunOneOffAsync(new DockerOneOffRequest(
            _opt.HelperImage,
            ["sh", "-c", RestoreScript.Build(fileName)],
            [(volumeName, "/data", false), (_opt.StagingVolume, "/backup", true)]),
            new Progress<string>(l => logger.LogDebug("restore: {Line}", l)), ct);

        if (exit == RestoreScript.RolledBackExitCode)
            throw new InvalidOperationException(
                "The restore could not be completed and the volume's original contents were put back. " +
                "Nothing was lost; check disk space on the server and try again.");
        if (exit != 0) throw new InvalidOperationException($"Restore failed (exit {exit}).");
        if (containerName is not null) await docker.RestartContainerAsync(await RequireContainerIdAsync(containerName, ct), ct);
    }

    private async Task RestoreAppConfigAsync(Backup backup, string localPath, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await ReadGzAsync(localPath, ct));
        var slug = doc.RootElement.GetProperty("app").GetProperty("Slug").GetString();
        var app = await db.Apps.Include(a => a.EnvironmentVariables)
            .FirstOrDefaultAsync(a => a.WorkspaceId == backup.WorkspaceId && a.Slug == slug, ct);
        if (app is null) throw new InvalidOperationException($"App '{slug}' no longer exists.");

        // Re-apply env vars (values are stored as-is, secrets stay encrypted).
        var env = doc.RootElement.GetProperty("env");
        foreach (var e in env.EnumerateArray())
        {
            var key = e.GetProperty("Key").GetString()!;
            var existing = app.EnvironmentVariables.FirstOrDefault(x => x.Key == key);
            if (existing is null)
                app.EnvironmentVariables.Add(new Domain.Apps.EnvironmentVariable
                { Key = key, Value = e.GetProperty("Value").GetString() ?? "", IsSecret = e.GetProperty("IsSecret").GetBoolean() });
            else
                existing.Value = e.GetProperty("Value").GetString() ?? "";
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task RestorePlatformAsync(string localPath, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await ReadGzAsync(localPath, ct));
        foreach (var s in doc.RootElement.GetProperty("settings").EnumerateArray())
        {
            var key = s.GetProperty("Key").GetString()!;
            var value = s.GetProperty("Value").GetString() ?? "";
            var setting = await db.Settings.FirstOrDefaultAsync(x => x.Key == key, ct);
            if (setting is null) db.Settings.Add(new Domain.Settings.Setting { Key = key, Value = value });
            else setting.Value = value;
        }
        await db.SaveChangesAsync(ct);
    }

    // --- download + retention ---

    public async Task<(Stream Stream, string FileName)> OpenArtifactAsync(Guid backupId, CancellationToken ct)
    {
        var backup = await db.Backups.Include(b => b.Destination).AsNoTracking().FirstAsync(b => b.Id == backupId, ct);
        if (backup.ArtifactPath is null) throw new InvalidOperationException("Backup has no artifact.");
        var localPath = await storage.GetToLocalAsync(backup.Destination!, backup.ArtifactPath, ct);
        return (File.OpenRead(localPath), Path.GetFileName(localPath));
    }

    public async Task EnforceRetentionAsync(CancellationToken ct)
    {
        var completed = await db.Backups.Include(b => b.Destination)
            .Where(b => b.Status == BackupStatus.Completed)
            .OrderByDescending(b => b.CreatedAt).ToListAsync(ct);

        var schedules = await db.BackupSchedules.AsNoTracking().ToListAsync(ct);

        foreach (var group in completed.GroupBy(b => new { b.WorkspaceId, b.Type, b.TargetRef }))
        {
            var keep = schedules.FirstOrDefault(s =>
                s.WorkspaceId == group.Key.WorkspaceId && s.Type == group.Key.Type && s.TargetRef == group.Key.TargetRef)
                ?.RetentionCount ?? _opt.DefaultRetentionCount;

            foreach (var stale in group.Skip(keep))
            {
                try { if (stale.Destination is not null && stale.ArtifactPath is not null) await storage.DeleteAsync(stale.Destination, stale.ArtifactPath, ct); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to delete artifact for backup {Id}.", stale.Id); }
                stale.Status = BackupStatus.Expired;
                db.Backups.Remove(stale);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    // --- integrity, encryption + dry run ---

    /// <summary>
    /// Recompute the stored artifact's checksum and refuse to go further if it doesn't match what
    /// was recorded when the backup was taken.
    /// </summary>
    private async Task RequireIntactAsync(Backup backup, string localPath, CancellationToken ct)
    {
        if (!File.Exists(localPath))
            throw new InvalidOperationException("The backup artifact is missing from its destination.");

        if (string.IsNullOrWhiteSpace(backup.Checksum))
        {
            // Backups taken before checksums were recorded. Restoring is still allowed — refusing
            // would strand old backups — but the operator should know it was not verified.
            logger.LogWarning("Backup {Id} has no recorded checksum; restoring without verification.", backup.Id);
            return;
        }

        var actual = await Sha256Async(localPath, ct);
        if (!string.Equals(actual, backup.Checksum, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The backup artifact does not match its recorded checksum — it is corrupt or was modified. " +
                "Restore aborted; your current data has NOT been touched.");
    }

    /// <summary>Encrypts a staged artifact when enabled; returns the file to publish.</summary>
    private async Task<(string Key, string Path)> ProtectArtifactAsync(string key, string stagedPath, CancellationToken ct)
    {
        if (!_opt.EncryptArchives) return (key, stagedPath);

        var encryptedPath = stagedPath + ArchiveCipher.Extension;
        await using (var plain = File.OpenRead(stagedPath))
        await using (var cipher = File.Create(encryptedPath))
            await ArchiveCipher.EncryptAsync(plain, cipher, ArchiveKey(), ct);

        return (key + ArchiveCipher.Extension, encryptedPath);
    }

    /// <summary>Decrypts an artifact if it is one of ours; passes plaintext archives through.</summary>
    private async Task<string> UnprotectArtifactAsync(string localPath, CancellationToken ct)
    {
        if (!await ArchiveCipher.IsEncryptedArchiveAsync(localPath, ct)) return localPath;

        var decryptedPath = localPath.EndsWith(ArchiveCipher.Extension, StringComparison.Ordinal)
            ? localPath[..^ArchiveCipher.Extension.Length]
            : localPath + ".plain";

        await using (var cipher = File.OpenRead(localPath))
        await using (var plain = File.Create(decryptedPath))
            await ArchiveCipher.DecryptAsync(cipher, plain, ArchiveKey(), ct);

        return decryptedPath;
    }

    /// <summary>
    /// Archives are encrypted with a key derived from the platform master key, so an operator who
    /// already holds the master key can always recover — there is no second secret to lose.
    /// It must be DETERMINISTIC: an earlier version derived it from Protect(), which uses a random
    /// nonce per call, so every archive was encrypted under a key that could never be reproduced.
    /// </summary>
    private byte[] ArchiveKey() => protector.DeriveKey("backup-archive");

    public async Task<BackupVerification> VerifyAsync(Guid backupId, CancellationToken ct)
    {
        var backup = await db.Backups.Include(b => b.Destination).AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == backupId, ct);

        if (backup is null) return BackupVerification.Failed("Backup not found.");
        if (backup.Status != BackupStatus.Completed || backup.ArtifactPath is null)
            return BackupVerification.Failed("Only completed backups can be verified.");

        var checks = new List<BackupCheck>();
        string fetched;
        try
        {
            fetched = await storage.GetToLocalAsync(backup.Destination!, backup.ArtifactPath, ct);
        }
        catch (Exception ex)
        {
            checks.Add(new BackupCheck("Artifact present", false, ex.Message));
            return new BackupVerification(false, $"Could not fetch the artifact: {ex.Message}", 0, checks);
        }

        var present = File.Exists(fetched);
        checks.Add(new BackupCheck("Artifact present", present));
        if (!present)
            return new BackupVerification(false, "The artifact is missing from its destination.", 0, checks);

        var size = new FileInfo(fetched).Length;

        if (string.IsNullOrWhiteSpace(backup.Checksum))
        {
            checks.Add(new BackupCheck("Checksum recorded", false, "taken before checksums were recorded"));
        }
        else
        {
            var actual = await Sha256Async(fetched, ct);
            var matches = string.Equals(actual, backup.Checksum, StringComparison.OrdinalIgnoreCase);
            checks.Add(new BackupCheck("Checksum matches", matches, matches ? null : "artifact is corrupt or was modified"));
            if (!matches)
                return new BackupVerification(false, "The artifact does not match its recorded checksum.", size, checks);
        }

        // Decrypting and reading the archive is the only way to know the bytes are usable — a
        // checksum only proves they are the bytes we stored, not that they form a valid archive.
        string readable;
        try
        {
            readable = await UnprotectArtifactAsync(fetched, ct);
            checks.Add(new BackupCheck("Decrypts", true));
        }
        catch (Exception ex)
        {
            checks.Add(new BackupCheck("Decrypts", false, ex.Message));
            return new BackupVerification(false, $"The archive could not be decrypted: {ex.Message}", size, checks);
        }

        try
        {
            await ProbeArchiveAsync(backup.Type, readable, ct);
            checks.Add(new BackupCheck("Archive readable", true));
            return new BackupVerification(true, null, size, checks);
        }
        catch (Exception ex)
        {
            checks.Add(new BackupCheck("Archive readable", false, ex.Message));
            return new BackupVerification(false, $"The archive is unreadable: {ex.Message}", size, checks);
        }
        finally
        {
            // Never leave a decrypted copy lying around after a dry run.
            if (!string.Equals(readable, fetched, StringComparison.OrdinalIgnoreCase) && File.Exists(readable))
                try { File.Delete(readable); } catch { /* best effort */ }
        }
    }

    /// <summary>Reads the archive far enough to prove it decompresses and has the expected shape.</summary>
    private async Task ProbeArchiveAsync(BackupType type, string localPath, CancellationToken ct)
    {
        if (type is BackupType.AppConfig or BackupType.FullPlatform)
        {
            using var doc = JsonDocument.Parse(await ReadGzAsync(localPath, ct));
            if (!doc.RootElement.TryGetProperty("kind", out _))
                throw new InvalidOperationException("snapshot is missing its 'kind' marker");
            return;
        }

        // Volume/database tarball: decompress the whole stream. This catches truncation and gzip
        // corruption without needing Docker, which a restore would only discover mid-wipe.
        await using var file = File.OpenRead(localPath);
        await using var gz = new GZipStream(file, CompressionMode.Decompress);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await gz.ReadAsync(buffer, ct)) > 0) total += read;
        if (total == 0) throw new InvalidOperationException("archive is empty");
    }

    /// <summary>
    /// Tars the current volume aside before a restore overwrites it. Best-effort: a restore the
    /// operator explicitly confirmed should not be blocked because the safety copy failed, but the
    /// attempt is logged either way.
    /// </summary>
    private async Task SnapshotBeforeRestoreAsync(string volumeName, CancellationToken ct)
    {
        if (!_opt.SnapshotBeforeRestore) return;

        // Restore runs inside a web request, where the culture is Persian — without the invariant
        // calendar this produced names like "pre-restore-…-14050507-184916.tgz". Observed in production.
        var name = "pre-restore-" + volumeName + "-" +
                   clock.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".tgz";
        try
        {
            var exit = await docker.RunOneOffAsync(new DockerOneOffRequest(
                _opt.HelperImage,
                ["sh", "-c", $"tar czf /backup/{name} -C /data ."],
                [(volumeName, "/data", true), (_opt.StagingVolume, "/backup", false)]),
                new Progress<string>(l => logger.LogDebug("pre-restore: {Line}", l)), ct);

            if (exit == 0) logger.LogInformation("Pre-restore snapshot written: {Name}", name);
            else logger.LogWarning("Pre-restore snapshot failed (exit {Exit}); continuing with the restore.", exit);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Pre-restore snapshot could not be taken; continuing with the restore.");
        }
    }

    // --- helpers ---

    private async Task<(string VolumeName, string Label)> ResolveVolumeAsync(BackupType type, string targetRef, CancellationToken ct)
    {
        if (type is BackupType.Database or BackupType.Service)
        {
            var svc = await db.ManagedServices.AsNoTracking().FirstAsync(s => s.Id == Guid.Parse(targetRef), ct);
            return (svc.VolumeName, svc.Name);
        }
        return (targetRef, targetRef); // Volume: targetRef is the docker volume name
    }

    private async Task<string?> ContainerForTargetAsync(BackupType type, string targetRef, CancellationToken ct)
    {
        if (type is BackupType.Database or BackupType.Service)
            return await db.ManagedServices.AsNoTracking().Where(s => s.Id == Guid.Parse(targetRef))
                .Select(s => s.ContainerName).FirstOrDefaultAsync(ct);
        return null;
    }

    private async Task StopIfRunning(string containerName, CancellationToken ct)
    {
        var containers = await docker.ListContainersAsync("harbora.service", ct);
        var c = containers.FirstOrDefault(x => x.Name == containerName);
        if (c is not null && c.State.Equals("running", StringComparison.OrdinalIgnoreCase))
            await docker.StopContainerAsync(c.Id, ct);
    }

    private async Task<string> RequireContainerIdAsync(string containerName, CancellationToken ct)
    {
        var containers = await docker.ListContainersAsync("harbora.service", ct);
        return containers.First(x => x.Name == containerName).Id;
    }

    private async Task<string> WriteGzJsonAsync(string key, object payload, CancellationToken ct)
    {
        var path = Path.Combine(_opt.StagingDir, key);
        await using var file = File.Create(path);
        await using var gz = new GZipStream(file, CompressionLevel.Optimal);
        await JsonSerializer.SerializeAsync(gz, payload, Json, ct);
        return path;
    }

    private static async Task<string> ReadGzAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        await using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        return await reader.ReadToEndAsync(ct);
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
