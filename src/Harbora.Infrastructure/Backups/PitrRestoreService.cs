using System.Globalization;
using System.Security.Cryptography;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Backups;

/// <summary>
/// Restores a PostgreSQL instance to a point in time (3.1, round-2 market-gaps plan) — the base
/// backup + WAL replay this whole feature exists for, landing in a NEW logical database by default
/// (the clone-to-staging shape D1/D2, 2026-08-25 shared-databases plan, established) rather than
/// touching anything currently serving traffic.
///
/// <para>
/// <b>Honesty about what this proves.</b> Every refusal, the base-backup/window selection, the
/// destination logic (new database by default; typed confirmation naming attached apps to overwrite)
/// and the ORDER the docker calls happen in are real and are what <c>PitrRestoreServiceTests</c>
/// exercises against <c>FakeDockerEngine</c>. Whether the actual WAL replay inside
/// <see cref="RecoverAndDumpAsync"/> produces a correct PostgreSQL recovery is NOT provable on this
/// development machine — there is no Docker and no live PostgreSQL here. That step is written as
/// real orchestration (extract the base backup, stage the WAL segments recovery needs, start the
/// instance with <c>recovery_target_time</c>, wait for it to promote, dump it, load the dump into the
/// destination) rather than a stub, but it needs a live host to confirm.
/// </para>
/// </summary>
public sealed class PitrRestoreService(
    HarboraDbContext db,
    IServerEngineFactory engines,
    IBackupStorage storage,
    ISecretProtector protector,
    WalArchivingService recovery,
    LogicalDatabaseService logicalDatabases,
    ISystemClock clock,
    IOptions<BackupOptions> options,
    IOptions<Deployments.HarboraRuntimeOptions> runtime,
    ILogger<PitrRestoreService> logger)
{
    private readonly BackupOptions _opt = options.Value;
    private readonly Deployments.HarboraRuntimeOptions _runtime = runtime.Value;

    /// <summary>How long the recovered scratch instance is given to replay WAL and promote before
    /// this gives up and reports the restore failed. Generous: replay speed depends on how much WAL
    /// there is between the base backup and the target time, which this has no way to estimate ahead
    /// of time.</summary>
    public TimeSpan ReadinessTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Overridable only by tests, so a readiness loop does not really sleep for real minutes
    /// while proving its own ordering and retry count.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>What overwriting an existing logical database would touch — computed and handed back
    /// on its own, before anything destructive is even asked for, so a caller that does not know can
    /// find out from a refusal alone (the task's own requirement: the confirmation must name which
    /// apps are attached).</summary>
    public sealed record OverwriteImpact(string TargetName, IReadOnlyList<string> AttachedApps);

    public async Task<OverwriteImpact?> DescribeOverwriteAsync(Guid targetDatabaseId, CancellationToken ct)
    {
        var target = await db.ManagedServiceDatabases.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == targetDatabaseId, ct);
        if (target is null) return null;

        var apps = await db.AppManagedServices.AsNoTracking()
            .Where(a => a.ManagedServiceDatabaseId == targetDatabaseId)
            .Select(a => a.App!.Name)
            .ToListAsync(ct);

        return new OverwriteImpact(target.Name, apps);
    }

    /// <summary>
    /// Restores <paramref name="managedServiceId"/> to <paramref name="targetTime"/>.
    ///
    /// <paramref name="overwriteDatabaseId"/> null (the default) creates a brand-new logical database
    /// on the same instance and loads the recovered data there — nothing existing is touched. A value
    /// names an EXISTING logical database to overwrite instead, and is refused unless
    /// <paramref name="typedConfirmation"/> matches that database's own name exactly
    /// (<see cref="ServiceRemovalPlan.IsConfirmed"/>, the same typed-name idiom
    /// <c>DatabasesController.Remove</c>/<c>RemoveDatabase</c> already use) — the refusal itself always
    /// names which apps are attached, via <see cref="DescribeOverwriteAsync"/>, because the person
    /// restoring may not know.
    /// </summary>
    public async Task<(bool Ok, string? Error, Guid? DatabaseId)> RestoreToTimestampAsync(
        Guid managedServiceId, DateTimeOffset targetTime, Guid? overwriteDatabaseId,
        string? typedConfirmation, CancellationToken ct)
    {
        var svc = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == managedServiceId, ct);
        if (svc is null) return (false, "That database instance no longer exists.", null);

        // Refused by name before any row or any docker call — the same "never a Postgres-shaped
        // command against an engine that cannot honour it" rule PitrSupport exists to enforce
        // everywhere else in this feature.
        if (!PitrSupport.Supports(svc.Type)) return (false, PitrSupport.UnsupportedReason(svc.Type), null);

        var window = await recovery.RecoveryWindowAsync(managedServiceId, clock.UtcNow, ct);
        if (!window.HasRecoverableWindow)
            return (false, $"There is nothing to restore yet: {window.Message}", null);

        if (targetTime < window.EarliestPoint!.Value || targetTime > window.LatestPoint!.Value)
            return (false,
                $"{Iso(targetTime)} is outside the recoverable window ({Iso(window.EarliestPoint.Value)} to " +
                $"{Iso(window.LatestPoint.Value)}). " +
                (window.Status == PitrStatus.Degraded
                    ? "Archiving has been failing, which is why the window stops there rather than at now."
                    : "Choose a time inside that window."),
                null);

        var baseBackup = await db.Backups.Include(b => b.Destination).AsNoTracking()
            .Where(b => b.WorkspaceId == svc.WorkspaceId && b.Type == BackupType.PostgresBaseBackup
                        && b.TargetRef == svc.Id.ToString() && b.Status == BackupStatus.Completed
                        && b.FinishedAt <= targetTime)
            .OrderByDescending(b => b.FinishedAt)
            .FirstOrDefaultAsync(ct);
        if (baseBackup is null)
            return (false,
                "No base backup exists at or before that time, so there is nothing to replay WAL onto.", null);

        Guid destinationDatabaseId;
        string destinationUsername;
        string destinationEncryptedPassword;
        string destinationDbName;

        if (overwriteDatabaseId is null)
        {
            var stamp = targetTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            var (created, error) = await logicalDatabases.CreateAsync(svc.Id, $"pitr-{stamp}", ct);
            if (created is null) return (false, error ?? "The new database could not be created.", null);

            destinationDatabaseId = created.Id;
            destinationUsername = created.Username;
            destinationEncryptedPassword = created.EncryptedPassword;
            destinationDbName = created.Name;
        }
        else
        {
            var impact = await DescribeOverwriteAsync(overwriteDatabaseId.Value, ct);
            if (impact is null) return (false, "The database to overwrite no longer exists.", null);

            if (!ServiceRemovalPlan.IsConfirmed(true, typedConfirmation, impact.TargetName))
            {
                var appsText = impact.AttachedApps.Count == 0
                    ? "no apps are currently attached to it"
                    : $"{impact.AttachedApps.Count} app(s) are attached to it: {string.Join(", ", impact.AttachedApps)}";
                return (false,
                    $"Overwriting '{impact.TargetName}' replaces everything currently in it — {appsText}. " +
                    $"Type its name exactly to confirm: {impact.TargetName}", null);
            }

            var target = await db.ManagedServiceDatabases.AsNoTracking()
                .FirstAsync(d => d.Id == overwriteDatabaseId.Value, ct);
            destinationDatabaseId = target.Id;
            destinationUsername = target.Username;
            destinationEncryptedPassword = target.EncryptedPassword;
            destinationDbName = target.Name;
        }

        try
        {
            var dumpPath = await RecoverAndDumpAsync(svc, baseBackup, targetTime, ct);
            await LoadIntoDestinationAsync(
                svc, destinationUsername, destinationEncryptedPassword, destinationDbName, dumpPath, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Point-in-time restore of {Svc} to {Target} failed.", svc.Name, targetTime);
            return (false, $"The restore failed: {ex.Message}", null);
        }

        logger.LogInformation(
            "Restored {Svc} to {Target} into logical database {Db} (id {Id}).",
            svc.Name, targetTime, destinationDbName, destinationDatabaseId);
        return (true, null, destinationDatabaseId);
    }

    /// <summary>
    /// The physical part: extracts the chosen base backup into a throwaway data volume, stages every
    /// WAL segment recovery might ask for, starts a throwaway PostgreSQL instance against it with
    /// <c>recovery_target_time</c> set, waits for it to replay forward and promote, then dumps it —
    /// exactly the way an ordinary logical backup is taken (<see cref="DatabaseDumpPlan.For"/>, reused
    /// rather than reinvented) except the source is a moment in the past instead of the live server.
    /// The scratch container and volume are removed whichever way this ends, in <c>finally</c> — the
    /// same guarantee <c>RestoreRehearsal</c>'s own scratch database already gives.
    /// </summary>
    private async Task<string> RecoverAndDumpAsync(
        ManagedService svc, Domain.Backups.Backup baseBackup, DateTimeOffset targetTime, CancellationToken ct)
    {
        var docker = await engines.ResolveAsync(svc.ServerId, ct);
        if (!ReferenceEquals(docker, engines.Local))
            throw new InvalidOperationException(
                $"'{svc.Name}' runs on a server other than this panel. Point-in-time recovery needs the " +
                "base backup and its WAL segments in this panel's own staging directory, the same " +
                "constraint an ordinary database restore already has — carrying them to another server " +
                "needs a transport Harbora does not have yet.");

        var definition = ServiceCatalog.All[svc.Type];
        var image = $"{definition.ImageRepo}:{svc.Version}";
        var wsSlug = await db.Workspaces.Where(w => w.Id == svc.WorkspaceId).Select(w => w.Slug).FirstAsync(ct);
        var environmentNetwork = await Networking.EnvironmentNetworkResolver.ForAsync(db, svc.EnvironmentId, ct);
        var network = Networking.NetworkPlan.Primary(environmentNetwork, _runtime.WorkspaceNetwork(wsSlug));

        var scratchId = Guid.CreateVersion7().ToString("N")[..12];
        var scratchContainer = $"harbora-pitr-{scratchId}";
        var scratchVolume = $"{scratchContainer}-data";
        var walDir = $"pitr-wal-{scratchId}";
        Directory.CreateDirectory(Path.Combine(_opt.StagingDir, walDir));

        try
        {
            // The base backup, fetched and checked exactly like an ordinary restore checks its own
            // artifact before anything is touched — a corrupt base backup must fail loudly here, not
            // three steps into a recovery nobody can then trust.
            var localBaseBackup = await storage.GetToLocalAsync(baseBackup.Destination!, baseBackup.ArtifactPath!, ct);
            if (!string.IsNullOrWhiteSpace(baseBackup.Checksum))
            {
                var actual = await Sha256Async(localBaseBackup, ct);
                if (!string.Equals(actual, baseBackup.Checksum, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "The base backup does not match its recorded checksum — it is corrupt or was " +
                        "modified. Nothing was touched.");
            }
            var baseBackupFileName = Path.GetFileName(localBaseBackup);
            var stagedBaseBackup = Path.Combine(_opt.StagingDir, baseBackupFileName);
            if (!string.Equals(Path.GetFullPath(localBaseBackup), Path.GetFullPath(stagedBaseBackup), StringComparison.OrdinalIgnoreCase))
                File.Copy(localBaseBackup, stagedBaseBackup, overwrite: true);

            // Every segment shipped at or before the target time. Postgres decides at replay time
            // which of these it actually asks restore_command for by name (%f) — this only has to
            // make them available, never guess which ones matter, which is why nothing here invents
            // or reasons about an LSN or a timeline id.
            var segments = await db.WalSegments.AsNoTracking()
                .Where(w => w.WorkspaceId == svc.WorkspaceId && w.ManagedServiceId == svc.Id && w.ArchivedAt <= targetTime)
                .ToListAsync(ct);
            foreach (var segment in segments)
            {
                var local = await storage.GetToLocalAsync(segment.Destination!, segment.ArtifactPath, ct, segment.FileName);
                var staged = Path.Combine(_opt.StagingDir, walDir, segment.FileName);
                if (!string.Equals(Path.GetFullPath(local), Path.GetFullPath(staged), StringComparison.OrdinalIgnoreCase))
                    File.Copy(local, staged, overwrite: true);
            }

            await docker.EnsureVolumeAsync(scratchVolume, ct);

            // Extract the base backup into the scratch volume and write the recovery config PostgreSQL
            // reads on its next start — recovery.signal is what puts it into archive recovery at all
            // (PostgreSQL 12+); recovery_target_time is where it stops; recovery_target_action=promote
            // is what turns it back into an ordinary read-write server the moment it gets there, with
            // no second command from Harbora needed.
            var prepCommand =
                $"set -e; tar xzf /backup/{Sh(baseBackupFileName)} -C {Sh(definition.DataMountPath)}; " +
                $"touch {Sh(definition.DataMountPath)}/recovery.signal; " +
                $"printf \"restore_command = 'cp /backup/{Sh(walDir)}/%%f %%p'\\nrecovery_target_time = '{Iso(targetTime)}'\\nrecovery_target_action = 'promote'\\n\" " +
                $">> {Sh(definition.DataMountPath)}/postgresql.auto.conf";

            var prep = await docker.RunOneOffAsync(new DockerOneOffRequest(
                image, ["sh", "-c", prepCommand],
                [(_opt.StagingVolume, "/backup", true), (scratchVolume, definition.DataMountPath, false)]),
                null, ct);
            if (prep != 0)
                throw new InvalidOperationException(
                    $"The base backup could not be extracted or the recovery settings could not be written (exit {prep}).");

            await docker.RunContainerAsync(new DockerRunRequest(
                image, scratchContainer, network,
                new Dictionary<string, string>(),
                new Dictionary<string, string> { ["harbora.pitr.scratch"] = "true" },
                [(scratchVolume, definition.DataMountPath, false), (_opt.StagingVolume, "/backup", true)],
                definition.Port, 0, 0, null), ct);

            await WaitForPromotionAsync(docker, scratchContainer, network, image, ct);

            var creds = new ServiceCreds(scratchContainer, definition.Port, svc.Username, RevealPassword(svc.EncryptedPassword), svc.DatabaseName);
            var dumpKey = $"pitr-recovered-{scratchId}";
            var plan = DatabaseDumpPlan.For(ManagedServiceType.PostgreSql, creds, $"/backup/{dumpKey}")!;
            dumpKey += plan.FileExtension;
            plan = DatabaseDumpPlan.For(ManagedServiceType.PostgreSql, creds, $"/backup/{dumpKey}")!;

            var output = new System.Text.StringBuilder();
            var dumped = await docker.RunOneOffAsync(new DockerOneOffRequest(
                image, plan.Command, [(_opt.StagingVolume, "/backup", false)], Env: plan.Env, NetworkMode: network),
                new Deployments.InlineProgress<string>(line => { lock (output) output.AppendLine(line); }), ct);
            if (dumped != 0)
                throw new InvalidOperationException(
                    $"Dumping the recovered instance failed (exit {dumped}). {Deployments.LogText.Clean(output.ToString()).Trim()}");

            var dumpPath = Path.Combine(_opt.StagingDir, dumpKey);
            if (!File.Exists(dumpPath))
                throw new InvalidOperationException(
                    $"The recovered instance reported a completed dump but no file arrived at {dumpPath}.");

            return dumpPath;
        }
        finally
        {
            // Removed whatever happened — a failed recovery must not leave a scratch instance running
            // (and billed-for-nothing disk held) on the server, the same guarantee RestoreRehearsal's
            // own scratch database gives via its Drop step.
            try
            {
                var containers = await docker.ListContainersAsync("harbora.pitr.scratch", ct);
                var found = containers.FirstOrDefault(c => c.Name == scratchContainer);
                if (found is not null)
                {
                    await docker.StopContainerAsync(found.Id, ct);
                    await docker.RemoveContainerAsync(found.Id, force: true, ct);
                }
            }
            catch (Exception ex) { logger.LogWarning(ex, "Could not remove the PITR scratch container {Name}.", scratchContainer); }

            try { await docker.RemoveVolumeAsync(scratchVolume, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not remove the PITR scratch volume {Name}.", scratchVolume); }

            try { Directory.Delete(Path.Combine(_opt.StagingDir, walDir), recursive: true); }
            catch { /* best effort, as everywhere else staging is cleaned up in this codebase */ }
        }
    }

    /// <summary>
    /// Polls with <c>pg_isready</c> until the recovered instance promotes to a normal read-write
    /// server, or <see cref="ReadinessTimeout"/> runs out. A server still replaying WAL refuses
    /// connections, so readiness IS the signal that replay reached <c>recovery_target_time</c> and
    /// promoted — no second check is needed.
    /// </summary>
    private async Task WaitForPromotionAsync(
        IDockerEngine docker, string containerName, string network, string image, CancellationToken ct)
    {
        var deadline = clock.UtcNow + ReadinessTimeout;
        while (true)
        {
            var exit = await docker.RunOneOffAsync(new DockerOneOffRequest(
                image, ["pg_isready", "-h", containerName], [], NetworkMode: network), null, ct);
            if (exit == 0) return;

            if (clock.UtcNow >= deadline)
                throw new InvalidOperationException(
                    $"The recovered instance did not finish replaying WAL and promote within " +
                    $"{ReadinessTimeout.TotalMinutes:0} minute(s).");

            await Task.Delay(PollInterval, ct);
        }
    }

    private async Task LoadIntoDestinationAsync(
        ManagedService svc, string username, string encryptedPassword, string database, string dumpPath, CancellationToken ct)
    {
        var docker = await engines.ResolveAsync(svc.ServerId, ct);
        var definition = ServiceCatalog.All[svc.Type];
        var image = $"{definition.ImageRepo}:{svc.Version}";
        var wsSlug = await db.Workspaces.Where(w => w.Id == svc.WorkspaceId).Select(w => w.Slug).FirstAsync(ct);
        var environmentNetwork = await Networking.EnvironmentNetworkResolver.ForAsync(db, svc.EnvironmentId, ct);
        var network = Networking.NetworkPlan.Primary(environmentNetwork, _runtime.WorkspaceNetwork(wsSlug));

        var creds = new ServiceCreds(svc.ContainerName, definition.Port, username, RevealPassword(encryptedPassword), database);
        var plan = DatabaseDumpPlan.RestoreFor(ManagedServiceType.PostgreSql, creds, $"/backup/{Path.GetFileName(dumpPath)}")!;

        var stagedCopy = Path.Combine(_opt.StagingDir, Path.GetFileName(dumpPath));
        if (!string.Equals(Path.GetFullPath(dumpPath), Path.GetFullPath(stagedCopy), StringComparison.OrdinalIgnoreCase))
            File.Copy(dumpPath, stagedCopy, overwrite: true);

        var output = new System.Text.StringBuilder();
        var exit = await docker.RunOneOffAsync(new DockerOneOffRequest(
            image, plan.Command, [(_opt.StagingVolume, "/backup", true)], Env: plan.Env, NetworkMode: network),
            new Deployments.InlineProgress<string>(line => { lock (output) output.AppendLine(line); }), ct);

        if (exit != 0)
            throw new InvalidOperationException(
                $"Loading the recovered data into '{database}' failed (exit {exit}). " +
                Deployments.LogText.Clean(output.ToString()).Trim());
    }

    private string RevealPassword(string encrypted)
    {
        try { return protector.Unprotect(encrypted); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "A stored password for this restore could not be decrypted. The master key most likely " +
                "changed since it was created.", ex);
        }
    }

    private static string Iso(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    private static string Sh(string value) => value.Replace("'", "'\\''");

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
