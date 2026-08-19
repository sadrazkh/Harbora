using Harbora.Domain.Deployments;
using Harbora.Domain.Monitoring;
using Harbora.Domain.Networking;

namespace Harbora.Web.ViewModels;

public sealed class MonitoringDashboardViewModel
{
    public bool DockerAvailable { get; set; }
    public string DockerVersion { get; set; } = "—";
    public long DiskUsed { get; set; }
    public long DiskTotal { get; set; }
    public long MemTotal { get; set; }
    public int ContainersRunning { get; set; }

    /// <summary>
    /// The host's latest <c>cpu.percent</c> sample, or null when the collector has never written one.
    /// Nullable on purpose (2026-08-19 monitoring redesign) — it used to be a plain <c>double</c>
    /// defaulted by EF's <c>FirstOrDefaultAsync</c> to <c>0</c> when no row existed at all, so a fresh
    /// install whose collector had not ticked yet read as a confidently measured "0%" rather than as
    /// "not collected yet". See <c>MonitoringController.Index</c> for the query this backs.
    /// </summary>
    public double? CpuPercent { get; set; }

    /// <summary>
    /// The configured fraction of disk that counts as a problem
    /// (<c>MonitoringOptions.DiskWarnRatio</c>). Defaulted to today's shipped constant so a view or
    /// test that never sets it keeps the old behaviour; the controller always sets it from options.
    /// </summary>
    public double DiskWarnRatio { get; set; } = 0.85;

    public bool DiskWarning => DiskTotal > 0 && (double)DiskUsed / DiskTotal >= DiskWarnRatio;

    public List<AppHealth> Apps { get; set; } = new();
    public List<Deployment> RecentDeploys { get; set; } = new();
    public int FailedDeploys { get; set; }

    public bool BackupWarning { get; set; }
    public string? BackupWarningText { get; set; }

    public List<DomainName> Domains { get; set; } = new();
    public List<Alert> Alerts { get; set; } = new();

    /// <summary>Newest first, open or closed — the timeline of what fired and, where it has
    /// happened, how it stopped firing (2026-08-16 monitoring-alerting spec §M4).</summary>
    public List<AlertIncident> Incidents { get; set; } = new();

    /// <summary>
    /// Newest first — the delivery log N1 (2026-08-16 notification-system spec) promises: one durable
    /// row per (message × destination), so a channel's refusal is something a person can read rather
    /// than something the next successful send to the same rule quietly erased.
    /// </summary>
    public List<Harbora.Domain.Notifications.NotificationDelivery> Deliveries { get; set; } = new();
}

public sealed record AppHealth(string Name, string Slug, string Status, string? LastDeployStatus, string ContainerState)
{
    /// <summary>Needed by the threshold-alert picker, which has to post an application id.</summary>
    public Guid Id { get; init; }

    /// <summary>
    /// The fraction of the last 30 days this app's container was observed running, or null when
    /// nothing was ever collected for it — the same figure and the same honesty gate the app's own
    /// Overview tab already shows (<c>LifecycleHistory.UptimePercentAsync</c>, Phase 6 M3). Surfaced
    /// here too so a worried person reading the monitoring page does not have to open every app in
    /// turn to find out which one has actually been flaky.
    /// </summary>
    public double? UptimePercent30d { get; init; }

    /// <summary>Restarts attributed to the last 30 days, alongside <see cref="UptimePercent30d"/> —
    /// null under the same "never collected" gate, zero when it was watched the whole window and
    /// genuinely never restarted.</summary>
    public int? RestartCount30d { get; init; }
}
