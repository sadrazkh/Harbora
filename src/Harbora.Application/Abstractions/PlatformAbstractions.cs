namespace Harbora.Application.Abstractions;

/// <summary>Testable clock.</summary>
public interface ISystemClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// Minimal in-process background job queue (Channel-backed). A hosted worker drains it.
/// Deployments, backups and metric collection all flow through here so the request path
/// never blocks on long-running work.
/// </summary>
public interface IBackgroundJobQueue
{
    ValueTask EnqueueAsync(Func<IServiceProvider, CancellationToken, Task> job, CancellationToken ct = default);
    ValueTask<Func<IServiceProvider, CancellationToken, Task>> DequeueAsync(CancellationToken ct);
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

    /// <summary>Open a completed backup's artifact for download.</summary>
    Task<(Stream Stream, string FileName)> OpenArtifactAsync(Guid backupId, CancellationToken ct);

    /// <summary>Apply retention rules (delete artifacts + rows past the keep window/count).</summary>
    Task EnforceRetentionAsync(CancellationToken ct);
}

/// <summary>Fan-out for alerts across configured channels (email/Telegram/Discord/webhook).</summary>
public interface INotificationService
{
    /// <summary>
    /// Deliver a notification to every enabled alert in the workspace that opted into this event
    /// and whose minimum severity is satisfied. Best-effort — channel failures are logged, not thrown.
    /// </summary>
    Task NotifyAsync(Guid workspaceId, Domain.Common.AlertEvent evt, Domain.Common.AlertSeverity severity, string title, string body, CancellationToken ct);

    /// <summary>Send a one-off test message to a single alert (for the "test" button).</summary>
    Task SendTestAsync(Guid alertId, CancellationToken ct);
}
