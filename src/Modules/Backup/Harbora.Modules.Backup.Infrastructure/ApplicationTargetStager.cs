using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Modules.Backup.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>
/// Assembles everything needed to rebuild an application: its data volumes and its definition.
///
/// <para>
/// A volume backup restores the data and leaves you guessing at the image tag, the ports and the
/// domains. A config backup restores the definition and has no data in it. An application backup is
/// only useful if it is both, in one snapshot, taken at one moment — which is what this produces:
/// a directory holding <c>application.json</c> and one subdirectory per volume.
/// </para>
/// </summary>
public interface IApplicationTargetStager
{
    /// <summary>Confirm the app exists and can be assembled, without doing any work.</summary>
    Task<(bool Ok, string? Error)> ValidateAsync(Guid appId, CancellationToken ct);

    /// <summary>
    /// Assemble the app into a directory named from <paramref name="snapshotId"/>. Dispose the
    /// lease to remove it; see <see cref="BackupStagingLayout"/> for why the name is not a fresh
    /// Guid — a copy nothing can name from the row is a copy nothing can clean up after a crash.
    /// </summary>
    Task<TargetLease> StageAsync(Guid appId, Guid snapshotId, CancellationToken ct);
}

/// <inheritdoc />
public sealed class ApplicationTargetStager(
    HarboraDbContext db,
    IDockerEngine docker,
    IOptions<BackupModuleOptions> options,
    ILogger<ApplicationTargetStager> logger) : IApplicationTargetStager
{
    private readonly BackupModuleOptions _options = options.Value;

    /// <summary>Named so a restore can find it without guessing.</summary>
    public const string MetadataFileName = "application.json";

    /// <summary>Volume data lives under here, one directory per volume, named by the volume.</summary>
    public const string VolumesDirectoryName = "volumes";

    public async Task<(bool Ok, string? Error)> ValidateAsync(Guid appId, CancellationToken ct)
    {
        var app = await db.Apps.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == appId, ct);

        if (app is null) return (false, "That application no longer exists.");

        var volumes = await db.Volumes.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.AppId == appId).Select(v => v.Name).ToListAsync(ct);

        // Named rather than silently skipped: a volume whose name the daemon would reject is a
        // volume that will not be backed up, and finding that out during a restore is too late.
        var bad = volumes.FirstOrDefault(v => !EngineArgumentGuard.IsSafeVolumeName(v));
        if (bad is not null)
            return (false, $"The volume '{bad}' does not have a name Docker would accept.");

        return (true, null);
    }

    public async Task<TargetLease> StageAsync(Guid appId, Guid snapshotId, CancellationToken ct)
    {
        var (ok, error) = await ValidateAsync(appId, ct);
        if (!ok) return TargetLease.Fail(error!);

        var app = await db.Apps.IgnoreQueryFilters().AsNoTracking()
            .Include(a => a.EnvironmentVariables)
            .Include(a => a.Domains)
            .Include(a => a.Volumes)
            .FirstAsync(a => a.Id == appId, ct);

        var stageName = BackupStagingLayout.ApplicationDirectory(snapshotId);
        var stagePath = Path.Combine(_options.StagingDirectory, stageName);

        try
        {
            // A retry of the same snapshot lands here again. Clear it first: a half-assembled
            // application from the attempt that crashed must not be folded into the new archive.
            Cleanup(stagePath);
            Directory.CreateDirectory(stagePath);

            await WriteMetadataAsync(app, stagePath, ct);

            foreach (var volume in app.Volumes)
            {
                ct.ThrowIfCancellationRequested();

                var target = Path.Combine(stagePath, VolumesDirectoryName, volume.Name);
                Directory.CreateDirectory(target);

                var exit = await docker.RunOneOffAsync(new DockerOneOffRequest(
                        _options.HelperImage,
                        ["cp", "-a", "/data/.", $"/backup/{stageName}/{VolumesDirectoryName}/{volume.Name}"],
                        [
                            (volume.Name, "/data", true),
                            (_options.StagingVolume, "/backup", false)
                        ]),
                    new Progress<string>(line => logger.LogDebug("app staging: {Line}", line)),
                    ct);

                if (exit != 0)
                {
                    Cleanup(stagePath);
                    return TargetLease.Fail(
                        $"The volume '{volume.Name}' could not be read (helper exited {exit}). " +
                        "Nothing was backed up — a partial application backup would restore an " +
                        "application that has some of its data.");
                }
            }

            return TargetLease.Ok(stagePath, () =>
            {
                // Staged application data, in the clear. It goes as soon as the snapshot is done.
                Cleanup(stagePath);
                return ValueTask.CompletedTask;
            });
        }
        catch (Exception ex)
        {
            Cleanup(stagePath);
            logger.LogError(ex, "Staging application {AppId} failed.", appId);
            return TargetLease.Fail($"The application could not be assembled: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes the application's definition beside its data.
    ///
    /// <para>
    /// <b>Secret VALUES are not written.</b> Only the names, and a marker saying a value existed. The
    /// archive is encrypted, so including ciphertext would not be catastrophic — but a backup is
    /// copied, downloaded and restored into places the original secret never reached, and every one
    /// of those is a new copy of the credential. A restore that says "this app needs
    /// DATABASE_PASSWORD, set it" is a small inconvenience; a database password quietly living in
    /// six restored snapshots is not.
    /// </para>
    /// </summary>
    private static async Task WriteMetadataAsync(
        Harbora.Domain.Apps.App app, string stagePath, CancellationToken ct)
    {
        var metadata = new
        {
            kind = "harbora-application",
            version = 1,
            capturedAt = DateTimeOffset.UtcNow,

            application = new
            {
                app.Name,
                app.Slug,
                SourceType = app.SourceType.ToString(),
                Kind = app.Kind.ToString(),
                app.PrebuiltImage,
                app.DockerfilePath,
                app.ComposeFilePath,
                app.BuildContextPath,
                app.BuildCommand,
                app.GitRef,
                app.ReleaseCommand,
                app.Command,
                app.CronExpression
            },

            deployment = new
            {
                app.ContainerPort,
                app.PublishedHostPort,
                app.DesiredReplicas,
                app.InstanceSizeKey,
                app.MemoryLimitBytes,
                app.CpuLimit
            },

            healthCheck = new { app.HealthCheckPath },

            // Names and non-secret values. See the remark above for why the secrets stop here.
            environment = app.EnvironmentVariables.Select(e => new
            {
                e.Key,
                e.IsSecret,
                e.AvailableAtBuild,
                Value = e.IsSecret ? null : e.Value,
                Note = e.IsSecret ? "secret — value deliberately not included in the backup" : null
            }),

            domains = app.Domains.Select(d => new { d.Host, d.SslEnabled, d.ForceHttps }),

            volumes = app.Volumes.Select(v => new
            {
                v.Name,
                v.MountPath,
                v.ReadOnly,
                v.SizeLimitBytes,
                // Where this volume's contents are in the snapshot, so a restore need not guess.
                dataPath = $"{VolumesDirectoryName}/{v.Name}"
            })
        };

        var path = Path.Combine(stagePath, MetadataFileName);
        await using var file = File.Create(path);
        await JsonSerializer.SerializeAsync(
            file, metadata, new JsonSerializerOptions { WriteIndented = true }, ct);
    }

    private void Cleanup(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A staged application copy could not be removed from {Path}.", path);
        }
    }
}
