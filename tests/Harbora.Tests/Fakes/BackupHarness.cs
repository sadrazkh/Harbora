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

    /// <summary>This panel's own daemon — the machine a backup must NOT reach for by default.</summary>
    public FakeDockerEngine Docker { get; } = new();

    /// <summary>Which machine holds what. Anything not registered here is the panel's own daemon.</summary>
    public FakeServerEngineFactory Engines { get; }

    public Guid WorkspaceId { get; } = Guid.NewGuid();
    public BackupDestination Destination { get; }

    private readonly string _dir;

    public BackupHarness()
    {
        Engines = new FakeServerEngineFactory(Docker);

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

    /// <summary>
    /// Shared by every engine this harness builds, so what one queued is still readable after the
    /// call that queued it — the exclusion key a backup is enqueued under is the whole of Task 5's
    /// guarantee for this engine, and it is only visible here.
    /// </summary>
    public NoopJobQueue Jobs { get; } = new();

    public BackupEngine Engine() => new(
        Db, Engines, Storage, new PassthroughProtector(), Jobs,
        Notifications, new Harbora.Infrastructure.Monitoring.IncidentService(Db), Delivery(), Clock, Options.AsOptions(),
        Microsoft.Extensions.Options.Options.Create(Runtime),
        NullLogger<BackupEngine>.Instance);

    /// <summary>Networking the database export needs — it runs on the tenant's own network.</summary>
    public Harbora.Infrastructure.Deployments.HarboraRuntimeOptions Runtime { get; } = new();

    // --- who holds the data -------------------------------------------------------------------

    /// <summary>
    /// Another machine, with its own daemon, behind a server id of the caller's choosing — the older
    /// inbound HTTP agent, which unlike a v1 node will happily run any helper container it is asked
    /// to. The <see cref="Harbora.Domain.Servers.Server"/> row goes in too, so a refusal can name the
    /// machine the way an operator knows it rather than by its id.
    /// </summary>
    public FakeDockerEngine ServerAt(Guid serverId, string name = "web-02")
    {
        Db.Servers.Add(new Harbora.Domain.Servers.Server
        {
            Id = serverId, Name = name, Hostname = name, IsLocal = false,
            AgentEndpoint = $"https://{name}:9443"
        });
        Db.SaveChanges();

        var engine = new FakeDockerEngine();
        Engines.On(serverId, engine);
        return engine;
    }

    /// <summary>A PostgreSQL database scheduled on the given server, with its workspace.</summary>
    /// <param name="environmentId">
    /// Placed in this environment when given — <see cref="SeedEnvironmentAsync"/> makes one. Left
    /// null, a default environment is created and used: EnvironmentId is required (P2, 2026-08-17
    /// app-environment-management design), so every call site that does not care which environment
    /// still needs a real one rather than none at all.
    /// </param>
    public async Task<Harbora.Domain.Services.ManagedService> SeedDatabaseAsync(
        Guid serverId, string name = "orders", Guid? environmentId = null)
    {
        await EnsureWorkspaceAsync();
        var placedIn = environmentId ?? await SeedEnvironmentAsync();

        var service = new Harbora.Domain.Services.ManagedService
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            EnvironmentId = placedIn,
            ServerId = serverId,
            Name = name,
            Type = ManagedServiceType.PostgreSql,
            Version = "16-alpine",
            ContainerName = $"harbora-{name}",
            InternalPort = 5432,
            Username = "harbora",
            EncryptedPassword = "s3cret",
            DatabaseName = name,
            VolumeName = $"{name}-data"
        };

        Db.ManagedServices.Add(service);
        await Db.SaveChangesAsync();
        return service;
    }

    /// <summary>
    /// A project and one environment inside it, in this harness's workspace — what a database has to
    /// be placed in before it gets a network of its own rather than the shared workspace one.
    ///
    /// The default slug carries a short unique suffix rather than a fixed name, so two calls with no
    /// argument in the same test — one seeding a database with no explicit placement, say, then
    /// another — do not collide on the (WorkspaceId, Slug) unique index.
    /// </summary>
    public async Task<Guid> SeedEnvironmentAsync(string? projectSlug = null, string environmentSlug = "prod")
    {
        await EnsureWorkspaceAsync();
        projectSlug ??= "shop-" + Guid.NewGuid().ToString("N")[..8];

        var project = new Harbora.Domain.Projects.Project
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, Name = projectSlug, Slug = projectSlug
        };
        var environment = new Harbora.Domain.Projects.Environment
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, ProjectId = project.Id,
            Name = environmentSlug, Slug = environmentSlug, IsDefault = true
        };
        Db.Projects.Add(project);
        Db.Environments.Add(environment);
        await Db.SaveChangesAsync();
        return environment.Id;
    }

    /// <summary>An application on the given server, declaring one docker volume by name.</summary>
    public async Task<Harbora.Domain.Apps.App> SeedAppWithVolumeAsync(
        Guid serverId, string volumeName, string slug = "blog")
    {
        await EnsureWorkspaceAsync();
        var environmentId = await SeedEnvironmentAsync();

        var app = new Harbora.Domain.Apps.App
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, EnvironmentId = environmentId, ServerId = serverId,
            Name = slug, Slug = slug
        };
        app.Volumes.Add(new Harbora.Domain.Apps.Volume { Name = volumeName, MountPath = "/data" });

        Db.Apps.Add(app);
        await Db.SaveChangesAsync();
        return app;
    }

    /// <summary>A backup waiting to run, exactly as <c>QueueBackupAsync</c> would have written it.</summary>
    public async Task<Backup> SeedPendingBackupAsync(BackupType type, string targetRef)
    {
        var backup = new Backup
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, DestinationId = Destination.Id,
            Type = type, TargetRef = targetRef, Status = BackupStatus.Pending
        };
        Db.Backups.Add(backup);
        await Db.SaveChangesAsync();
        return backup;
    }

    /// <summary>A finished logical dump of a database, artifact and all — what verification reads.</summary>
    public async Task<Backup> SeedCompletedDatabaseDumpAsync(Guid serviceId)
    {
        var path = Path.Combine(_dir, $"database-{Guid.NewGuid():N}.sql.gz");
        await using (var file = File.Create(path))
        await using (var gz = new GZipStream(file, CompressionLevel.Optimal))
            await gz.WriteAsync(Encoding.UTF8.GetBytes("-- a dump\n"));

        var backup = new Backup
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, DestinationId = Destination.Id,
            Type = BackupType.Database, Status = BackupStatus.Completed,
            TargetRef = serviceId.ToString(), ArtifactPath = path,
            Checksum = await Sha256Async(path), SizeBytes = new FileInfo(path).Length,
            FinishedAt = Clock.UtcNow
        };
        Db.Backups.Add(backup);
        await Db.SaveChangesAsync();
        return backup;
    }

    private async Task EnsureWorkspaceAsync()
    {
        if (await Db.Workspaces.AnyAsync(w => w.Id == WorkspaceId)) return;

        Db.Workspaces.Add(new Harbora.Domain.Identity.Workspace
        { Id = WorkspaceId, Name = "Acme", Slug = "acme" });
        await Db.SaveChangesAsync();
    }

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

    public Task<string> GetToLocalAsync(
        BackupDestination dest, string artifactRef, CancellationToken ct, string? localFileName = null)
        => Task.FromResult(artifactRef);

    public Task DeleteAsync(BackupDestination dest, string artifactRef, CancellationToken ct)
    {
        if (File.Exists(artifactRef)) File.Delete(artifactRef);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Runs nothing, but remembers what it was asked to run — including what each job excludes on,
/// which is the only place a caller's serialisation decision is observable.
/// </summary>
public sealed class NoopJobQueue : IJobQueue
{
    /// <summary>Every enqueue, with <c>Job.ExcludesOn</c> already resolved the way the queue does it.</summary>
    public List<(Harbora.Domain.Jobs.JobKind Kind, Guid TargetId, Guid ExcludesOn)> Enqueued { get; } = [];

    public Task<Guid> EnqueueAsync(
        Harbora.Domain.Jobs.JobKind kind, Guid targetId, Guid? workspaceId = null, CancellationToken ct = default)
    {
        Enqueued.Add((kind, targetId, targetId));
        return Task.FromResult(Guid.NewGuid());
    }

    public Task<Guid> EnqueueExclusiveAsync(
        Harbora.Domain.Jobs.JobKind kind, Guid targetId, Guid exclusiveWith, Guid? workspaceId = null,
        CancellationToken ct = default)
    {
        Enqueued.Add((kind, targetId, exclusiveWith));
        return Task.FromResult(Guid.NewGuid());
    }

    public Task<bool> RequestCancellationAsync(Harbora.Domain.Jobs.JobKind kind, Guid targetId, CancellationToken ct = default)
        => Task.FromResult(false);
}

internal static class OptionsExtensions
{
    public static IOptions<T> AsOptions<T>(this T value) where T : class => Options.Create(value);
}
