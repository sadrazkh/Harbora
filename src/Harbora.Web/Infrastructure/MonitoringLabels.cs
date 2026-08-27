using Harbora.Domain.Common;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// Every bit of vocabulary the monitoring page and its partials need to turn a domain value into a
/// sentence a person reads — moved out of <c>Views/Monitoring/Index.cshtml</c> when that view was
/// split into partials (2026-08-19 monitoring redesign), because a Razor local function only lives in
/// the file that declares it and the same wording is needed in more than one of them now.
///
/// <para>
/// Follows the same rule <see cref="StatusLabel"/> already does: one mapping per concept, used by
/// every partial, so two panels cannot describe one state with two different words.
/// </para>
/// </summary>
public static class MonitoringLabels
{
    /// <summary>A percentage-shaped number, or empty when there is nothing to show — callers that
    /// need the full "unmeasured" sentence go through <c>MetricDisplay</c> instead; this only formats
    /// a value that is already known to exist.</summary>
    public static string Pct(double? d) => d is null ? "" : d.Value.ToString("0.#");

    /// <summary>
    /// RestartRate is not a percentage of an allocation the way the other two metrics are — the
    /// summary line has to say so rather than appending "%" to a restart count.
    /// </summary>
    public static string ThresholdSummary(Harbora.Domain.Monitoring.Alert a, string? appName, bool isFa) => a.Metric switch
    {
        AlertMetric.RestartRate => isFa
            ? $"روی «{appName}» · بیش از {Pct(a.ThresholdPercent)} بار ری‌استارت در {a.SustainedMinutes} دقیقه"
            : $"watches {appName} · more than {Pct(a.ThresholdPercent)} restart(s) in {a.SustainedMinutes}m",
        // C2 (2026-08-27 "the outage nobody sees coming"): no "for Nm" — Volume.StorageBytes is a
        // periodic measurement, not a live sample, so SustainedMinutes plays no part in this metric
        // the way it does for CPU/memory below (EvaluateDiskThresholdsAsync's own doc says why).
        AlertMetric.DiskPercent => isFa
            ? $"روی «{appName}» · دیسک ≥ {Pct(a.ThresholdPercent)}٪ از سقف والیوم"
            : $"watches {appName} · disk ≥ {Pct(a.ThresholdPercent)}% of volume limit",
        _ => isFa
            ? $"روی «{appName}» · {(a.Metric == AlertMetric.CpuPercent ? "CPU" : "حافظه")} ≥ {Pct(a.ThresholdPercent)}٪ به مدت {a.SustainedMinutes} دقیقه"
            : $"watches {appName} · {a.Metric} ≥ {Pct(a.ThresholdPercent)}% for {a.SustainedMinutes}m"
    };

    /// <summary>
    /// The semantic tone a container's own state pill renders in. A state Docker never reported
    /// (<c>"unknown"</c> — Docker unreachable, or the app has never deployed) gets the <c>idle</c>
    /// tone that already exists for exactly this shape of fact, rather than the neutral/slate look
    /// that used to make "unknown" read as just another resting state.
    /// </summary>
    public static string ContainerTone(string? state) => state?.ToLowerInvariant() switch
    {
        "running" => Harbora.Web.ViewModels.Tone.Ok,
        "exited" or "crashed" or "failed" or "dead" => Harbora.Web.ViewModels.Tone.Error,
        "restarting" => Harbora.Web.ViewModels.Tone.Warn,
        _ => Harbora.Web.ViewModels.Tone.Idle
    };

    /// <summary>Reuses <see cref="AlertEvent"/> — the same vocabulary the notification router matches
    /// on — so a condition's name reads the same here as it does in the rule that watches for it.</summary>
    public static string ConditionLabel(AlertEvent c, bool isFa) => (c, isFa) switch
    {
        (AlertEvent.DeployFailed, true) => "استقرار ناموفق", (AlertEvent.DeployFailed, false) => "Deploy failed",
        (AlertEvent.AppCrashed, true) => "خرابی برنامه", (AlertEvent.AppCrashed, false) => "App crashed",
        (AlertEvent.SslExpiring, true) => "انقضای گواهی SSL", (AlertEvent.SslExpiring, false) => "SSL expiring",
        (AlertEvent.DiskWarning, true) => "هشدار فضای دیسک", (AlertEvent.DiskWarning, false) => "Disk warning",
        (AlertEvent.BackupFailed, true) => "پشتیبان‌گیری ناموفق", (AlertEvent.BackupFailed, false) => "Backup failed",
        (AlertEvent.ThresholdBreached, true) => "عبور از آستانه", (AlertEvent.ThresholdBreached, false) => "Threshold breached",
        (AlertEvent.LowBalance, true) => "موجودی کم", (AlertEvent.LowBalance, false) => "Low balance",
        _ => c.ToString()
    };

