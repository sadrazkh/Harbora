namespace Harbora.Application.Abstractions;

/// <summary>Testable clock.</summary>
public interface ISystemClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Collects host + container metrics into the monitoring store.</summary>
public interface IMetricsCollector
{
    Task CollectAsync(CancellationToken ct);
}

/// <summary>Runs, restores, downloads and prunes backups against a destination.</summary>
public interface IBackupEngine
{
    /// <summary>Create the backup row and queue the work on the background worker; returns the backup id.</summary>
    Task<Guid> QueueBackupAsync(Guid workspaceId, Domain.Common.BackupType type, string targetRef, Guid destinationId, bool scheduled, CancellationToken ct);

    /// <summary>Restore a completed backup. Destructive — callers must confirm first.</summary>
    Task RestoreAsync(Guid backupId, CancellationToken ct);

    /// <summary>
    /// Dry run: fetch the artifact and check it is intact and readable WITHOUT touching live data.
    /// A backup nobody has ever verified is a promise, not a safety net.
    /// </summary>
    Task<BackupVerification> VerifyAsync(Guid backupId, CancellationToken ct);

    /// <summary>Open a completed backup's artifact for download.</summary>
    Task<(Stream Stream, string FileName)> OpenArtifactAsync(Guid backupId, CancellationToken ct);

    /// <summary>Apply retention rules (delete artifacts + rows past the keep window/count).</summary>
    Task EnforceRetentionAsync(CancellationToken ct);
}

/// <summary>
/// Outcome of a dry-run verification. <paramref name="Checks"/> lists every individual check so the
/// UI can show what passed, not just a verdict.
/// </summary>
public record BackupVerification(
    bool IsRestorable,
    string? Reason,
    long SizeBytes,
    IReadOnlyList<BackupCheck> Checks)
{
    public static BackupVerification Failed(string reason, params BackupCheck[] checks) =>
        new(false, reason, 0, checks);
}

/// <summary>
/// One thing that was checked about a backup.
///
/// <paramref name="Skipped"/> exists because "not checked" and "checked and fine" must never look
/// the same on a screen — a Redis snapshot cannot be restored into a scratch database, and saying so
/// is honest, while showing a failed check would be alarming and showing a passed one would be a lie.
/// </summary>
public record BackupCheck(string Name, bool Passed, string? Detail = null, bool Skipped = false);

/// <summary>
/// What happened when a notification was handed to a channel.
///
/// The point of returning this is that "sent" used to mean "we called something and did not crash":
/// a webhook answering 404 counted as delivered, and the panel's Test button reported success
/// unconditionally. A channel nobody can tell is broken is worse than no channel.
/// </summary>
public sealed record NotificationResult(bool Delivered, string? Error = null)
{
    public static readonly NotificationResult Ok = new(true);
    public static NotificationResult Failed(string error) => new(false, error);
}

/// <summary>Fan-out for alerts across configured channels (email/Telegram/Discord/webhook).</summary>
public interface INotificationService
{
    /// <summary>
    /// Deliver a notification to every enabled alert in the workspace that opted into this event
    /// and whose minimum severity is satisfied. Best-effort — channel failures are logged, not thrown.
    ///
    /// <para>
    /// Answers how many rules it was handed to, and that number is the only way a caller can tell
    /// the difference between "delivered" and "there was nobody to deliver to". Nothing seeds an
    /// alert rule — the alerts page is the only thing in the product that creates one — so a
    /// workspace with none is the ordinary case, not an edge one, and against it this method
    /// iterates nothing, raises nothing and returns. A caller that discards the count records a
    /// notification sent to nobody; the failure of a channel that <i>exists</i> is a different fact
    /// and is written back onto the rule, where a broken channel is meant to be read.
    /// </para>
    /// </summary>
    Task<int> NotifyAsync(Guid workspaceId, Domain.Common.AlertEvent evt, Domain.Common.AlertSeverity severity, string title, string body, CancellationToken ct);

    /// <summary>
    /// Deliver through one specific rule, whatever its event opt-ins say.
    ///
    /// A per-application threshold belongs to the rule that defines it: broadcasting it to every
    /// channel in the workspace would tell people who never asked about that app, and matching it
    /// against the event flags would require a sixth flag nobody set. Used by the threshold
    /// evaluator, which already holds the row.
    /// </summary>
    Task<NotificationResult> NotifyRuleAsync(
        Guid alertId, Domain.Common.AlertSeverity severity, string title, string body, CancellationToken ct);

    /// <summary>Send a one-off test message to a single alert (for the "test" button).</summary>
    /// <summary>
    /// Sends a test notification and reports what actually happened, so the panel can say "that URL
    /// returned 404" instead of "sent".
    /// </summary>
    Task<NotificationResult> SendTestAsync(Guid alertId, CancellationToken ct);
}
