using Harbora.Domain.Common;

namespace Harbora.Domain.Deployments;

/// <summary>
/// A persisted log line. Live logs stream over SignalR; these rows back the
/// "download / search" view. Secret values are redacted before persistence.
/// </summary>
public class DeploymentLog : BaseEntity
{
    public Guid DeploymentId { get; set; }
    public Deployment? Deployment { get; set; }

    public LogStream Stream { get; set; }
    public LogLevel Level { get; set; } = LogLevel.Info;

    public long Sequence { get; set; }   // ordering within a deployment
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
