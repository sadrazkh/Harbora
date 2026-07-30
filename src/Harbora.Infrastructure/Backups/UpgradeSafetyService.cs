using Harbora.Application.Abstractions;
using Harbora.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Harbora.Infrastructure.Backups;

/// <summary>
/// Takes a restore point before an upgrade migrates the database.
///
/// `harbora update` pulls new code, rebuilds, and the panel applies migrations on boot — with nothing
/// captured beforehand. Additive migrations are harmless, but a destructive one, or a new version that
/// turns out to be broken, left no way back to the data as it was. This closes that: an upgrade of an
/// existing install dumps the database first, and refuses to migrate if the dump cannot be taken.
///
/// Refusing is the deliberate choice. The alternative — migrate anyway and log a warning — spends the
/// one moment the restore point exists to be taken. A panel that declines to start is recoverable:
/// the previous image and the data are both still there, and `harbora doctor` explains it. A schema
/// migrated with no way back is not.
/// </summary>
public sealed class UpgradeSafetyService(
    HarboraDbContext db,
    IDockerEngine docker,
    IOptions<BackupOptions> backupOptions,
    IOptions<Deployments.HarboraRuntimeOptions> runtimeOptions,
    ISystemClock clock,
    ILogger<UpgradeSafetyService> logger)
{
    /// <summary>Set to 1 to migrate without a restore point — for a host where the dump cannot work.</summary>
    public const string SkipEnvVar = "HARBORA_SKIP_UPGRADE_BACKUP";

    /// <summary>How many restore points to keep. Each is a full dump, so this is disk, not history.</summary>
    private const int Keep = 5;

    private readonly BackupOptions _backup = backupOptions.Value;
    private readonly Deployments.HarboraRuntimeOptions _runtime = runtimeOptions.Value;

    /// <summary>
    /// Called before migrations run. Returns the artifact path when one was taken, or null when none
    /// was needed. Throws when a restore point was needed and could not be produced.
    /// </summary>
    public async Task<string?> EnsureRestorePointAsync(CancellationToken ct)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).Count();
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).Count();

        if (!UpgradeSafetyPlan.NeedsRestorePoint(pending, applied))
        {
            if (pending > 0)
                logger.LogInformation("First-time install: {Pending} migrations to apply, nothing to back up.", pending);
            return null;
        }

        if (Environment.GetEnvironmentVariable(SkipEnvVar) == "1")
        {
            logger.LogWarning(
                "{Pending} migrations are pending and {Skip}=1, so this upgrade is proceeding with no restore point.",
                pending, SkipEnvVar);
            return null;
        }

        logger.LogInformation("Upgrade detected: {Pending} pending migrations. Taking a restore point first.", pending);

        var name = UpgradeSafetyPlan.FileNameFor(clock.UtcNow);
        var path = Path.Combine(_backup.StagingDir, name);
        var conn = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());

        var exit = await docker.RunOneOffAsync(new DockerOneOffRequest(
            DumpImage(conn),
            UpgradeSafetyPlan.DumpCommand(conn, $"/backup/{name}"),
            [(_backup.StagingVolume, "/backup", false)],
            Env: new Dictionary<string, string> { ["PGPASSWORD"] = conn.Password ?? "" },
            // Share the panel's network namespace so the host in our own connection string resolves.
            NetworkMode: $"container:{_runtime.PanelContainerName}"),
            new Progress<string>(l => logger.LogDebug("pre-upgrade dump: {Line}", l)), ct);

        if (exit != 0)
            throw new InvalidOperationException(
                $"The pre-upgrade database dump failed (exit {exit}), so the upgrade was stopped before " +
                $"migrating. The previous version's data is untouched. Investigate with `harbora doctor`, " +
                $"or set {SkipEnvVar}=1 to upgrade without a restore point.");

        // A dump the panel cannot see is not a restore point. This is the same failure the volume
        // backups hit once: the helper wrote into a differently-named volume and reported success.
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
            throw new InvalidOperationException(
                $"The pre-upgrade dump reported success but produced nothing readable at {path}. " +
                $"Check that the helper's volume '{_backup.StagingVolume}' and the panel's " +
                $"{_backup.StagingDir} are the same docker volume.");

        logger.LogWarning("Restore point written: {Path} ({Size} bytes). Migrating now.", path, info.Length);
        Prune();
        return path;
    }

    /// <summary>
    /// Dump with the same major version the server runs. pg_dump refuses to dump a newer server, so
    /// pinning a fixed image would break silently the day Postgres is upgraded.
    /// </summary>
    private string DumpImage(NpgsqlConnectionStringBuilder conn)
    {
        try
        {
            using var probe = new NpgsqlConnection(conn.ConnectionString);
            probe.Open();
            var major = probe.PostgreSqlVersion.Major;
            return $"postgres:{major}-alpine";
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read the server version; using the default dump image.");
            return "postgres:16-alpine";
        }
    }

    private void Prune()
    {
        try
        {
            var names = Directory.EnumerateFiles(_backup.StagingDir)
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .Select(n => n!);

            foreach (var stale in UpgradeSafetyPlan.DumpsToPrune(names, Keep))
            {
                File.Delete(Path.Combine(_backup.StagingDir, stale));
                logger.LogInformation("Removed old restore point {Name}.", stale);
            }
        }
        catch (Exception ex)
        {
            // Housekeeping must never be the reason an upgrade stops.
            logger.LogWarning(ex, "Could not prune old restore points.");
        }
    }
}
