namespace Harbora.Modules.Backup.Contracts;

/// <summary>
/// What the operator is told when something happens to a backup.
///
/// <para>
/// Behind an interface of its own rather than calling the platform's notifier directly, so that the
/// set of backup-specific events is visible in one place and future channels (Telegram, Slack,
/// webhook) can be added without touching the jobs that raise them. The implementation in this
/// branch forwards to Harbora's existing notification service — no new channel is invented.
/// </para>
/// </summary>
public interface IBackupNotificationService
{
    Task SendAsync(BackupNotification notification, CancellationToken cancellationToken);
}

public enum BackupNotificationKind
{
    BackupFailed = 0,

    /// <summary>
    /// No successful backup within the policy's expected window. Distinct from
    /// <see cref="BackupFailed"/>: a schedule that silently stopped firing raises nothing at all,
    /// and is the more dangerous of the two precisely because it is quiet.
    /// </summary>
    NoRecentBackup = 1,

    RepositoryUnavailable = 2,
    RepositoryAlmostFull = 3,
    DeviceOffline = 4,
    AgentOutdated = 5,
    SnapshotVerificationFailed = 6,
    RestoreCompleted = 7,
    RestoreFailed = 8,
    SyncConflictDetected = 9,
    SyncDeviceDisconnected = 10
}

public enum BackupNotificationSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

/// <summary>
/// <paramref name="Detail"/> reaches a human and may reach a chat channel, so it must already be
/// redacted by the caller — engine output is not safe to pass through verbatim.
/// </summary>
public sealed record BackupNotification(
    Guid WorkspaceId,
    BackupNotificationKind Kind,
    BackupNotificationSeverity Severity,
    string Title,
    string Detail,
    Guid? RepositoryId = null,
    Guid? SnapshotId = null,
    Guid? PolicyId = null);
