using Harbora.Domain.Common;

namespace Harbora.Domain.Services;

/// <summary>
/// A maintenance statement this platform knows how to run against a logical database (2.3, round-2
/// market-gaps plan). Persisted by value — appended, never renumbered, the same rule every enum on a
/// row already follows here.
///
/// <para>
/// <see cref="Vacuum"/> and <see cref="VacuumFull"/> are deliberately two different members rather
/// than one "vacuum" with a flag: they are different operations with different costs, and offering
/// them as an undifferentiated choice is exactly the dishonesty this feature's own brief calls out.
/// Plain <c>VACUUM</c> is online; <c>VACUUM FULL</c> rewrites the table under an
/// <c>ACCESS EXCLUSIVE</c> lock and needs free disk roughly equal to the table's own size. See
/// <see cref="Harbora.Infrastructure.Services.DatabaseMaintenanceSql.Describe"/> for the sentence each
/// one shows.
/// </para>
/// </summary>
public enum DatabaseMaintenanceOperation
{
    /// <summary>PostgreSQL <c>VACUUM</c> — reclaims dead-row space and updates the visibility map.
    /// Runs online.</summary>
    Vacuum = 0,

    /// <summary>PostgreSQL <c>VACUUM FULL</c> — rewrites the table into a new file. Takes an
    /// <c>ACCESS EXCLUSIVE</c> lock for the duration and needs free disk space roughly equal to the
    /// table's own size.</summary>
    VacuumFull = 1,

    /// <summary>PostgreSQL <c>ANALYZE</c> — refreshes the query planner's statistics. Runs online.</summary>
    Analyze = 2,

    /// <summary>PostgreSQL <c>REINDEX DATABASE</c> — rebuilds every index. Each index is locked
    /// against writes while it rebuilds.</summary>
    Reindex = 3,

    /// <summary>MySQL/MariaDB <c>OPTIMIZE TABLE</c>, run over every table in the database. The table
    /// is locked while it runs.</summary>
    Optimize = 4
}

/// <summary>
/// A maintenance run's lifecycle. Three states an operator can act on, never a fourth meaning
/// "nothing happened yet" spelled as one of these three — <see cref="DatabaseMaintenanceRun.Status"/>
/// starts at <see cref="Pending"/> precisely so a row that exists but has not been picked up by the
/// job worker yet is never confused with one that ran and passed.
/// </summary>
public enum DatabaseMaintenanceRunStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3
}

/// <summary>What started a maintenance run — the same distinction <c>BackupTrigger</c> already draws
/// for a backup, kept separate rather than reused because this feature has no API trigger and no
/// safety-before-a-risky-operation trigger to account for.</summary>
public enum DatabaseMaintenanceTrigger
{
    Manual = 0,
    Schedule = 1
}

/// <summary>
/// A recurring maintenance rule for one logical database and one operation (2.3, round-2 market-gaps
/// plan) — the same cron-expression idiom <c>Harbora.Modules.Backup.Domain.BackupPolicy</c> already
/// uses, reused rather than reinvented: <see cref="Schedule"/> is read by
/// <see cref="Harbora.Infrastructure.Deployments.CronSchedule"/>, and
/// <see cref="Harbora.Infrastructure.Services.DatabaseMaintenanceScheduler"/> ticks it the same way
/// <c>BackupPolicyScheduler</c> ticks a <c>BackupPolicy</c>.
///
/// <para>
/// One row per (database, operation) rather than one row per database: a database's operator may
/// want <c>VACUUM</c> nightly and <c>REINDEX</c> monthly, and those are different schedules with
/// different costs, not two settings on the same rule.
/// </para>
/// </summary>
public class DatabaseMaintenanceSchedule : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>No navigation back onto <see cref="ManagedServiceDatabase"/> — looked up by id where
    /// needed, the same loose-reference shape <c>Harbora.Domain.Backups.WalArchivingStatus</c> already
    /// uses for the identical reason.</summary>
    public Guid ManagedServiceDatabaseId { get; set; }

    public DatabaseMaintenanceOperation Operation { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Cron expression, read in <see cref="Timezone"/>. Validated on write — an unparseable
    /// schedule silently never fires, the same trap <c>BackupPolicy.Schedule</c>'s own doc warns
    /// about.</summary>
    public string Schedule { get; set; } = "0 3 * * 0";

    /// <summary>IANA timezone the schedule is read in — "3am" means the tenant's 3am, the same
    /// reasoning <c>BackupPolicy.Timezone</c> gives.</summary>
    public string Timezone { get; set; } = "UTC";

    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
}

/// <summary>
/// One attempt at one maintenance statement, and the row the panel's history reads (2.3, round-2
/// market-gaps plan) — the same "row is the queue" shape <c>Harbora.Domain.Jobs.Job</c> already uses,
/// scoped down to what this feature's own history needs to show: which statement, on which database,
/// how long it took, and — on failure — the engine's own words.
///
/// <para>
/// Never fabricated. <see cref="StartedAt"/>/<see cref="FinishedAt"/> are set only when the run
/// actually reached that point, so a duration is either real or absent — never a zero standing in for
/// "not measured". <see cref="Status"/> starts at <see cref="DatabaseMaintenanceRunStatus.Pending"/>
/// and is the only field a screen may render a tick or a cross from.
/// </para>
/// </summary>
public class DatabaseMaintenanceRun : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    public Guid ManagedServiceDatabaseId { get; set; }

    /// <summary>The schedule that queued this run, or null for a "run now" a person pressed.</summary>
    public Guid? ScheduleId { get; set; }

    public DatabaseMaintenanceOperation Operation { get; set; }

    public DatabaseMaintenanceRunStatus Status { get; set; } = DatabaseMaintenanceRunStatus.Pending;

    public DatabaseMaintenanceTrigger TriggeredBy { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>
    /// Why this run failed, in the engine's own words where there are any to quote — never a bare
    /// "maintenance failed". <see cref="Harbora.Infrastructure.Services.DatabaseMaintenanceService"/>
    /// is what composes this from the statement, the database's name and
    /// <see cref="Harbora.Infrastructure.Services.DatabaseGrantExecutor.MaintainAsync"/>'s own answer.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>Terminal runs are never re-run or reconciled — the same idiom <c>Job.IsTerminal</c>
    /// already uses.</summary>
    public bool IsTerminal => Status is DatabaseMaintenanceRunStatus.Succeeded or DatabaseMaintenanceRunStatus.Failed;
}