    /// <summary>The one interesting fact an incident that only says "closed" would have lost — see
    /// <see cref="IncidentClosedReason"/>'s own doc comment for why each of the three is worded the
    /// way it is.</summary>
    public static string ReasonLabel(IncidentClosedReason r, bool isFa) => (r, isFa) switch
    {
        (IncidentClosedReason.Resolved, true) => "به‌طور خودکار برطرف شد", (IncidentClosedReason.Resolved, false) => "resolved automatically",
        (IncidentClosedReason.Acknowledged, true) => "تأیید شد", (IncidentClosedReason.Acknowledged, false) => "acknowledged",
        (IncidentClosedReason.Expired, true) => "منقضی شد", (IncidentClosedReason.Expired, false) => "expired",
        _ => r.ToString()
    };

    /// <summary>N1 (2026-08-16 notification-system spec): the delivery log. Pending/Sent/Failed/Suppressed
    /// — the same four-way split <see cref="NotificationDeliveryStatus"/> itself carries, so a row's
    /// own colour never has to be reasoned about separately from its meaning.</summary>
    public static string DeliveryStatusLabel(NotificationDeliveryStatus s, bool isFa) => (s, isFa) switch
    {
        (NotificationDeliveryStatus.Pending, true) => "در صف", (NotificationDeliveryStatus.Pending, false) => "pending",
        (NotificationDeliveryStatus.Sent, true) => "ارسال شد", (NotificationDeliveryStatus.Sent, false) => "sent",
        (NotificationDeliveryStatus.Failed, true) => "ناموفق", (NotificationDeliveryStatus.Failed, false) => "failed",
        (NotificationDeliveryStatus.Suppressed, true) => "ارسال نشد", (NotificationDeliveryStatus.Suppressed, false) => "suppressed",
        _ => s.ToString()
    };

    public static string DeliveryPurposeLabel(NotificationDeliveryPurpose p, bool isFa) => (p, isFa) switch
    {
        (NotificationDeliveryPurpose.AlertDispatch, true) => "قانون هشدار",
        (NotificationDeliveryPurpose.AlertDispatch, false) => "alert rule",
        (NotificationDeliveryPurpose.NoRecipientFallback, true) => "بدون قانون هشدار — به مدیران",
        (NotificationDeliveryPurpose.NoRecipientFallback, false) => "no alert rule — sent to admins",
        (NotificationDeliveryPurpose.PasswordReset, true) => "بازنشانی رمز عبور",
        (NotificationDeliveryPurpose.PasswordReset, false) => "password reset",
        (NotificationDeliveryPurpose.EmailVerification, true) => "تأیید ایمیل",
        (NotificationDeliveryPurpose.EmailVerification, false) => "email verification",
        (NotificationDeliveryPurpose.WorkspaceInvite, true) => "دعوت به ورک‌اسپیس",
        (NotificationDeliveryPurpose.WorkspaceInvite, false) => "workspace invite",
        (NotificationDeliveryPurpose.PlatformInvite, true) => "دعوت به پلتفرم",
        (NotificationDeliveryPurpose.PlatformInvite, false) => "platform invite",
        _ => p.ToString()
    };

    /// <summary>The semantic tone a delivery row's status pill renders in.</summary>
    public static string DeliveryStatusTone(NotificationDeliveryStatus s) => s switch
    {
        NotificationDeliveryStatus.Sent => Harbora.Web.ViewModels.Tone.Ok,
        NotificationDeliveryStatus.Failed => Harbora.Web.ViewModels.Tone.Error,
        NotificationDeliveryStatus.Suppressed => Harbora.Web.ViewModels.Tone.Idle,
        _ => Harbora.Web.ViewModels.Tone.Info
    };
}
