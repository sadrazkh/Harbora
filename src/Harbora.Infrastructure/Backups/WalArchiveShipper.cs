using System.Text;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Backups;

/// <summary>
/// Moves WAL segments off a PostgreSQL instance's own <see cref="PostgresWalArchivingCommand.VolumeNameFor"/>
/// volume into object storage, on its own tick — independent of <see cref="BackupScheduler"/>'s tick
/// and of when a base backup last ran, because a base backup schedule set to nightly must not mean
/// WAL only ships once a night too (3.1, round-2 market-gaps plan).
///
/// <para>
/// This is the single writer of <see cref="WalArchivingStatus.LastSuccessAt"/>, which
/// <see cref="PitrRecoveryWindow"/> reads back as the latest point actually recoverable. A run that
/// fails updates <see cref="WalArchivingStatus.LastAttemptAt"/>/<see cref="WalArchivingStatus.ConsecutiveFailures"/>/
/// <see cref="WalArchivingStatus.LastError"/> and leaves <c>LastSuccessAt</c> exactly where it was —
/// this class is what makes "a failing archive shrinks the reported recoverable window" true, rather
/// than merely documented.
/// </para>
///
/// <para>
/// Runs with no <c>HttpContext</c>, so the DbContext's workspace query filter is off
/// (<c>HttpWorkspaceScope.IsUnscoped</c>) — the same reason <see cref="BackupScheduler"/> reads every
/// workspace's schedules unfiltered. Every cross-table read below still carries an explicit
/// <c>WorkspaceId ==</c> comparison anyway, so this stays correct even if it is ever called from a
/// scope that is NOT unscoped.
/// </para>
/// </summary>
public sealed class WalArchiveShipper(IServiceScopeFactory scopeFactory, ILogger<WalArchiveShipper> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try { await ShipDueInstancesAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "WAL archive shipper tick failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task ShipDueInstancesAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var engines = scope.ServiceProvider.GetRequiredService<IServerEngineFactory>();
        var storage = scope.ServiceProvider.GetRequiredService<IBackupStorage>();
        var clock = scope.ServiceProvider.GetRequiredService<ISystemClock>();
        var opt = scope.ServiceProvider.GetRequiredService<IOptions<BackupOptions>>().Value;

        // Both requested AND actually applied — HasUnpublishedChanges gates this exactly the way
        // ManagedServiceEngine.ProvisionAsync only bakes archive_command into the container's command
        // line on a rebuild; shipping before that has ever happened would find an empty volume every
        // tick and, worse, would look identical to "archiving is healthy but idle".
        var instances = await db.ManagedServices
            .Where(s => s.Type == ManagedServiceType.PostgreSql && s.PitrEnabled && !s.HasUnpublishedChanges)
            .ToListAsync(ct);

        foreach (var svc in instances)
        {
            try { await ShipOneAsync(svc, db, engines, storage, clock, opt, ct); }
            catch (Exception ex) { logger.LogError(ex, "WAL shipping failed for {Svc}.", svc.Name); }
        }
    }

    private async Task ShipOneAsync(
        ManagedService svc, HarboraDbContext db, IServerEngineFactory engines, IBackupStorage storage,
        ISystemClock clock, BackupOptions opt, CancellationToken ct)
    {
        var status = await db.WalArchivingStatuses
            .FirstOrDefaultAsync(w => w.ManagedServiceId == svc.Id && w.WorkspaceId == svc.WorkspaceId, ct);
        if (status is null)
        {
            status = new WalArchivingStatus { WorkspaceId = svc.WorkspaceId, ManagedServiceId = svc.Id };
            db.WalArchivingStatuses.Add(status);
        }
        status.LastAttemptAt = clock.UtcNow;

        IDockerEngine docker;
        try
        {
            docker = await engines.ResolveAsync(svc.ServerId, ct);
        }
        catch (Exception ex)
        {
            Fail(status, $"Could not reach the server holding '{svc.Name}': {ex.Message}");
            await db.SaveChangesAsync(ct);
            return;
        }

        if (!ReferenceEquals(docker, engines.Local))
        {
            Fail(status,
                $"'{svc.Name}' runs on a server other than this panel; WAL shipping needs this panel's " +
                "own staging directory, the same constraint an ordinary database backup already has.");
            await db.SaveChangesAsync(ct);
            return;
        }

        var walVolume = PostgresWalArchivingCommand.VolumeNameFor(svc.VolumeName);

        var listOutput = new StringBuilder();
        var listExit = await docker.RunOneOffAsync(new DockerOneOffRequest(
            opt.HelperImage, ["sh", "-c", $"ls -1 {PostgresWalArchivingCommand.ArchiveMountPath}"],
            [(walVolume, PostgresWalArchivingCommand.ArchiveMountPath, true)]),
            new Deployments.InlineProgress<string>(l => listOutput.AppendLine(l)), ct);

        if (listExit != 0)
        {
            Fail(status, $"Could not list archived WAL segments for '{svc.Name}' (exit {listExit}).");
            await db.SaveChangesAsync(ct);
            return;
        }

        var fileNames = Deployments.LogText.Clean(listOutput.ToString())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => f.Length > 0)
            .ToList();

        var alreadyShipped = await db.WalSegments.AsNoTracking()
            .Where(w => w.ManagedServiceId == svc.Id && w.WorkspaceId == svc.WorkspaceId && fileNames.Contains(w.FileName))
            .Select(w => w.FileName)
            .ToListAsync(ct);
        var toShip = fileNames.Except(alreadyShipped, StringComparer.Ordinal).ToList();

        if (toShip.Count == 0)
        {
            // Nothing new to ship is not itself a failure — a quiet instance between segment switches
            // is normal — but it must not advance LastSuccessAt either. archive_timeout=300
            // (PostgresWalArchivingCommand) forces a real switch often enough that "nothing shipped
            // for longer than PitrRecoveryWindow.StaleAfter" is exactly the signal a stalled shipper
            // needs, and that signal comes from LastSuccessAt standing still, not from this branch.
            status.ConsecutiveFailures = 0;
            status.LastError = null;
            await db.SaveChangesAsync(ct);
            return;
        }

        var destination = await db.BackupDestinations.FirstOrDefaultAsync(
            d => d.WorkspaceId == svc.WorkspaceId && d.IsDefault, ct)
            ?? await db.BackupDestinations.FirstOrDefaultAsync(d => d.WorkspaceId == svc.WorkspaceId, ct);
        if (destination is null)
        {
            Fail(status, $"'{svc.Name}' has no backup destination configured, so archived WAL segments have nowhere to go.");
            await db.SaveChangesAsync(ct);
            return;
        }

        var shipped = new List<string>();
        foreach (var fileName in toShip)
        {
            // Staged into the shared staging volume first — the same "helper touches the archive
            // volume, the panel-side call owns the destination" split PostgresWalArchivingCommand's
            // own doc describes, and the same shape every other backup artifact already moves through.
            var fetchExit = await docker.RunOneOffAsync(new DockerOneOffRequest(
                opt.HelperImage,
                ["cp", $"{PostgresWalArchivingCommand.ArchiveMountPath}/{fileName}", $"/backup/{fileName}"],
                [(walVolume, PostgresWalArchivingCommand.ArchiveMountPath, true), (opt.StagingVolume, "/backup", false)]),
                null, ct);

            var localPath = Path.Combine(opt.StagingDir, fileName);
            if (fetchExit != 0 || !File.Exists(localPath))
            {
                Fail(status, $"Could not stage WAL segment '{fileName}' for '{svc.Name}' (exit {fetchExit}).");
                break;
            }

            var (artifactRef, size) = await storage.PutFileAsync(destination, fileName, localPath, ct);

            // Written only after PutFileAsync has confirmed the bytes actually reached the
            // destination — WalSegment's own class doc states this as the law; this is where it holds.
            db.WalSegments.Add(new WalSegment
            {
                WorkspaceId = svc.WorkspaceId, ManagedServiceId = svc.Id, DestinationId = destination.Id,
                FileName = fileName, ArchivedAt = clock.UtcNow, ArtifactPath = artifactRef, SizeBytes = size
            });
            shipped.Add(fileName);

            if (!string.Equals(Path.GetFullPath(artifactRef), Path.GetFullPath(localPath), StringComparison.OrdinalIgnoreCase)
                && File.Exists(localPath))
                try { File.Delete(localPath); } catch { /* best effort, as everywhere else staging is cleaned up */ }
        }

        if (shipped.Count > 0)
        {
            // Pruned from the volume only once each one is confirmed durably stored — never before,
            // and never a segment that failed to upload this run; it stays for the next tick to retry.
            var pruneList = string.Join(' ', shipped.Select(f => $"{PostgresWalArchivingCommand.ArchiveMountPath}/{f}"));
            await docker.RunOneOffAsync(new DockerOneOffRequest(
                opt.HelperImage, ["sh", "-c", $"rm -f {pruneList}"],
                [(walVolume, PostgresWalArchivingCommand.ArchiveMountPath, false)]), null, ct);

            status.SegmentsArchived += shipped.Count;
            status.LastSuccessAt = clock.UtcNow;
            status.ConsecutiveFailures = 0;
            status.LastError = null;
        }
        else
        {
            Fail(status, status.LastError ?? $"No WAL segment for '{svc.Name}' could be staged this run.");
        }

        await db.SaveChangesAsync(ct);
    }

    private static void Fail(WalArchivingStatus status, string error)
    {
        status.ConsecutiveFailures++;
        status.LastError = error;
    }
}
