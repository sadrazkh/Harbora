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

/// <summary>Runs and restores backups against a destination.</summary>
public interface IBackupEngine
{
    Task<Guid> RunBackupAsync(Guid workspaceId, Domain.Common.BackupType type, string targetRef, Guid destinationId, CancellationToken ct);
    Task RestoreAsync(Guid backupId, CancellationToken ct);
}

/// <summary>Fan-out for alerts across configured channels (email/Telegram/Discord/webhook).</summary>
public interface INotificationService
{
    Task NotifyAsync(Guid workspaceId, Domain.Common.AlertSeverity severity, string title, string body, CancellationToken ct);
}
