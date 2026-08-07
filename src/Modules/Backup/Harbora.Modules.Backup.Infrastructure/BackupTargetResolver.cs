using Harbora.Shared;
using Harbora.Application.Abstractions;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>A cheap, side-effect-free verdict on whether a target could be backed up.</summary>
public sealed record ResolvedTarget(bool Succeeded, string? SourcePath = null, string? Error = null);

/// <summary>
/// A readable copy of a target, held open for as long as the snapshot needs it.
///
/// <para>
/// A lease rather than a path because some targets have to be materialised first: a Docker volume is
/// staged to disk by a helper container, and that staged copy is plaintext application data which
/// must not outlive the backup that needed it. Disposing releases it.
/// </para>
/// </summary>
public sealed class TargetLease : IAsyncDisposable
{
    private readonly Func<ValueTask>? _release;

    private TargetLease(bool succeeded, string? sourcePath, string? error, Func<ValueTask>? release)
    {
        Succeeded = succeeded;
        SourcePath = sourcePath;
        Error = error;
        _release = release;
    }

    public bool Succeeded { get; }
    public string? SourcePath { get; }
    public string? Error { get; }

    public static TargetLease Ok(string sourcePath, Func<ValueTask>? release = null) =>
        new(true, sourcePath, null, release);

    public static TargetLease Fail(string error) => new(false, null, error, null);

    public async ValueTask DisposeAsync()
    {
        if (_release is not null) await _release();
    }
}

/// <summary>
/// Turns a policy's target into something the engine can read.
///
/// <para>
/// The one place that decides what a backup is allowed to read. A backup engine given an arbitrary
/// path is an arbitrary-file-read primitive with a download button attached, so the answer is
/// constrained here rather than at each call site.
/// </para>
/// </summary>
public interface IBackupTargetResolver
{
    /// <summary>
    /// Queue-time check. Does no work and creates nothing, so a mistyped target is a message on the
    /// screen the user is looking at rather than a failed job they have to go and find.
    /// </summary>
    ResolvedTarget Validate(BackupTargetType targetType, string targetRef);

    /// <summary>
    /// Run-time acquisition. May stage data; always dispose the lease.
    ///
    /// <para>
    /// <paramref name="snapshotId"/> names the staged copy (see <see cref="BackupStagingLayout"/>).
    /// It is a parameter rather than a private Guid because a copy that nothing can name is a copy
    /// nothing can clean up: the row is not written until this method returns, so a kill during the
    /// copy used to leave plaintext application data on disk under a name that appeared in no row
    /// anywhere. Deriving it from the snapshot makes the leftovers findable before the copy starts.
    /// </para>
    /// </summary>
    Task<TargetLease> AcquireAsync(
        BackupTargetType targetType, string targetRef, Guid snapshotId, CancellationToken ct);
}

