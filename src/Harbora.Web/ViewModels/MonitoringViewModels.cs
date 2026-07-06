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

    public bool DiskWarning => DiskTotal > 0 && (double)DiskUsed / DiskTotal >= 0.85;

    public List<AppHealth> Apps { get; set; } = new();
    public List<Deployment> RecentDeploys { get; set; } = new();
    public int FailedDeploys { get; set; }

    public bool BackupWarning { get; set; }
    public string? BackupWarningText { get; set; }

    public List<DomainName> Domains { get; set; } = new();
    public List<Alert> Alerts { get; set; } = new();
}

public sealed record AppHealth(string Name, string Slug, string Status, string? LastDeployStatus, string ContainerState);
