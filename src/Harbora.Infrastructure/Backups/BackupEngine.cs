using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Domain.Notifications;
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
///
/// <para>
/// Every helper container runs on the machine that actually holds the data, resolved through
/// <see cref="IServerEngineFactory"/> — see <see cref="HostForAsync"/>. This class used to hold the
/// panel's own <see cref="IDockerEngine"/>, so a backup of a service scheduled on another server
/// archived whichever local volume happened to carry the same name (or an empty one Docker created
/// on the spot) and recorded a successful backup. Nothing discovered that until a restore.
/// </para>
///
/// <para>
/// Resolving the host is only half of it, because the helper and the panel have to share the staging
/// volume. Enrolled v1 nodes use their narrow snapshot verbs and a one-use HTTPS relay back to this
/// panel; storage credentials remain here. The legacy inbound agent still has no artifact transport,
/// so <see cref="RequireCapableHost"/> refuses it before any helper starts.
/// </para>
/// </summary>
public sealed class BackupEngine(
    HarboraDbContext db,
    IServerEngineFactory engines,
    IBackupStorage storage,
    ISecretProtector protector,
    IJobQueue jobs,
    INotificationService notifications,
    Monitoring.IncidentService incidents,
    BackupDeliveryService delivery,
    ISystemClock clock,
    IOptions<BackupOptions> options,
    IOptions<Deployments.HarboraRuntimeOptions> runtime,
    ILogger<BackupEngine> logger,
    ArtifactRelayRegistry? relays = null) : IBackupEngine
{
    private readonly BackupOptions _opt = options.Value;
    private readonly Deployments.HarboraRuntimeOptions _runtime = runtime.Value;
    private readonly ArtifactRelayRegistry _relays = relays ?? new ArtifactRelayRegistry(TimeProvider.System);
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

        // Queued against the TARGET, not against this row. The work is this backup and the runner is
        // handed its id, but what must never have two of these running at once is the thing being
        // backed up: a Backup row is created per run, so two backups of one target are two different
        // TargetIds and the queue would let them run side by side.
        //
        // Two schedules of one target falling due on the same tick, or a manual run racing the
        // scheduler, is ordinary — and the staged filename is derived from the second, so both
        // helper containers write into the same path in the shared staging volume, and both then
        // checksum, upload and record Completed. The archive is two moments of the data interleaved
        // and nothing about it says so. BackupRunIdentity.StampFor is the other half of that fix;
        // this is the half that keeps the two runs from overlapping in the first place.
        //
        // Serialised rather than coalesced, unlike DeploymentEngine. Two deploys of one app want the
        // same thing, so handing the second the first one's id is right. Two backups do not: they are
        // copies of the data at two moments, and the later one is the point. Coalescing would report
        // a manual "back up now" as finished using an archive taken before the operator asked for it,
        // and would fail it along with the run it was folded into.
        await jobs.EnqueueExclusiveAsync(
            Harbora.Domain.Jobs.JobKind.Backup, id,
            exclusiveWith: BackupRunIdentity.ExclusionKeyFor(type, targetRef), workspaceId: workspaceId, ct);

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
            // The time, then this run's own identity — see BackupRunIdentity.StampFor for why the
            // second half exists and why the calendar is pinned.
            var stamp = BackupRunIdentity.StampFor(clock.UtcNow, backup.Id);
            var nodeArtifact = await TryBackupNodeAsync(backup, stamp, ct);
            var (key, stagedPath) = nodeArtifact ?? (backup.Type switch
            {
                BackupType.AppConfig => await BackupAppConfigAsync(backup, stamp, ct),
                BackupType.FullPlatform => await BackupPlatformAsync(backup, stamp, ct),
                BackupType.Database or BackupType.Service => await BackupDatabaseAsync(backup, stamp, ct),
                _ => await BackupVolumeAsync(backup, stamp, ct)
            });

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
            // Not `ct`: the job's deadline is the commonest way a backup reaches this catch — a
            // helper container that never exits is exactly what it exists for — and saving under the
            // token that just fired throws before the row is written. The backup would then read
            // Running for ever, which the Backup Center shows as a target still being protected.
            // Same idiom as JobWorker.SettleAsync and BackupSnapshotService's cancelled path.
            //
            // The incident opens here too, ahead of the save, for the same reason DeploymentPipeline's
            // own failure path does: a failed backup never resolves on its own — the next backup
            // succeeding is a different fact about a different run — so this is the only close it will
            // ever get short of a person acknowledging it or the bounded auto-expiry backstop. Subject
            // is this backup's own id, so a retry that fails again is a second, independent incident.
            await incidents.OpenAsync(backup.WorkspaceId, AlertEvent.BackupFailed, backup.Id.ToString(),
                AlertSeverity.Warning, $"Backup failed: {backup.Type}", ex.Message, clock.UtcNow, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
            var evt = NotificationEventData.Create(AlertEvent.BackupFailed,
                ("TargetRef", $"{backup.Type} · {backup.TargetRef}"), ("Detail", ex.Message));
            await notifications.NotifyAsync(backup.WorkspaceId, evt, AlertSeverity.Warning, ct);
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

    /// <summary>
    /// Asks the database for its contents, rather than copying its files while it is running.
    ///
    /// Tarring a live data directory is not a backup of PostgreSQL or MySQL: the files are being
    /// written to as they are read, so what comes out may be torn — and nothing discovers that until
    /// someone tries to restore it, which is the worst moment to find out. Engines with no logical
    /// dump (Redis, whose own snapshot file is the sensible artifact) still take the volume copy.
    /// </summary>
    private async Task<(string Key, string Path)> BackupDatabaseAsync(Backup backup, string stamp, CancellationToken ct)
    {
        var svc = await db.ManagedServices.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == Guid.Parse(backup.TargetRef), ct);
        if (svc is null) throw new InvalidOperationException("That database no longer exists.");

        var definition = Services.ServiceCatalog.All[svc.Type];
        var creds = new Services.ServiceCreds(
            svc.ContainerName, definition.Port, svc.Username, RevealPassword(svc.EncryptedPassword), svc.DatabaseName);

        var key = $"database-{svc.Name}-{stamp}";
        var plan = DatabaseDumpPlan.For(svc.Type, creds, $"/backup/{key}");
        if (plan is null) return await BackupVolumeAsync(backup, stamp, ct);

        key += plan.FileExtension;
        plan = DatabaseDumpPlan.For(svc.Type, creds, $"/backup/{key}")!;

        // Run the dump from the database's OWN image, so the client tools match the server version —
        // pg_dump refuses to dump a server newer than itself, which is exactly what happens when a
        // fixed helper image is used against a database someone upgraded.
        var image = $"{definition.ImageRepo}:{svc.Version}";
        var wsSlug = await db.Workspaces.Where(w => w.Id == svc.WorkspaceId).Select(w => w.Slug).FirstAsync(ct);

        // On the database's own environment network once it has one, not the workspace network the
        // whole tenant shares — see NetworkForAsync's own comment on ManagedServiceEngine. The
        // workspace network is only reachable here today because of the dual attach every workload
        // still carries; P3 (2026-08-17 app-environment-management design) moves this one-off off it
        // ahead of that attach going away, so a dump does not go quiet the day it does.
        var environmentNetwork = await Networking.EnvironmentNetworkResolver.ForAsync(db, svc.EnvironmentId, ct);
        var network = Networking.NetworkPlan.Primary(environmentNetwork, _runtime.WorkspaceNetwork(wsSlug));

        var docker = RequireCapableHost(await HostForServiceAsync(svc, ct), "be exported");

        var output = new System.Text.StringBuilder();
        var exit = await docker.RunOneOffAsync(new DockerOneOffRequest(
            image,
            plan.Command,
            [(_opt.StagingVolume, "/backup", false)],
            Env: plan.Env,
            NetworkMode: network),
            new Deployments.InlineProgress<string>(line => { lock (output) output.AppendLine(line); }), ct);

        if (exit != 0)
            throw new InvalidOperationException(
                $"The database export failed (exit {exit}). {Deployments.LogText.Clean(output.ToString()).Trim()}");

        var staged = Path.Combine(_opt.StagingDir, key);
        if (!File.Exists(staged))
            throw new InvalidOperationException(
                $"The export reported success but no file arrived at {staged}. The helper mounts the " +
                $"volume '{_opt.StagingVolume}' while the panel reads {_opt.StagingDir}; check that both " +
                "resolve to the SAME docker volume (`docker volume ls`).");

        return (key, staged);
    }

    private async Task<(string Key, string Path)> BackupVolumeAsync(Backup backup, string stamp, CancellationToken ct)
    {
        var (volumeName, label) = await ResolveVolumeAsync(backup.Type, backup.TargetRef, ct);
        var key = $"{backup.Type.ToString().ToLowerInvariant()}-{label}-{stamp}.tgz";

        var docker = RequireCapableHost(
            await HostForAsync(backup.Type, backup.TargetRef, ct), "have a volume snapshot taken");

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

    private async Task<(string Key, string Path)?> TryBackupNodeAsync(
        Backup backup, string stamp, CancellationToken ct)
    {
        if (backup.Type is BackupType.AppConfig or BackupType.FullPlatform) return null;

        var host = await HostForAsync(backup.Type, backup.TargetRef, ct);
        if (host.Docker is not Nodes.NodeWorkloadEngine node) return null;

        var (volumeName, label) = await ResolveVolumeAsync(backup.Type, backup.TargetRef, ct);
        var key = $"{backup.Type.ToString().ToLowerInvariant()}-{label}-{stamp}.tgz";
        var staged = Path.Combine(_opt.StagingDir, key);
        var snapshotId = backup.Id.ToString("n");
        var workloadId = await ContainerForTargetAsync(backup.Type, backup.TargetRef, ct);
        var relay = _relays.CreateUpload(staged);

        try
        {
            var result = await node.SnapshotToPanelAsync(volumeName, workloadId, snapshotId, relay, ct);
            if (!File.Exists(staged))
                throw new InvalidOperationException("The node reported a completed transfer, but no artifact reached the panel.");

            var actual = await Sha256Async(staged, ct);
            if (!actual.Equals(result.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"The relayed artifact checksum is {actual}, but node reported {result.Sha256}.");
            if (result.SizeBytes > 0 && new FileInfo(staged).Length != result.SizeBytes)
                throw new InvalidOperationException("The relayed artifact size differs from the node snapshot.");

            return (key, staged);
        }
        finally
        {
            _relays.Revoke(relay.Id);
        }
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

        // A logical dump is put back by the engine that produced it, not untarred into a data
        // directory. Which one this is comes from the artifact itself, so backups taken before
        // database exports existed still restore the way they were made.
        if (backup.Type is BackupType.Database or BackupType.Service
            && !BackupArtifact.IsVolumeArchive(backup.ArtifactPath))
        {
            await RestoreDatabaseAsync(backup, localPath, ct);
            return;
        }

        // Volume restore: stop the container, wipe + untar the volume, restart.
        var (volumeName, _) = await ResolveVolumeAsync(backup.Type, backup.TargetRef, ct);

        var host = await HostForAsync(backup.Type, backup.TargetRef, ct);
        if (host.Docker is Nodes.NodeWorkloadEngine node)
        {
            var snapshotId = $"restore-{backup.Id:n}";
            var checksum = await Sha256Async(localPath, ct);
            var workloadId = await ContainerForTargetAsync(backup.Type, backup.TargetRef, ct);
            var relay = _relays.CreateDownload(localPath);
            try
            {
                await node.RestoreFromPanelAsync(
                    volumeName, workloadId, snapshotId, checksum, new FileInfo(localPath).Length, relay, ct);
                return;
            }
            finally
            {
                _relays.Revoke(relay.Id);
            }
        }

        // Asked before anything is stopped, and long before anything is wiped. A host that cannot run
        // the helper has to end the restore while the current container is still serving — the
        // refusal is worth nothing if it arrives after the container is down.
        var docker = RequireCapableHost(host, "have a volume restored into it");

        var fileName = Path.GetFileName(localPath);
        var stagedCopy = Path.Combine(_opt.StagingDir, fileName);
        if (!string.Equals(Path.GetFullPath(localPath), Path.GetFullPath(stagedCopy), StringComparison.OrdinalIgnoreCase))
            File.Copy(localPath, stagedCopy, overwrite: true);

        var containerName = await ContainerForTargetAsync(backup.Type, backup.TargetRef, ct);
        if (containerName is not null) await StopIfRunning(docker, containerName, ct);

        // The swap below can undo a failed restore, but not a successful restore of the WRONG backup.
        // This snapshot is what covers that case.
        await SnapshotBeforeRestoreAsync(docker, volumeName, ct);

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
        if (containerName is not null)
            await docker.RestartContainerAsync(await RequireContainerIdAsync(docker, containerName, ct), ct);
    }

    /// <summary>
    /// Puts a logical dump back through the database's own client, into the running database.
    ///
    /// A safety dump is taken first. The volume path can undo a failed restore by swapping
    /// directories back; a logical restore writes into a live database and has no such move, so the
    /// only protection is having the previous contents on disk before it starts.
    /// </summary>
    private async Task RestoreDatabaseAsync(Backup backup, string localPath, CancellationToken ct)
    {
        var svc = await db.ManagedServices.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == Guid.Parse(backup.TargetRef), ct);
        if (svc is null) throw new InvalidOperationException("That database no longer exists.");

        var definition = Services.ServiceCatalog.All[svc.Type];
        var creds = new Services.ServiceCreds(
            svc.ContainerName, definition.Port, svc.Username, RevealPassword(svc.EncryptedPassword), svc.DatabaseName);
        var image = $"{definition.ImageRepo}:{svc.Version}";
        var wsSlug = await db.Workspaces.Where(w => w.Id == svc.WorkspaceId).Select(w => w.Slug).FirstAsync(ct);

        // Same network the dump above reaches this database on — the environment's own once it has
        // one. Both the safety dump below and the restore itself share this value.
        var environmentNetwork = await Networking.EnvironmentNetworkResolver.ForAsync(db, svc.EnvironmentId, ct);
        var network = Networking.NetworkPlan.Primary(environmentNetwork, _runtime.WorkspaceNetwork(wsSlug));

        // Before the safety dump, so a host that cannot take one also cannot start the restore that
        // dump exists to protect.
        var docker = RequireCapableHost(
            await HostForServiceAsync(svc, ct), "have a dump loaded back into it");

        var stamp = clock.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var safetyKey = $"pre-restore-{svc.Name}-{stamp}";
        var safety = DatabaseDumpPlan.For(svc.Type, creds, $"/backup/{safetyKey}");
        if (safety is not null)
        {
            safetyKey += safety.FileExtension;
            safety = DatabaseDumpPlan.For(svc.Type, creds, $"/backup/{safetyKey}")!;
            var safetyExit = await docker.RunOneOffAsync(new DockerOneOffRequest(
                image, safety.Command, [(_opt.StagingVolume, "/backup", false)],
                Env: safety.Env, NetworkMode: network), null, ct);

            if (safetyExit != 0)
                throw new InvalidOperationException(
                    "The database could not be exported before restoring, so the restore was not " +
                    "started — there would have been nothing to go back to.");
            logger.LogInformation("Pre-restore dump of {Name} written to {Key}.", svc.Name, safetyKey);
        }

        // The artifact has to be where the helper can see it: the staging volume, by name.
        var fileName = Path.GetFileName(localPath);
        var stagedCopy = Path.Combine(_opt.StagingDir, fileName);
        if (!string.Equals(Path.GetFullPath(localPath), Path.GetFullPath(stagedCopy), StringComparison.OrdinalIgnoreCase))
            File.Copy(localPath, stagedCopy, overwrite: true);

        var plan = DatabaseDumpPlan.RestoreFor(svc.Type, creds, $"/backup/{fileName}")
                   ?? throw new InvalidOperationException($"{svc.Type} has no restore command.");

        var output = new System.Text.StringBuilder();
        var exit = await docker.RunOneOffAsync(new DockerOneOffRequest(
            image, plan.Command, [(_opt.StagingVolume, "/backup", true)],
            Env: plan.Env, NetworkMode: network),
            new Deployments.InlineProgress<string>(line => { lock (output) output.AppendLine(line); }), ct);

        if (exit != 0)
            throw new InvalidOperationException(
                $"The restore failed (exit {exit}). The database may be partly restored; the export " +
                $"taken just before it is stored as {safetyKey}. " +
                Deployments.LogText.Clean(output.ToString()).Trim());
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

    /// <inheritdoc />
    public async Task DeleteAsync(Guid backupId, CancellationToken ct)
    {
        var backup = await db.Backups.Include(b => b.Destination).FirstOrDefaultAsync(b => b.Id == backupId, ct);
        if (backup is null) return;

        // The artifact first, and the row only if that worked. The row is the only record of where the
        // bytes are, so dropping it first and then failing to reach the destination leaves an artifact
        // nobody can find, name or account for — on a paid destination, one that goes on being charged
        // for. Retention above tolerates that failure because it is a sweeper with more chances; a
        // person who pressed delete is owed the truth about whether it happened.
        if (backup is { Destination: not null, ArtifactPath: not null })
            await storage.DeleteAsync(backup.Destination, backup.ArtifactPath, ct);

        db.Backups.Remove(backup);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Backup {Id} ({Type} of {Target}) was deleted by hand.", backup.Id, backup.Type, backup.TargetRef);
    }

    /// <inheritdoc />
    public async Task<Guid> ImportAsync(
        Guid workspaceId, BackupType type, string targetRef, Guid destinationId,
        string fileName, Stream content, CancellationToken ct)
    {
        var destination = await db.BackupDestinations
            .FirstOrDefaultAsync(d => d.Id == destinationId && d.WorkspaceId == workspaceId, ct)
            ?? throw new InvalidOperationException("The destination does not belong to this workspace.");

        Directory.CreateDirectory(_opt.StagingDir);

        // The uploaded name never becomes a path. It came from a browser and is the one string here
        // somebody else chose, so only its file-name part survives — and that only to keep an extension
        // a restore may read.
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
            throw new InvalidOperationException("The uploaded file has no name.");

        var stamp = BackupRunIdentity.StampFor(clock.UtcNow, Guid.CreateVersion7());
        var key = $"imported-{stamp}-{safeName}";
        var stagedPath = Path.Combine(_opt.StagingDir, key);

        await using (var file = File.Create(stagedPath))
            await content.CopyToAsync(file, ct);

        try
        {
            // Deliberately NOT through ProtectArtifactAsync. A downloaded artifact is already in its
            // stored form, so encrypting it again would wrap it twice and nothing would restore it. The
            // checksum is over the bytes that actually land, which is what verification recomputes.
            var checksum = await Sha256Async(stagedPath, ct);
            var (artifactRef, size) = await storage.PutFileAsync(destination, key, stagedPath, ct);

            var backup = new Backup
            {
                WorkspaceId = workspaceId,
                DestinationId = destinationId,
                Type = type,
                TargetRef = targetRef,
                // Completed because the artifact is stored and downloadable, which is all this status
                // has ever claimed. Whether it would RESTORE is a different question, and the field
                // below leaves it open rather than answering it by having accepted the upload.
                Status = BackupStatus.Completed,
                ArtifactPath = artifactRef,
                SizeBytes = size,
                Checksum = checksum,
                StartedAt = clock.UtcNow,
                FinishedAt = clock.UtcNow,
                // Not scheduled: nothing ran on a timer. Counting an import towards a schedule's
                // retention would let one upload prune the automatic backups it sits beside.
                IsScheduled = false,
                // Null on purpose — "not checked yet", not "checked and fine". Harbora has no idea what
                // is in this file; the panel offers the dry run that finds out.
                VerifiedRestorable = null,
                VerificationNote = "Imported from an uploaded file; never verified."
            };
            db.Backups.Add(backup);
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Backup {Id} was imported from an uploaded file for {Type} of {Target}.",
                backup.Id, type, targetRef);

            return backup.Id;
        }
        finally
        {
            // The staging copy goes whether the publish worked or not: a failed import must not leave
            // somebody's archive sitting in a shared directory.
            if (File.Exists(stagedPath))
                try { File.Delete(stagedPath); } catch { /* best effort, as everywhere else here */ }
        }
    }

    /// <inheritdoc />
    public async Task<string?> TestDestinationAsync(Guid destinationId, CancellationToken ct)
    {
        var destination = await db.BackupDestinations.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == destinationId, ct);
        if (destination is null) return "The destination no longer exists.";

        Directory.CreateDirectory(_opt.StagingDir);

        // A name of its own each time. Two operators testing one destination at the same moment would
        // otherwise write and delete each other's probe, and one of them would be told it failed.
        var key = $"harbora-probe-{Guid.CreateVersion7():N}.txt";
        var probePath = Path.Combine(_opt.StagingDir, key);

        try
        {
            await File.WriteAllTextAsync(
                probePath,
                "Harbora wrote this to check it can reach this destination, then deleted it.\n", ct);

            var (artifactRef, _) = await storage.PutFileAsync(destination, key, probePath, ct);

            // Deleted even though it is a few bytes. A destination that slowly fills with probes is one
            // somebody eventually cleans up by hand — and the delete is half of what is being tested
            // anyway, since a write that cannot be undone is not a working destination.
            await storage.DeleteAsync(destination, artifactRef, ct);
            return null;
        }
        catch (Exception ex)
        {
            // The message, not a verdict of our own. "Could not connect" sends an operator to check a
            // network that is fine, when the real answer was a bucket name with a typo in it.
            logger.LogWarning(ex, "Backup destination {Id} failed its round-trip test.", destinationId);
            return ex.Message;
        }
        finally
        {
            if (File.Exists(probePath))
                try { File.Delete(probePath); } catch { /* best effort */ }
        }
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
    /// <summary>A password that cannot be decrypted fails loudly: a dump attempted with an empty
    /// one produces an authentication error nobody could trace back to a key problem.</summary>
    private string RevealPassword(string encrypted)
    {
        try { return protector.Unprotect(encrypted); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "This database's stored password could not be decrypted, so it cannot be exported. " +
                "The master key most likely changed since it was created.", ex);
        }
    }

    private byte[] ArchiveKey() => protector.DeriveKey("backup-archive");

    public async Task<BackupVerification> VerifyAsync(Guid backupId, CancellationToken ct)
    {
        var verification = await RunVerificationAsync(backupId, ct);
        await RecordVerificationAsync(backupId, verification, ct);
        return verification;
    }

    /// <summary>
    /// Keeps the verdict on the backup itself. "Has anyone confirmed this would restore, and how
    /// long ago" is the question a list of backups cannot otherwise answer — and a year of nightly
    /// backups nobody ever checked is a year of assumption.
    /// </summary>
    private async Task RecordVerificationAsync(Guid backupId, BackupVerification verification, CancellationToken ct)
    {
        var row = await db.Backups.FirstOrDefaultAsync(b => b.Id == backupId, ct);
        if (row is null) return;

        row.VerifiedAt = clock.UtcNow;
        row.VerifiedRestorable = verification.IsRestorable;
        row.VerificationNote = Deployments.LogText.Clean(
            verification.Reason ?? verification.Checks.FirstOrDefault(c => c.Skipped)?.Detail);
        await db.SaveChangesAsync(ct);
    }

    private async Task<BackupVerification> RunVerificationAsync(Guid backupId, CancellationToken ct)
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

            // The question none of the checks above answers: would it restore? A gzip full of SQL
            // that references a missing extension, or was cut short mid-write, passes every one of
            // them and is worthless. So it is restored into a database created for the purpose and
            // dropped afterwards — the live one is never touched.
            var (rehearsed, rehearsalDetail, restorable) = await RehearseRestoreAsync(backup, readable, ct);
            if (rehearsed is not null)
            {
                checks.Add(new BackupCheck("Restores into a scratch database", rehearsed.Value, rehearsalDetail));
                if (!rehearsed.Value)
                    return new BackupVerification(false, rehearsalDetail, size, checks);
            }
            else if (rehearsalDetail is not null)
            {
                // Recorded as skipped rather than failed: a Redis snapshot has no dump to load, and
                // that is not a fault in the backup. Silence would be worse — "not checked" and
                // "checked and fine" must never look the same.
                checks.Add(new BackupCheck("Restore rehearsal", false, rehearsalDetail, Skipped: true));
            }

            return new BackupVerification(restorable, null, size, checks);
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

    /// <summary>
    /// Restores the dump into a throwaway database on the same server and counts what arrived.
    ///
    /// Returns (null, reason, true) when this kind of backup cannot be rehearsed — a Redis snapshot
    /// or a volume tarball — so the screen can say "not checked" instead of implying it passed.
    /// The scratch database is dropped whatever happens, including when the restore fails.
    /// </summary>
    private async Task<(bool? Rehearsed, string? Detail, bool Restorable)> RehearseRestoreAsync(
        Backup backup, string localDumpPath, CancellationToken ct)
    {
        // Nothing to say for a config snapshot or a volume archive: rehearsing a restore is not a
        // concept that applies to them, so the screen shows no line at all rather than a caveat.
        if (backup.Type is not (BackupType.Database or BackupType.Service)
            || BackupArtifact.IsVolumeArchive(backup.ArtifactPath))
            return (null, null, true);

        var svc = await db.ManagedServices.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == Guid.Parse(backup.TargetRef), ct);
        if (svc is null) return (null, "the database this came from no longer exists", true);

        if (RestoreRehearsal.WhyUnsupported(svc.Type) is { } unsupported) return (null, unsupported, true);

        // The scratch database is created on the server the real one runs on. A host that cannot run
        // a one-off container cannot host it, a server that is unreachable cannot either, and neither
        // can a machine that is simply not this one — the dump the rehearsal loads sits in this
        // panel's staging directory, and a scratch database over there has no way to read it. All
        // three are "not checked", which the caller records as skipped. Reporting any of them as a
        // bad archive would condemn a backup that is very likely fine.
        BackupHost host;
        try
        {
            host = await HostForServiceAsync(svc, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, $"the machine holding '{svc.Name}' could not be reached: {ex.Message}", true);
        }

        if (Nodes.NodeWorkloadEngine.NodeBehind(host.Docker) is { } rehearsalNode)
            return (null,
                $"'{svc.Name}' runs on node {rehearsalNode}, which cannot host the throwaway database " +
                "this check restores into", true);

        if (host.Machine is { } elsewhere)
            return (null,
                $"'{svc.Name}' runs on {elsewhere}, and the dump this check loads is in this panel's " +
                "own staging directory — a scratch database over there could not read it", true);

        var docker = host.Docker;
        var definition = Services.ServiceCatalog.All[svc.Type];
        var creds = new Services.ServiceCreds(
            svc.ContainerName, definition.Port, svc.Username, RevealPassword(svc.EncryptedPassword), svc.DatabaseName);

        var fileName = Path.GetFileName(localDumpPath);
        var stagedCopy = Path.Combine(_opt.StagingDir, fileName);
        if (!string.Equals(Path.GetFullPath(localDumpPath), Path.GetFullPath(stagedCopy), StringComparison.OrdinalIgnoreCase))
            File.Copy(localDumpPath, stagedCopy, overwrite: true);

        var plan = RestoreRehearsal.For(svc.Type, creds, $"/backup/{fileName}", backup.Id)!;
        var image = $"{definition.ImageRepo}:{svc.Version}";
        var wsSlug = await db.Workspaces.Where(w => w.Id == svc.WorkspaceId).Select(w => w.Slug).FirstAsync(ct);
        var environmentNetwork = await Networking.EnvironmentNetworkResolver.ForAsync(db, svc.EnvironmentId, ct);
        var network = Networking.NetworkPlan.Primary(environmentNetwork, _runtime.WorkspaceNetwork(wsSlug));

        async Task<(int Exit, string Output)> RunAsync(IReadOnlyList<string> command, bool mountBackups)
        {
            var output = new System.Text.StringBuilder();
            var exit = await docker.RunOneOffAsync(new DockerOneOffRequest(
                image, command,
                mountBackups ? [(_opt.StagingVolume, "/backup", true)] : [],
                Env: plan.Env, NetworkMode: network),
                new Deployments.InlineProgress<string>(l => { lock (output) output.AppendLine(l); }), ct);
            return (exit, Deployments.LogText.Clean(output.ToString()).Trim());
        }

        try
        {
            var created = await RunAsync(plan.Create, mountBackups: false);
            if (created.Exit != 0)
                return (false, $"A scratch database could not be created, so the backup was not rehearsed. {created.Output}", false);

            var restored = await RunAsync(plan.Restore, mountBackups: true);
            if (restored.Exit != 0)
                return (false, $"This backup does not restore. {restored.Output}", false);

            var counted = await RunAsync(plan.Count, mountBackups: false);
            var tables = RestoreRehearsal.ReadCount(counted.Output);
            if (RestoreRehearsal.Explain(tables) is { } problem) return (false, problem, false);

            return (true, $"restored {tables} table(s) into {plan.ScratchDatabase}", true);
        }
        finally
        {
            // Dropped whatever happened, or a failed rehearsal leaves a database behind on the
            // server and the next one collides with it.
            try { await RunAsync(plan.Drop, mountBackups: false); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not drop the scratch database {Name}.", plan.ScratchDatabase); }
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
    private async Task SnapshotBeforeRestoreAsync(IDockerEngine docker, string volumeName, CancellationToken ct)
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

    // --- which machine holds the data ---

    /// <summary>
    /// The engine for the machine that holds a target's data, how to name the target to a person,
    /// and — when it is not this panel — how to name the machine. <see cref="Machine"/> is null
    /// exactly when the work would run on the panel's own daemon.
    /// </summary>
    private sealed record BackupHost(IDockerEngine Docker, string Subject, string? Machine);

    /// <summary>
    /// The machine a backup has to read from, or write back to.
    ///
    /// Everything below runs a helper container beside the data, so getting this wrong does not fail —
    /// it succeeds against the wrong disk. Resolution goes through <see cref="IServerEngineFactory"/>
    /// and keeps its refusals: a server with no agent endpoint and no enrolled node throws rather than
    /// quietly becoming this panel.
    /// </summary>
    private async Task<BackupHost> HostForAsync(BackupType type, string targetRef, CancellationToken ct)
    {
        if (type is BackupType.Database or BackupType.Service)
        {
            var svc = await db.ManagedServices.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == Guid.Parse(targetRef), ct)
                ?? throw new InvalidOperationException("That database no longer exists.");

            return await HostForServiceAsync(svc, ct);
        }

        // A volume target is a bare docker volume name, so the machine holding it is whichever
        // application declares it. Read unfiltered: a backup runs on the background worker, which has
        // no session, and a filtered read would find no owner for every volume on the platform.
        var owner = await db.Volumes.IgnoreQueryFilters()
            .Where(v => v.Name == targetRef)
            .Select(v => new { v.App!.ServerId, v.App!.Name })
            .FirstOrDefaultAsync(ct);

        // Nothing claims it. There is no second place to look — a volume name is not a placement —
        // so this panel's own daemon is the only machine the name can be addressed on. Said out loud
        // rather than assumed, because assuming it is the defect this method exists to end.
        if (owner is null)
        {
            logger.LogInformation(
                "No application declares a volume named '{Volume}', so it is read from this panel's own daemon.",
                targetRef);

            return new BackupHost(engines.Local, $"The volume '{targetRef}'", null);
        }

        return await ResolveHostAsync(owner.ServerId, $"The volume '{targetRef}' of {owner.Name}", ct);
    }

    private Task<BackupHost> HostForServiceAsync(Domain.Services.ManagedService svc, CancellationToken ct) =>
        ResolveHostAsync(svc.ServerId, $"The database '{svc.Name}'", ct);

    /// <summary>
    /// Resolves a server's engine and works out whether it is this panel's own.
    ///
    /// <para>
    /// "Is this machine this one" is decided by reference against <see cref="IServerEngineFactory.Local"/>,
    /// which the interface documents as the very instance <c>ResolveAsync</c> hands back for the local
    /// server. Deciding it again from the <c>Server</c> row here would be a second copy of the
    /// factory's own rule — including its "no row at all means this machine" case — and two copies of
    /// a placement rule drift apart in silence, which is the failure mode this whole task exists to
    /// end. The server's name is looked up only when it is somewhere else, and only so a refusal can
    /// name the machine the way its operator does.
    /// </para>
    /// </summary>
    private async Task<BackupHost> ResolveHostAsync(Guid serverId, string subject, CancellationToken ct)
    {
        var docker = await engines.ResolveAsync(serverId, ct);
        if (ReferenceEquals(docker, engines.Local)) return new BackupHost(docker, subject, null);

        var name = await db.Servers.AsNoTracking()
            .Where(s => s.Id == serverId).Select(s => s.Name).FirstOrDefaultAsync(ct);

        return new BackupHost(docker, subject, string.IsNullOrWhiteSpace(name) ? serverId.ToString() : name);
    }

    /// <summary>
    /// Refuses, in words an operator can act on, when the machine holding the data cannot see this
    /// job through — for either of the two quite different reasons it cannot.
    ///
    /// <para>
    /// <b>Any other machine that is not this one</b> — the older inbound HTTP agent — is the more
    /// dangerous case precisely because it refuses nothing. It runs the helper exactly as asked, and
    /// the helper reads and writes through <see cref="BackupOptions.StagingVolume"/> <em>by name on
    /// its own host</em>, while the panel reads <see cref="BackupOptions.StagingDir"/> here. So a
    /// backup produced a correct archive on a disk nothing here can read, every scheduled tick left
    /// another one there for nobody to collect, and the panel then failed with the staging-volume
    /// message — which sends an operator to <c>docker volume ls</c> on a machine where everything is
    /// exactly as it should be. Refusing in front of that check is what makes its message true again:
    /// it now only ever describes two volumes on one host, which is what it was written about.
    /// </para>
    ///
    /// <para>
    /// The refusal is raised before any helper starts, so it can promise that nothing was left on
    /// the remote host.
    /// </para>
    /// </summary>
    private static IDockerEngine RequireCapableHost(BackupHost host, string work)
    {
        if (host.Machine is { } elsewhere)
            throw new InvalidOperationException(
                $"{host.Subject} runs on {elsewhere}, which cannot {work} yet: the helper container " +
                $"would use the staging volume on {elsewhere} while this panel uses its own, so the " +
                "archive could never be passed between the two. Nothing was read, nothing was written, " +
                $"and nothing was left behind on {elsewhere} — this was refused before the helper " +
                "started. Carrying an archive to or from another server needs a transport Harbora " +
                "does not have yet.");

        return host.Docker;
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

    private static async Task StopIfRunning(IDockerEngine docker, string containerName, CancellationToken ct)
    {
        var containers = await docker.ListContainersAsync("harbora.service", ct);
        var c = containers.FirstOrDefault(x => x.Name == containerName);
        if (c is not null && c.State.Equals("running", StringComparison.OrdinalIgnoreCase))
            await docker.StopContainerAsync(c.Id, ct);
    }

    private static async Task<string> RequireContainerIdAsync(
        IDockerEngine docker, string containerName, CancellationToken ct)
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
