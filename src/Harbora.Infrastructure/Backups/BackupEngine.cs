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
    IBackgroundJobQueue queue,
    INotificationService notifications,
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
        await queue.EnqueueAsync((sp, jobCt) => sp.GetRequiredService<BackupEngine>().RunAsync(id, jobCt), ct);
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
            var stamp = clock.UtcNow.ToString("yyyyMMdd-HHmmss");
            var (key, stagedPath) = backup.Type switch
            {
                BackupType.AppConfig => await BackupAppConfigAsync(backup, stamp, ct),
                BackupType.FullPlatform => await BackupPlatformAsync(backup, stamp, ct),
                _ => await BackupVolumeAsync(backup, stamp, ct) // Database / Volume / Service
            };

            backup.Checksum = await Sha256Async(stagedPath, ct);
            var (artifactRef, size) = await storage.PutFileAsync(backup.Destination, key, stagedPath, ct);
            backup.ArtifactPath = artifactRef;
            backup.SizeBytes = size;

            // Drop the staging copy if the destination stored it elsewhere (e.g. S3 or custom dir).
            if (!string.Equals(artifactRef, stagedPath, StringComparison.OrdinalIgnoreCase) && File.Exists(stagedPath))
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
        return (key, Path.Combine(_opt.StagingDir, key));
    }

    // --- restore ---

    public async Task RestoreAsync(Guid backupId, CancellationToken ct)
    {
        var backup = await db.Backups.Include(b => b.Destination).FirstAsync(b => b.Id == backupId, ct);
        if (backup.Status != BackupStatus.Completed || backup.ArtifactPath is null)
            throw new InvalidOperationException("Only completed backups can be restored.");

        var localPath = await storage.GetToLocalAsync(backup.Destination!, backup.ArtifactPath, ct);

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

        var exit = await docker.RunOneOffAsync(new DockerOneOffRequest(
            _opt.HelperImage,
            ["sh", "-c", $"rm -rf /data/* && tar xzf /backup/{fileName} -C /data"],
            [(volumeName, "/data", false), (_opt.StagingVolume, "/backup", true)]),
            new Progress<string>(l => logger.LogDebug("restore: {Line}", l)), ct);

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
