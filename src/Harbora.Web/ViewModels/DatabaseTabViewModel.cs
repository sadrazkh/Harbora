namespace Harbora.Web.ViewModels;

/// <summary>
/// One logical database inside an instance, for the Overview tab's "logical databases" panel (D3,
/// 2026-08-25 shared-databases plan) — the management surface D1 shipped the machinery for but no
/// panel UI, per that task's own report.
/// </summary>
/// <param name="SizeBytes">
/// Per-database size, or null — always null today. Only the whole instance's disk usage is measured
/// (<c>DatabaseRowViewModel.StorageBytes</c>, one figure for every logical database on it combined);
/// no engine here has a per-database query this platform has built yet. Shown as "not measured",
/// never as a fabricated zero — the honesty requirement this panel exists to satisfy.
/// </param>
/// <param name="BackupTrackingAvailable">
/// Whether a backup can even be attributed to this one database yet. True only for the instance's
/// own default database: every backup an instance took before D1 existed was, definitionally, a
/// backup of the one database it had — so the instance-wide backup history already answers this
/// question for that row. A logical database created after D1 has no backup path of its own yet
/// (D2's remit, in flight alongside this one); rather than show "never backed up" beside one nobody
/// could have backed up individually, the panel says tracking is not available for it yet.
/// </param>
/// <param name="CanRename">
/// Whether this engine can rename a database without data loss — see
/// <c>DatabaseGrantSql.SupportsRename</c>. False for the default database regardless of engine (see
/// <c>LogicalDatabaseService.RenameAsync</c>), and false for MySQL/MariaDB, which have no lossless
/// rename to offer.
/// </param>
/// <param name="CanDelete">
/// Whether this row's own delete control applies here — the default database never can be
/// (<c>LogicalDatabaseService.DeleteAsync</c>); removing it means removing the whole instance.
/// </param>
/// <param name="HasVectorExtension">
/// 1.7 (pgvector-as-option plan): whether pgvector is installed inside this specific database, as
/// last confirmed by the engine — <c>ManagedServiceDatabase.HasVectorExtension</c> verbatim. Null is
/// "not measured", not "no"; the view must tell the two apart the same way it already does for
/// <see cref="SizeBytes"/>.
/// </param>
/// <param name="VectorExtensionCheckedAt">When <see cref="HasVectorExtension"/> was last confirmed,
/// or null if never.</param>
public sealed record LogicalDatabaseRowViewModel(
    Guid Id,
    string Name,
    bool IsDefault,
    string Username,
    IReadOnlyList<string> AttachedApps,
    long? SizeBytes,
    bool BackupTrackingAvailable,
    DateTimeOffset? LastBackupAt,
    Harbora.Domain.Common.BackupStatus? LastBackupStatus,
    bool CanRename,
    bool CanDelete,
    bool? HasVectorExtension,
    DateTimeOffset? VectorExtensionCheckedAt);

/// <summary>
/// What the database shell's header and tab strip need, on every tab. Mirrors <see cref="AppTabViewModel"/>.
///
/// <para>
/// A base class rather than ViewData: the shell is typed to this, so a tab that forgets to supply the
/// header fails to compile instead of rendering a page with an empty title.
/// </para>
/// </summary>
public abstract class DatabaseTabViewModel
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public Harbora.Domain.Common.ManagedServiceType Type { get; init; }
    public string Version { get; init; } = "";
    public Harbora.Domain.Common.ServiceStatus Status { get; init; }
    public string Project { get; init; } = "";
    public string Environment { get; init; } = "";

    /// <summary>
    /// Whether the caller may operate this database, not merely see it. <c>Details.cshtml</c> gated
    /// its header's action buttons — Start/Stop, Test, External access, Admin tool — on this before
    /// the header moved into the shell; every tab reads it the same way now.
    /// </summary>
    public bool CanManage { get; init; }

    /// <summary>Which tab is drawn as current. One of: overview, access, usage, backups.</summary>
    public string CurrentTab { get; init; } = "overview";
}

/// <summary>
/// The Overview tab — today's <c>Details.cshtml</c>, unmoved by this task except for its header (now
/// in <c>_Shell.cshtml</c>) and its "this moment" resource figures (now the Usage tab). Wraps the row
/// built for the list/details pages rather than re-declaring its many fields on this class, because
/// Overview alone still reads most of them.
/// </summary>
public sealed class DatabaseOverviewViewModel : DatabaseTabViewModel
{
    public required DatabaseRowViewModel Database { get; init; }
    public string Connection { get; init; } = string.Empty;
    public bool Reveal { get; init; }
    public string? Network { get; init; }
    public IReadOnlyList<string> UsedBy { get; init; } = [];
    public IReadOnlyList<ResourceOptionViewModel> Apps { get; init; } = [];
    public IReadOnlyList<BackupEventViewModel> Backups { get; init; } = [];
    public DateTimeOffset? NextBackupAt { get; init; }
    public int? BackupIntervalHours { get; init; }

    /// <summary>
    /// Sub-project 10. Export needs only <c>backups.run</c> — the same capability the "back up now"
    /// button elsewhere already asks for — so an Operator, who has that but not the heavier restore
    /// capability, can export without being able to import.
    /// </summary>
    public bool CanExport { get; init; }