/// <inheritdoc />
public sealed class BackupTargetResolver(
    IDockerEngine docker,
    IDatabaseTargetStager databases,
    IApplicationTargetStager applications,
    IOptions<BackupModuleOptions> options,
    ILogger<BackupTargetResolver> logger) : IBackupTargetResolver
{
    private readonly BackupModuleOptions _options = options.Value;

    public ResolvedTarget Validate(BackupTargetType targetType, string targetRef)
    {
        if (string.IsNullOrWhiteSpace(targetRef))
            return new ResolvedTarget(false, Error: "This policy has no target.");

        return targetType switch
        {
            BackupTargetType.Directory => ValidateDirectory(targetRef),
            BackupTargetType.DockerVolume => ValidateVolume(targetRef),
            BackupTargetType.Database => ValidateDatabase(targetRef),
            BackupTargetType.Application => ValidateApplication(targetRef),

            _ => new ResolvedTarget(false, Error: $"{targetType} is not a target this module can read.")
        };
    }

    public async Task<TargetLease> AcquireAsync(
        BackupTargetType targetType, string targetRef, Guid snapshotId, CancellationToken ct)
    {
        var validation = Validate(targetType, targetRef);
        if (!validation.Succeeded) return TargetLease.Fail(validation.Error!);

        return targetType switch
        {
            BackupTargetType.Directory => TargetLease.Ok(validation.SourcePath!),
            BackupTargetType.DockerVolume => await StageVolumeAsync(targetRef, snapshotId, ct),
            BackupTargetType.Database => await databases.StageAsync(Guid.Parse(targetRef), snapshotId, ct),
            BackupTargetType.Application => await applications.StageAsync(Guid.Parse(targetRef), snapshotId, ct),
            _ => TargetLease.Fail($"{targetType} is not a target this module can read.")
        };
    }

    /// <summary>
    /// A directory target must sit inside a configured root.
    ///
    /// <para>
    /// Fails closed: with no roots configured, no directory can be backed up. The alternative
    /// default — any absolute path — would mean that enabling the feature quietly grants the ability
    /// to read <c>/etc</c>, or the panel's own data directory including its master key, and to
    /// download the result.
    /// </para>
    /// </summary>
    private ResolvedTarget ValidateDirectory(string path)
    {
        if (_options.AllowedSourceRoots.Count == 0)
            return new ResolvedTarget(false, Error:
                "No backup source directories are configured. Set Backups:Module:AllowedSourceRoots " +
                "to the directories this panel may read before creating a directory policy.");

        foreach (var root in _options.AllowedSourceRoots)
        {
            var check = PathGuard.ResolveWithin(root, path);
            if (!check.Allowed) continue;

            return Directory.Exists(check.ResolvedPath)
                ? new ResolvedTarget(true, check.ResolvedPath)
                : new ResolvedTarget(false, Error: $"There is no directory at {check.ResolvedPath}.");
        }

        return new ResolvedTarget(false, Error:
            $"'{path}' is not inside any configured backup source directory.");
    }

    /// <summary>
    /// A volume name is checked against the daemon's own naming rule, and nothing else.
    ///
    /// <para>
    /// There is no allowlist for volumes as there is for directories, because a Docker volume is
    /// already scoped to this platform's own workloads — unlike a path, which can be anything on the
    /// host. The name still has to be a name: it becomes an argument to a container runtime.
    /// </para>
    /// </summary>
    private static ResolvedTarget ValidateVolume(string volumeName) =>
        EngineArgumentGuard.IsSafeVolumeName(volumeName)
            ? new ResolvedTarget(true, volumeName)
            : new ResolvedTarget(false, Error: $"'{volumeName}' is not a valid Docker volume name.");

    /// <summary>
    /// A database target is a managed-service id.
    ///
    /// <para>
    /// Only the shape is checked here. Whether the service exists, which engine it runs and whether
    /// its password still decrypts are all questions that need the database and the master key, and
    /// this method is the one that must stay free of side effects — so they are answered when the
    /// lease is acquired on the worker.
    /// </para>
    /// </summary>
    private static ResolvedTarget ValidateDatabase(string serviceId) =>
        Guid.TryParse(serviceId, out _)
            ? new ResolvedTarget(true, serviceId)
            : new ResolvedTarget(false, Error: "A database target must be a managed database's id.");

    /// <summary>
    /// An application target is an app id. Shape only here, for the same reason as a database: the
    /// app's volumes and definition need the database, and this method must stay side-effect free.
    /// </summary>
    private static ResolvedTarget ValidateApplication(string appId) =>
        Guid.TryParse(appId, out _)
            ? new ResolvedTarget(true, appId)
            : new ResolvedTarget(false, Error: "An application target must be an application's id.");

    /// <summary>
    /// Copies a volume's contents into the staging area so the engine can read them as a directory.
    ///
    /// <para>
    /// The panel runs in a container and cannot see a volume's host path, so the data is brought to
    /// it by a helper that mounts the volume read-only and the shared staging volume read-write.
    /// This is the same mechanism the platform's existing backup engine uses, and it costs a full
    /// temporary copy of the volume on disk — worth knowing before scheduling a 200 GB volume.
    /// </para>
    /// <para>
    /// <c>cp</c> is invoked directly with an argument list, NOT through <c>sh -c</c>. The platform's
    /// older helper uses a shell string; this module does not, so a volume name is never in a
    /// position to be read as syntax (THREAT_MODEL T1).
    /// </para>
    /// </summary>
    private async Task<TargetLease> StageVolumeAsync(
        string volumeName, Guid snapshotId, CancellationToken ct)
    {
        // Named by the SNAPSHOT's Guid, so nothing user-supplied reaches the path or the container
        // argument AND the reconciler can find this directory from the row before the copy that
        // fills it has returned. See BackupStagingLayout for why that second property matters.
        var stageName = BackupStagingLayout.VolumeDirectory(snapshotId);
        var stagePath = Path.Combine(_options.StagingDirectory, stageName);

        try
        {
            // A deterministic name means a retry of the same snapshot lands where the attempt that
            // crashed was writing. Clear it first: half a copy folded into the new archive would be
            // a backup that restores a mixture of two moments, and nothing would say so.
            //
            // What makes deleting it safe is that one execution per snapshot gets this far:
            // BackupSnapshotService.RunAsync refuses a snapshot already Preparing or Running, so
            // this directory is not one a live run is filling.
            //
            // That is an ORDERING argument, not a lock. RunAsync reads the row, tests its status,
            // and only then writes Preparing, with no concurrency token across the gap — two
            // executions interleaved inside it would both pass. Nothing produces them today: there
            // is one job row per snapshot id, and the worker reserves the target in process before
            // it stamps its claim. Named rather than left implied, because this comment is what
            // licenses a recursive delete.
            Cleanup(stagePath);
            Directory.CreateDirectory(stagePath);

            var exit = await docker.RunOneOffAsync(new DockerOneOffRequest(
                    _options.HelperImage,
                    // "/data/." copies the contents rather than the directory itself, and -a keeps
                    // permissions and timestamps so a restore puts back what was there.
                    ["cp", "-a", "/data/.", $"/backup/{stageName}"],
                    [
                        (volumeName, "/data", true),
                        (_options.StagingVolume, "/backup", false)
                    ]),
                new Progress<string>(line => logger.LogDebug("volume staging: {Line}", line)),
                ct);

            if (exit != 0)
            {
                Cleanup(stagePath);
                return TargetLease.Fail(
                    $"The volume '{volumeName}' could not be read (helper exited {exit}).");
            }

            // The helper writes into the staging volume BY NAME while the panel reads it through a
            // mount. If those resolve to different volumes the copy reports success and lands
            // somewhere the panel can never read — say so, rather than backing up an empty folder.
            if (!Directory.Exists(stagePath))
            {
                return TargetLease.Fail(
                    $"The copy reported success but nothing arrived at {stagePath}. The helper mounts " +
                    $"the volume '{_options.StagingVolume}' while the panel reads " +
                    $"{_options.StagingDirectory}; check both resolve to the SAME docker volume.");
            }

            return TargetLease.Ok(stagePath, () =>
            {
                // A staged copy is plaintext application data. It goes as soon as the snapshot that
                // needed it is finished, successfully or not.
                Cleanup(stagePath);
                return ValueTask.CompletedTask;
            });
        }
        catch (Exception ex)
        {
            Cleanup(stagePath);
            logger.LogError(ex, "Staging volume {Volume} failed.", volumeName);
            return TargetLease.Fail($"The volume '{volumeName}' could not be staged: {ex.Message}");
        }
    }

    private void Cleanup(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            // Logged loudly: a staged copy left behind is a plaintext copy of application data
            // sitting on disk, which is worth someone noticing.
            logger.LogWarning(ex, "A staged volume copy could not be removed from {Path}.", path);
        }
    }
}
