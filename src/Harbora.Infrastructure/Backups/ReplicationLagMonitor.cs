using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Backups;

/// <summary>
/// Measures how far behind its primary every running PostgreSQL read replica is, on its own tick
/// (3.2, round-2 market-gaps plan) — the single writer of <see cref="ReplicationLagStatus"/>, and
/// therefore the only thing standing between an application routing reads to a replica and a panel
/// that would otherwise have nothing honest to say about how stale those reads are.
///
/// <para>
/// This is the single most important piece of this whole feature. A green tick, a "0s" or a blank
/// cell for a replica whose lag this monitor could not actually measure is a data-correctness bug in
/// a customer's own product — reads served from a replica that is silently hours behind — caused by
/// this panel telling them something untrue. <see cref="ReplicationLagStatus.LastSuccessAt"/> and
/// <see cref="ReplicationLagStatus.LagSeconds"/> therefore only ever move together, and only on a
/// query that actually got an answer back; every other path through <see cref="CheckOneAsync"/> —
/// unreachable server, non-zero exit, an empty/unparseable answer — leaves them exactly where they
/// were and records why, so <see cref="ReplicationLagPresenter"/> can say "unknown" instead of
/// repeating a stale number.
/// </para>
///
/// <para>
/// Runs with no <c>HttpContext</c>, the same reasoning <see cref="WalArchiveShipper"/> already gives
/// for its own unscoped read: every cross-table read below still carries an explicit
/// <c>WorkspaceId ==</c> comparison, so this stays correct even outside the unscoped ambient scope it
/// normally runs in.
/// </para>
/// </summary>
public sealed class ReplicationLagMonitor(
    IServiceScopeFactory scopeFactory, ILogger<ReplicationLagMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try { await CheckDueReplicasAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Replication lag monitor tick failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task CheckDueReplicasAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var engines = scope.ServiceProvider.GetRequiredService<IServerEngineFactory>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var clock = scope.ServiceProvider.GetRequiredService<ISystemClock>();
        var runtime = scope.ServiceProvider.GetRequiredService<IOptions<Deployments.HarboraRuntimeOptions>>().Value;

        // Running only — a replica still provisioning or stopped has no live connection to query, and
        // that is not the same fact as "lag unknown because the query failed"; NeverMeasured already
        // says the honest thing for a replica this monitor has correctly never touched yet.
        var replicas = await db.ManagedServices
            .Where(s => s.PrimaryManagedServiceId != null && s.Status == ServiceStatus.Running)
            .ToListAsync(ct);

        foreach (var replica in replicas)
        {
            if (!ReplicationSupport.Supports(replica.Type)) continue; // cannot happen today; stated, not assumed
            try { await CheckOneAsync(replica, db, engines, protector, clock, runtime, ct); }
            catch (Exception ex) { logger.LogError(ex, "Replication lag check failed for {Replica}.", replica.Name); }
        }
    }

    private async Task CheckOneAsync(
        ManagedService replica, HarboraDbContext db, IServerEngineFactory engines, ISecretProtector protector,
        ISystemClock clock, Deployments.HarboraRuntimeOptions runtime, CancellationToken ct)
    {
        var status = await db.ReplicationLagStatuses
            .FirstOrDefaultAsync(s => s.ManagedServiceId == replica.Id && s.WorkspaceId == replica.WorkspaceId, ct);
        if (status is null)
        {
            status = new ReplicationLagStatus { WorkspaceId = replica.WorkspaceId, ManagedServiceId = replica.Id };
            db.ReplicationLagStatuses.Add(status);
        }
        status.LastAttemptAt = clock.UtcNow;

        Application.Abstractions.IDockerEngine docker;
        try { docker = await engines.ResolveAsync(replica.ServerId, ct); }
        catch (Exception ex)
        {
            Fail(status, $"Could not reach the server holding '{replica.Name}': {ex.Message}");
            await db.SaveChangesAsync(ct);
            return;
        }

        string adminPassword;
        try { adminPassword = protector.Unprotect(replica.EncryptedPassword); }
        catch (Exception ex)
        {
            logger.LogError(ex, "The admin password for {Replica} could not be decrypted.", replica.Name);
            Fail(status, "This replica's own credentials could not be read.");
            await db.SaveChangesAsync(ct);
            return;
        }

        var wsSlug = await db.Workspaces.Where(w => w.Id == replica.WorkspaceId).Select(w => w.Slug).FirstAsync(ct);
        var environmentNetwork = await Networking.EnvironmentNetworkResolver.ForAsync(db, replica.EnvironmentId, ct);
        var network = Networking.NetworkPlan.Primary(environmentNetwork, runtime.WorkspaceNetwork(wsSlug));

        var image = $"{ServiceCatalog.All[replica.Type].ImageRepo}:{replica.Version}";
        var output = new System.Text.StringBuilder();
        int exit;
        try
        {
            exit = await docker.RunOneOffAsync(new Application.Abstractions.DockerOneOffRequest(
                image,
                ReplicationLagQuery.Command(replica.ContainerName, replica.InternalPort, replica.Username, replica.DatabaseName),
                [],
                Env: ReplicationLagQuery.Environment(adminPassword),
                NetworkMode: network),
                new Deployments.InlineProgress<string>(line => { lock (output) output.AppendLine(line); }), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not query replication lag for {Replica}.", replica.Name);
            Fail(status, $"Harbora lost contact with '{replica.Name}' while checking lag: {ex.Message}");
            await db.SaveChangesAsync(ct);
            return;
        }

        if (exit != 0)
        {
            Fail(status, $"'{replica.Name}' refused the lag query (exit {exit}). " +
                          Deployments.LogText.Clean(output.ToString()).Trim());
            await db.SaveChangesAsync(ct);
            return;
        }

        // A successful query, whatever it answered — this is the ONLY place LastSuccessAt advances.
        var replayedAt = ReplicationLagQuery.ParseReplayTimestamp(output.ToString());
        status.LagSeconds = replayedAt is { } at ? Math.Max(0, (clock.UtcNow - at).TotalSeconds) : null;
        status.LastSuccessAt = clock.UtcNow;
        status.ConsecutiveFailures = 0;
        status.LastError = null;
        await db.SaveChangesAsync(ct);
    }

    private static void Fail(ReplicationLagStatus status, string error)
    {
        status.ConsecutiveFailures++;
        status.LastError = error;
    }
}