    /// <summary>
    /// Sub-project 10. Import is gated on <c>backups.restore</c>, the same capability the existing
    /// admin restore button already requires: it overwrites the database's current contents, which
    /// is the heavier of the two acts this page can ask the backup engine to do.
    /// </summary>
    public bool CanImport { get; init; }

    /// <summary>What the container is actually running, so version drift can be shown.</summary>
    public string? RunningImage { get; init; }

    /// <summary>The resource plan, or null for a database created before they had one.</summary>
    public string? InstanceSizeKey { get; init; }

    /// <summary>
    /// The host it runs on. Read by the resize control, which pins the chooser to this server: a
    /// tier's price now depends on where it runs, and a resize does not move a workload.
    /// </summary>
    public Guid ServerId { get; init; }
    public long MemoryLimitBytes { get; init; }
    public double CpuLimit { get; init; }

    /// <summary>The disk the tier came with, so the measured figure has a ceiling beside it.</summary>
    public long DiskLimitBytes { get; init; }

    /// <summary>Whether connections to it are encrypted, as recorded at the last provision.</summary>
    public bool TlsEnabled { get; init; }

    /// <summary>
    /// Redis's <c>maxmemory-policy</c>, when <see cref="Database"/> is a Redis instance — see
    /// <c>RedisMemoryPolicy</c>. Null both for every non-Redis engine and for a Redis instance nobody
    /// has ever set one on.
    /// </summary>
    public string? RedisEvictionPolicy { get; init; }

    /// <summary>Redis's <c>maxmemory</c>, in bytes. Zero is Redis's own "no cap".</summary>
    public long RedisMaxMemoryBytes { get; init; }

    /// <summary>
    /// Whether the memory policy shown above has actually reached the running container's own launch
    /// command yet, or only its live, restart-fragile <c>CONFIG SET</c> state — see
    /// <c>Harbora.Domain.Services.ManagedService.HasUnpublishedChanges</c>'s own doc for why the two
    /// are different facts.
    /// </summary>
    public bool RedisMemoryPolicyUnpublished { get; init; }

    /// <summary>
    /// D3 (2026-08-25 shared-databases plan): the logical databases inside this instance. Empty both
    /// when the engine genuinely has none created yet AND when the engine has no logical-database
    /// story at all — <see cref="LogicalDatabasesSupported"/> is what tells those two apart, and the
    /// view must not render an empty table for the second case: an unsupported engine and an empty
    /// list must not look identical.
    /// </summary>
    public IReadOnlyList<LogicalDatabaseRowViewModel> LogicalDatabases { get; init; } = [];

    /// <summary>Whether this engine has any logical-database story at all — PostgreSQL, MySQL and
    /// MariaDB do; Redis, MongoDB, RabbitMQ and NATS do not (<c>DatabaseGrantSql.Supports</c>).</summary>
    public bool LogicalDatabasesSupported { get; init; }

    /// <summary>Why this engine has none, when <see cref="LogicalDatabasesSupported"/> is false —
    /// shown in place of the table rather than beside an empty one.</summary>
    public string? LogicalDatabasesUnsupportedReason { get; init; }

    /// <summary>
    /// Whether this installation can actually reach the engine to create, rename or delete a logical
    /// database right now — <c>LogicalDatabaseService.CanCreateLocally</c>. False on a remote node
    /// D4 has not reached yet (HARBORA-0059), so the create form is not offered where it would only
    /// ever fail.
    /// </summary>
    public bool CanManageLogicalDatabasesLocally { get; init; }

    /// <summary>
    /// 1.7 (pgvector-as-option plan): whether this PostgreSQL instance is set to run a pgvector-
    /// capable image. Always false for every other engine — <c>ManagedService.PgVectorEnabled</c>
    /// verbatim.
    /// </summary>
    public bool PgVectorEnabled { get; init; }

    /// <summary>
    /// Whether turning pgvector on (or off) here has reached the running container's own image yet —
    /// <c>ManagedService.HasUnpublishedChanges</c>, read for this section the same way
    /// <see cref="RedisMemoryPolicyUnpublished"/> already reads the very same field for Redis.
    /// </summary>
    public bool PgVectorUnpublished { get; init; }
}

/// <summary>
/// The Usage tab — today's <c>Details.cshtml</c> "this moment" stat cards (CPU, memory, storage,
/// linked apps) and the same figures charted over time, moved rather than rewritten.
/// </summary>
public sealed class DatabaseUsageViewModel : DatabaseTabViewModel
{
    public double? CpuPercent { get; init; }
    public double CpuLimit { get; init; }
    public long? MemoryBytes { get; init; }
    public long MemoryLimitBytes { get; init; }
    public long? StorageBytes { get; init; }
    public DateTimeOffset? StorageMeasuredAt { get; init; }
    public long DiskLimitBytes { get; init; }

    /// <summary>
    /// Not in the brief's field list — see the task report. Details.cshtml drew this stat card
    /// alongside CPU/memory/storage as one grid, so it moved with the rest of the block rather than
    /// being split out to stay behind on Overview.
    /// </summary>
    public int LinkedApps { get; init; }

    /// <summary>
    /// The chart window this render answers — see <see cref="AppUsageViewModel.SelectedMinutes"/>,
    /// which this mirrors for the same reason the two Usage tabs share one range control design.
    /// </summary>
    public int SelectedMinutes { get; init; } = Harbora.Infrastructure.Monitoring.UsageRangeWindow.OneHour;
}
