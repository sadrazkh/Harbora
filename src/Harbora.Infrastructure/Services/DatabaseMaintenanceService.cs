using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Jobs;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// Queues and runs VACUUM/VACUUM FULL/ANALYZE/REINDEX/OPTIMIZE TABLE against one logical database
/// (2.3, round-2 market-gaps plan) — the "run now" button and
/// <see cref="DatabaseMaintenanceScheduler"/>'s own tick both call <see cref="QueueAsync"/>, the same
/// "the schedule and the run-now button take exactly the same path" idiom <c>CronJobRunner</c>
/// already establishes for App cron jobs.
///
/// <para>
/// Optional <see cref="DatabaseGrantExecutor"/>/<see cref="ManagedServiceEngine"/> dependencies, the
/// same shape <see cref="LogicalDatabaseService"/> already uses for the identical reason: on a
/// single-server install the control plane talks to the same Docker daemon the database runs on, and
/// <see cref="CanRunLocally"/> is true; a remote node has not shipped this operation's node contract
/// yet, so there it is refused by name instead of pretending to work.
/// </para>
/// </summary>
public sealed class DatabaseMaintenanceService(
    HarboraDbContext db,
    ISystemClock clock,
    IJobQueue jobs,
    ILogger<DatabaseMaintenanceService> logger,
    DatabaseGrantExecutor? grants = null,
    ManagedServiceEngine? services = null)
{
    /// <summary>Whether this installation can really reach the instance's engine — see the type doc.</summary>
    public bool CanRunLocally => grants is not null && services is not null;

    public const string AlreadyRunning = "A maintenance run on this database is already in progress.";

    /// <summary>
    /// The collision rule this feature's own brief calls for: a physical base backup
    /// (<c>BackupType.PostgresBaseBackup</c>) reads the whole instance's data directory while it runs,
    /// and a logical dump (<c>BackupType.Database</c>) of the SAME logical database runs its own
    /// transaction against it — neither should be racing a VACUUM FULL taking an ACCESS EXCLUSIVE lock,
    /// or a plain VACUUM/ANALYZE/REINDEX/OPTIMIZE competing for the same I/O a backup is trying to read
    /// a consistent copy from. A dump of a DIFFERENT logical database on the same instance is
    /// deliberately not blocked here: <c>pg_dump</c>/<c>mysqldump</c> of one database takes nothing
    /// that a statement running inside a different database on the same server needs.
    /// </summary>
    public const string BackupRunning =
        "A backup of this database is currently running. Wait for it to finish, then try again.";

    /// <summary>
    /// Validates, refuses by name, or persists a Pending run and hands it to the durable queue. Never
    /// runs the statement itself — see the type doc for why "run now" and the schedule share this one
    /// method.
    /// </summary>
    public async Task<(Guid? RunId, string? Error)> QueueAsync(
        Guid databaseId, DatabaseMaintenanceOperation operation, DatabaseMaintenanceTrigger trigger,
        Guid? scheduleId, CancellationToken ct)
    {
        var logical = await db.ManagedServiceDatabases.Include(d => d.ManagedService)
            .FirstOrDefaultAsync(d => d.Id == databaseId, ct);
        if (logical is null) return (null, "That database no longer exists.");

        var service = logical.ManagedService;
        if (service is null) return (null, "That database instance no longer exists.");

        if (!DatabaseMaintenanceSql.Supports(service.Type))
            return (null, DatabaseMaintenanceSql.UnsupportedReason(service.Type));

        if (!DatabaseMaintenanceSql.SupportsOperation(service.Type, operation))
            return (null, DatabaseMaintenanceSql.UnsupportedOperationReason(service.Type, operation));

        if (!CanRunLocally)
            return (null,
                "This installation cannot reach that database instance's own engine, so maintenance " +
                "cannot run here yet.");

        // Validated, not acquired: the same "message, not safety" pre-check
        // BackupSnapshotService.QueueAsync already makes for its own AlreadyRunning refusal. The job
        // queue's own exclusivity (EnqueueExclusiveAsync below, keyed on the database's own id) is
        // what actually holds under concurrency.
        var alreadyRunning = await db.DatabaseMaintenanceRuns.AnyAsync(r =>
            r.ManagedServiceDatabaseId == databaseId &&
            (r.Status == DatabaseMaintenanceRunStatus.Pending || r.Status == DatabaseMaintenanceRunStatus.Running), ct);
        if (alreadyRunning) return (null, AlreadyRunning);

        if (await BackupIsActiveAsync(service, logical, ct)) return (null, BackupRunning);

        var run = new DatabaseMaintenanceRun
        {
            WorkspaceId = logical.WorkspaceId,
            ManagedServiceDatabaseId = databaseId,
            ScheduleId = scheduleId,
            Operation = operation,
            Status = DatabaseMaintenanceRunStatus.Pending,
            TriggeredBy = trigger
        };
        db.DatabaseMaintenanceRuns.Add(run);
        await db.SaveChangesAsync(ct);

        // Excludes on the DATABASE, not on this run's own id — every run is a fresh row, so without
        // this two "run now" presses (or a press racing the schedule) would both be Pending and both
        // get claimed at once. The same shape IJobQueue.EnqueueExclusiveAsync's own doc describes for
        // a deployment and the redeploy behind it.
        await jobs.EnqueueExclusiveAsync(
            JobKind.DatabaseMaintenance, run.Id, exclusiveWith: databaseId, workspaceId: logical.WorkspaceId, ct);

        logger.LogInformation("Queued {Operation} on {Database} ({Service}).",
            DatabaseMaintenanceSql.Label(operation), logical.Name, service.Name);

        return (run.Id, null);
    }

    /// <summary>
    /// Creates or updates the one schedule this database has for this operation — one row per
    /// (database, operation), matched by the unique index the same way an upsert reuses an existing
    /// row rather than growing a second one that would silently race it. Refuses by name — never
    /// silently swallowed — an operation this engine cannot run, or a cron expression that would
    /// otherwise never fire (see <c>CronSchedule.TryParse</c>).
    /// </summary>
    public async Task<string?> SetScheduleAsync(
        Guid databaseId, DatabaseMaintenanceOperation operation, bool enabled,
        string? schedule, string? timezone, CancellationToken ct)
    {
        var logical = await db.ManagedServiceDatabases.Include(d => d.ManagedService)
            .FirstOrDefaultAsync(d => d.Id == databaseId, ct);
        if (logical is null) return "That database no longer exists.";

        var service = logical.ManagedService;
        if (service is null) return "That database instance no longer exists.";

        if (!DatabaseMaintenanceSql.Supports(service.Type))
            return DatabaseMaintenanceSql.UnsupportedReason(service.Type);
        if (!DatabaseMaintenanceSql.SupportsOperation(service.Type, operation))
            return DatabaseMaintenanceSql.UnsupportedOperationReason(service.Type, operation);

        if (!CronSchedule.TryParse(schedule, out _, out var cronError)) return cronError;

        var zone = string.IsNullOrWhiteSpace(timezone) ? "UTC" : timezone;
        try { TimeZoneInfo.FindSystemTimeZoneById(zone); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return $"'{zone}' is not a timezone this platform recognises.";
        }

        var row = await db.DatabaseMaintenanceSchedules.FirstOrDefaultAsync(
            s => s.ManagedServiceDatabaseId == databaseId && s.Operation == operation, ct);
        if (row is null)
        {
            row = new DatabaseMaintenanceSchedule
            { WorkspaceId = logical.WorkspaceId, ManagedServiceDatabaseId = databaseId, Operation = operation };
            db.DatabaseMaintenanceSchedules.Add(row);
        }

        row.Enabled = enabled;
        row.Schedule = schedule!.Trim();
        row.Timezone = zone;
        row.NextRunAt = DatabaseMaintenanceScheduler.NextRun(row, clock.UtcNow);

        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Removes a schedule. Idempotent on an already-missing row — the same "a caller and a
    /// sweeper racing on the same delete must both see success" reasoning
    /// <c>LogicalDatabaseService.DeleteAsync</c> already gives.</summary>
    public async Task DeleteScheduleAsync(Guid scheduleId, CancellationToken ct)
    {
        var row = await db.DatabaseMaintenanceSchedules.FirstOrDefaultAsync(s => s.Id == scheduleId, ct);
        if (row is null) return;

        db.DatabaseMaintenanceSchedules.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The job body — <see cref="JobKind.DatabaseMaintenance"/>'s target is the run's own row.
    ///
    /// <para>
    /// Read with <see cref="EntityFrameworkQueryableExtensions.IgnoreQueryFilters{TEntity}(IQueryable{TEntity})"/>
    /// plus an explicit <c>WorkspaceId ==</c> comparison, not the ambient query filter this background
    /// path has none of: a job worker runs with no <c>HttpContext</c>, so the ordinary tenant filter
    /// would read an empty result and this would report success having touched nothing — the exact
    /// defect class this platform has shipped before. The explicit comparison against
    /// <paramref name="run"/>'s own <see cref="DatabaseMaintenanceRun.WorkspaceId"/> (rather than
    /// trusting the id alone) is what keeps a row whose target no longer belongs to the workspace that
    /// queued it from being touched at all.
    /// </para>
    /// </summary>
    public async Task RunAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.DatabaseMaintenanceRuns.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return;

        // Idempotent re-claim guard, the same shape BackupSnapshotService.RunAsync uses: nothing
        // legitimate reaches here for a run already settled.
        if (run.IsTerminal)
        {
            logger.LogInformation("Maintenance run {RunId} is already {Status}; nothing to do.", runId, run.Status);
            return;
        }

        var logical = await db.ManagedServiceDatabases.IgnoreQueryFilters()
            .Include(d => d.ManagedService)
            .FirstOrDefaultAsync(d => d.Id == run.ManagedServiceDatabaseId && d.WorkspaceId == run.WorkspaceId, ct);

        if (logical?.ManagedService is not { } service)
        {
            await FailAsync(run, "That database no longer exists.", ct);
            return;
        }

        // Re-checked here, not only at QueueAsync time: a backup can start in the window between this
        // run being enqueued and the worker claiming it. Failed rather than left Pending — this kind's
        // MaxAttemptsFor is 1, so there is no later attempt to fall back to; the schedule's own next
        // occurrence, or another "run now" press, is how this gets tried again.
        if (await BackupIsActiveAsync(service, logical, ct))
        {
            await FailAsync(run, BackupRunning, ct);
            return;
        }

        if (!CanRunLocally)
        {
            await FailAsync(run, "This installation cannot reach that database instance's own engine.", ct);
            return;
        }

        run.Status = DatabaseMaintenanceRunStatus.Running;
        run.StartedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        var network = await services!.NetworkForAsync(service, ct);
        var (ok, error, _) = await grants!.MaintainAsync(service, network, logical.Name, run.Operation, ct);

        run.FinishedAt = clock.UtcNow;
        if (ok)
        {
            run.Status = DatabaseMaintenanceRunStatus.Succeeded;
            run.Error = null;
            logger.LogInformation("{Operation} on {Database} ({Service}) completed.",
                DatabaseMaintenanceSql.Label(run.Operation), logical.Name, service.Name);
        }
        else
        {
            run.Status = DatabaseMaintenanceRunStatus.Failed;
            run.Error = error ?? $"{DatabaseMaintenanceSql.Label(run.Operation)} on '{logical.Name}' failed.";
            logger.LogWarning("{Operation} on {Database} ({Service}) failed: {Error}",
                DatabaseMaintenanceSql.Label(run.Operation), logical.Name, service.Name, run.Error);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Settles a run interrupted by a restart — the same reason <c>CronJobRunner.ReconcileAsync</c>
    /// exists: a row left reading <see cref="DatabaseMaintenanceRunStatus.Running"/> for ever would
    /// also hold <see cref="AlreadyRunning"/>'s own guard shut, so every future "run now" on that
    /// database would refuse forever on a run that stopped existing the moment the process did.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        var stranded = await db.DatabaseMaintenanceRuns.IgnoreQueryFilters()
            .Where(r => r.Status == DatabaseMaintenanceRunStatus.Running)
            .ToListAsync(ct);
        if (stranded.Count == 0) return;

        foreach (var run in stranded)
        {
            run.Status = DatabaseMaintenanceRunStatus.Failed;
            run.Error = "Interrupted by a platform restart, so how it ended is not known.";
            run.FinishedAt = clock.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Settled {Count} maintenance run(s) interrupted by a restart.", stranded.Count);
    }

    /// <summary>
    /// Whether a backup of <paramref name="logical"/> — or a physical base backup of the whole
    /// instance it lives on — is in flight right now. See <see cref="BackupRunning"/> for which
    /// backups count and why.
    /// </summary>
    private async Task<bool> BackupIsActiveAsync(
        ManagedService service, ManagedServiceDatabase logical, CancellationToken ct)
    {
        var targetRef = service.Id.ToString();
        return await db.Backups.IgnoreQueryFilters().AsNoTracking().AnyAsync(b =>
            b.WorkspaceId == service.WorkspaceId &&
            b.TargetRef == targetRef &&
            (b.Status == BackupStatus.Pending || b.Status == BackupStatus.Running) &&
            (b.Type == BackupType.PostgresBaseBackup ||
             (b.Type == BackupType.Database &&
              (b.ManagedServiceDatabaseId == logical.Id || (b.ManagedServiceDatabaseId == null && logical.IsDefault)))),
            ct);
    }

    private async Task FailAsync(DatabaseMaintenanceRun run, string reason, CancellationToken ct)
    {
        run.Status = DatabaseMaintenanceRunStatus.Failed;
        run.Error = reason;
        run.FinishedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
