using Harbora.Domain.Common;

namespace Harbora.Domain.Monitoring;

/// <summary>A configured notification rule + channel.</summary>
public class Alert : BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public AlertChannel Channel { get; set; }
    public AlertSeverity MinSeverity { get; set; } = AlertSeverity.Warning;

    /// <summary>Channel target, encrypted (webhook URL, Telegram chat id + token, email …).</summary>
    public string EncryptedTarget { get; set; } = string.Empty;

    // Which events fire this alert.
    public bool OnDeployFailed { get; set; } = true;
    public bool OnAppCrashed { get; set; } = true;
    public bool OnSslExpiring { get; set; } = true;
    public bool OnDiskWarning { get; set; } = true;
    public bool OnBackupFailed { get; set; } = true;

    public bool IsEnabled { get; set; } = true;
}
