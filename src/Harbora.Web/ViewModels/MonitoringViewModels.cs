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
    public double CpuPercent { get; set; }

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
}

public sealed record AppHealth(string Name, string Slug, string Status, string? LastDeployStatus, string ContainerState)
{
    /// <summary>Needed by the threshold-alert picker, which has to post an application id.</summary>
    public Guid Id { get; init; }
}
