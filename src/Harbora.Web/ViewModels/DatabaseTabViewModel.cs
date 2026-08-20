namespace Harbora.Web.ViewModels;

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
