using Harbora.Domain.Common;

namespace Harbora.Domain.Tenancy;

/// <summary>
/// A tenancy plan the provider offers to customers. Caps how much a workspace may consume in
/// total; quota checks run before an app/service is created or deployed. Zero means "unlimited".
/// </summary>
public class Plan : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string NameFa { get; set; } = string.Empty;

    public int MaxApps { get; set; }
    public int MaxServices { get; set; }

    /// <summary>Workspace seats, including unexpired pending invitations (0 = unlimited).</summary>
    public int MaxMembers { get; set; }
    /// <summary>Projects owned by the workspace (0 = unlimited).</summary>
    public int MaxProjects { get; set; }
    /// <summary>Environments across every project, including previews (0 = unlimited).</summary>
    public int MaxEnvironments { get; set; }
    /// <summary>Custom and automatically assigned application domains (0 = unlimited).</summary>
    public int MaxDomains { get; set; }
    /// <summary>Persistent application volumes (0 = unlimited).</summary>
    public int MaxVolumes { get; set; }
    /// <summary>Recurring backup schedules (0 = unlimited).</summary>
    public int MaxBackupSchedules { get; set; }

    /// <summary>Total memory a workspace may commit across all its apps (0 = unlimited).</summary>
    public long MaxMemoryBytes { get; set; }
    /// <summary>Total CPU cores a workspace may commit (0 = unlimited).</summary>
    public double MaxCpuCores { get; set; }
    /// <summary>Total volume/disk a workspace may allocate (0 = unlimited).</summary>
    public long MaxDiskBytes { get; set; }

    /// <summary>Comma-separated instance-size keys this plan allows (empty = all enabled sizes).</summary>
    public string AllowedSizeKeys { get; set; } = string.Empty;

    /// <summary>Optional server-pool tag: apps in this plan may only be scheduled on matching nodes.</summary>
    public string? NodePool { get; set; }

    /// <summary>For display/billing; not charged by Harbora itself.</summary>
    public decimal MonthlyPrice { get; set; }

    /// <summary>
    /// The floor. A workspace on this plan pays at least this much per hour, whatever it is running
    /// — including nothing. <see cref="MonthlyPrice"/> is unrelated and remains display-only.
    ///
    /// <para>
    /// <b>Null means nobody has priced this plan; zero means it has no floor on purpose.</b> Every
    /// rate on this class reads that way, and it is the opposite of the convention the caps above
    /// follow, where zero means unlimited. A cap left blank is a decision not to cap; a price left
    /// blank is not a decision to charge nothing. The two look identical in a column of zeros, and
    /// the difference is a bill nobody sends.
    /// </para>
    /// </summary>
    public long? BaseRatePerHourMinor { get; set; }

    /// <summary>
    /// Whether this plan sells capacity past its own caps. False keeps today's behaviour, where
    /// <c>IQuotaService</c> refuses; true lets the tenant past.
    ///
    /// <para>
    /// <b>What the excess costs is the ordinary meter, not a surcharge.</b> An application past the
    /// cap pays its instance size's hourly rate exactly like one inside it, and a volume past the
    /// cap pays <see cref="DiskGbHourMinor"/> exactly like one inside it. There is deliberately no
    /// second, higher rate: this plan class carried three — a per-core-hour, a per-memory-gibibyte
    /// -hour and a per-disk-gibibyte-hour — which nothing ever read, and they were removed rather
    /// than surfaced, because a price an operator can set and nothing collects is worse than no
    /// price at all. Anyone adding burst pricing has to start with the tick, not with a column:
    /// the compute meter is priced per size-hour, so there is no per-core figure to charge the
    /// over-cap fraction of an hour at, and whatever is added has to be told apart from the rate
    /// already being charged or the hour is billed twice.
    /// </para>
    /// </summary>
    public bool AllowsOverage { get; set; }

    /// <summary>
    /// Charged per gibibyte-hour of allocated volume, inside the caps as well as past them.
    /// Null is unpriced; see <see cref="BaseRatePerHourMinor"/>.
    /// </summary>
    public long? DiskGbHourMinor { get; set; }

    public bool IsDefault { get; set; }
    public bool IsEnabled { get; set; } = true;
}
