using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Backups;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Harbora.Tests.Fakes;

/// <summary>
/// Builds a real <see cref="BackupEngine"/> over a local staging directory and an in-memory
/// database. Storage is a simple pass-through to the local filesystem, so artifacts can be
/// corrupted on disk exactly as they could be in a real destination.
/// </summary>
public sealed class BackupHarness : IDisposable
{
    public HarboraDbContext Db { get; }
    public BackupOptions Options { get; }
    public FixedClock Clock { get; } = new();
    public LocalOnlyStorage Storage { get; }
    public RecordingNotificationService Notifications { get; } = new();
    public FakeDockerEngine Docker { get; } = new();

    public Guid WorkspaceId { get; } = Guid.NewGuid();
    public BackupDestination Destination { get; }

    private readonly string _dir;

    public BackupHarness()
    {
        _dir = Path.Combine(Path.GetTempPath(), "harbora-backup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        Options = new BackupOptions { StagingDir = _dir, EncryptArchives = true, SnapshotBeforeRestore = false };
        Storage = new LocalOnlyStorage(_dir);

        Db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("backup-" + Guid.NewGuid()).Options);

        Destination = new BackupDestination
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, Name = "local",
            Type = BackupDestinationType.Local, LocalPath = _dir
        };
        Db.BackupDestinations.Add(Destination);
        Db.SaveChanges();
    }

    /// <summary>Records what each finished backup was handed to a delivery channel.</summary>
    public StubHttpClientFactory DeliveryHttp { get; } = new(System.Net.HttpStatusCode.OK);

    public BackupDeliveryService Delivery() => new(
        Db, new PassthroughProtector(), DeliveryHttp, Clock,
        NullLogger<BackupDeliveryService>.Instance);

    public BackupEngine Engine() => new(
        Db, Docker, Storage, new PassthroughProtector(), new NoopJobQueue(),
        Notifications, Delivery(), Clock, Options.AsOptions(),
        Microsoft.Extensions.Options.Options.Create(Runtime),
        NullLogger<BackupEngine>.Instance);

    /// <summary>Networking the database export needs — it runs on the tenant's own network.</summary>
    public Harbora.Infrastructure.Deployments.HarboraRuntimeOptions Runtime { get; } = new();

    /// <summary>Writes a real gzipped JSON snapshot artifact and the row that points at it.</summary>
    public async Task<Backup> SeedAppConfigBackupAsync(bool encrypt = true, string kind = "app-config")
    {
        var payload = new { kind, version = 1, app = new { Slug = "blog" }, env = Array.Empty<object>() };
        var plainPath = Path.Combine(_dir, $"appconfig-{Guid.NewGuid():N}.json.gz");

        await using (var file = File.Create(plainPath))
        await using (var gz = new GZipStream(file, CompressionLevel.Optimal))
            await JsonSerializer.SerializeAsync(gz, payload);

        var storedPath = plainPath;
        if (encrypt)
        {
            storedPath = plainPath + ArchiveCipher.Extension;
            await using (var plain = File.OpenRead(plainPath))
            await using (var cipher = File.Create(storedPath))
                await ArchiveCipher.EncryptAsync(plain, cipher, ArchiveKey(), default);
            File.Delete(plainPath);
        }

        return await SeedRowAsync(BackupType.AppConfig, storedPath);
    }

    /// <summary>Writes a real gzipped tarball-shaped artifact (gzip stream) for volume backups.</summary>
    public async Task<Backup> SeedVolumeBackupAsync(bool encrypt = true)
    {
        var plainPath = Path.Combine(_dir, $"volume-{Guid.NewGuid():N}.tgz");
        await using (var file = File.Create(plainPath))
        await using (var gz = new GZipStream(file, CompressionLevel.Optimal))
            await gz.WriteAsync(Encoding.UTF8.GetBytes(new string('x', 4096)));

        var storedPath = plainPath;
        if (encrypt)
        {
            storedPath = plainPath + ArchiveCipher.Extension;
            await using (var plain = File.OpenRead(plainPath))
            await using (var cipher = File.Create(storedPath))
                await ArchiveCipher.EncryptAsync(plain, cipher, ArchiveKey(), default);
            File.Delete(plainPath);
        }

        return await SeedRowAsync(BackupType.Volume, storedPath);
    }

    private async Task<Backup> SeedRowAsync(BackupType type, string storedPath)
    {
        var backup = new Backup
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, DestinationId = Destination.Id,
            Type = type, Status = BackupStatus.Completed, TargetRef = "blog-data",
            ArtifactPath = storedPath, Checksum = await Sha256Async(storedPath),
            SizeBytes = new FileInfo(storedPath).Length, FinishedAt = Clock.UtcNow
        };
        Db.Backups.Add(backup);
        await Db.SaveChangesAsync();
        return backup;
    }

    /// <summary>
    /// Seeds a backup whose checksum is perfectly valid but whose contents are not a usable archive
    /// — the case a checksum alone can never catch.
    /// </summary>
    public async Task<Backup> SeedUnreadableArtifactAsync(BackupType type = BackupType.Volume)
    {
        var path = Path.Combine(_dir, $"garbage-{Guid.NewGuid():N}.tgz");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("this is not a gzip stream at all"));
        return await SeedRowAsync(type, path);
    }

    /// <summary>Corrupts the stored artifact in place, as bit-rot or tampering would.</summary>
    public void CorruptArtifact(Backup backup)
    {
        var bytes = File.ReadAllBytes(backup.ArtifactPath!);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(backup.ArtifactPath!, bytes);
    }

    public void DeleteArtifact(Backup backup) => File.Delete(backup.ArtifactPath!);

    /// <summary>Mirrors BackupEngine's key derivation for the protector used in tests.</summary>
    private static byte[] ArchiveKey() => new PassthroughProtector().DeriveKey("backup-archive");

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    public void Dispose()
    {
        Db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir — best effort */ }
    }
}

/// <summary>Storage that keeps everything on the local disk, so tests can tamper with artifacts.</summary>
public sealed class LocalOnlyStorage(string dir) : IBackupStorage
{
    public string LocalStagingDir => dir;

    public Task<(string ArtifactRef, long SizeBytes)> PutFileAsync(
        BackupDestination dest, string key, string localFilePath, CancellationToken ct)
        => Task.FromResult((localFilePath, new FileInfo(localFilePath).Length));

    public Task<string> GetToLocalAsync(BackupDestination dest, string artifactRef, CancellationToken ct)
        => Task.FromResult(artifactRef);

    public Task DeleteAsync(BackupDestination dest, string artifactRef, CancellationToken ct)
    {
        if (File.Exists(artifactRef)) File.Delete(artifactRef);
        return Task.CompletedTask;
    }
}

public sealed class NoopJobQueue : IJobQueue
{
    public Task<Guid> EnqueueAsync(Harbora.Domain.Jobs.JobKind kind, Guid targetId, CancellationToken ct = default)
        => Task.FromResult(Guid.NewGuid());

    public Task<bool> RequestCancellationAsync(Harbora.Domain.Jobs.JobKind kind, Guid targetId, CancellationToken ct = default)
        => Task.FromResult(false);
}

internal static class OptionsExtensions
{
    public static IOptions<T> AsOptions<T>(this T value) where T : class => Options.Create(value);
}
