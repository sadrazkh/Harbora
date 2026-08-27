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

    /// <summary>
    /// C1 (2026-08-27 "warn before the refusal"): fires when the workspace is close to one of its
    /// plan's committed-capacity caps — apps, services, memory, CPU or disk. Same shape as
    /// <see cref="OnDiskWarning"/> deliberately: the condition is workspace-wide, not one app's own
    /// metric, so it does not fit the <see cref="AppId"/>+<see cref="Metric"/> threshold shape below.
    /// </summary>
    public bool OnQuotaWarning { get; set; } = true;

    public bool IsEnabled { get; set; } = true;

    // ---- Per-application threshold (optional) ----
    //
    // Additive: an alert with no AppId is exactly the event-driven rule it always was. When these
    // are set, the same channel also fires when one application holds above a line for a while.

    /// <summary>The application this threshold watches, or null for a workspace-wide event rule.</summary>
    public Guid? AppId { get; set; }

    /// <summary>Which figure to watch. Null when this is not a threshold rule.</summary>
    public AlertMetric? Metric { get; set; }

    /// <summary>
    /// The line. A percentage of the application's own allocation for <see cref="AlertMetric.CpuPercent"/>
    /// and <see cref="AlertMetric.MemoryPercent"/> — for <see cref="AlertMetric.RestartRate"/> it is
    /// repurposed as a plain restart count, because a restart has no allocation to be a percentage of.
    /// </summary>
    public double? ThresholdPercent { get; set; }

    /// <summary>
    /// How long it must hold before anyone is told, for <see cref="AlertMetric.CpuPercent"/> and
    /// <see cref="AlertMetric.MemoryPercent"/>: a container touches 100% CPU on every start, and
    /// alerting on one sample fills a channel with noise. For <see cref="AlertMetric.RestartRate"/>
    /// this is repurposed as the rolling window restarts are counted over instead — "in the last N
    /// minutes" rather than "held for N minutes".
    /// </summary>
    public int SustainedMinutes { get; set; } = 5;

    /// <summary>When this threshold last fired, so a standing breach nags rather than floods.</summary>
    public DateTimeOffset? ThresholdFiredAt { get; set; }

    /// <summary>When this channel was last attempted — blank means it has never been used.</summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>
    /// Why the last attempt failed, or null if it succeeded. Kept on the rule rather than only in the
    /// log because the person who needs to know is looking at the alerts page, not at the panel logs.
    /// </summary>
    public string? LastError { get; set; }
}
