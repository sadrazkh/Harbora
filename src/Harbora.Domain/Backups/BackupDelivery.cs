using Harbora.Domain.Common;

namespace Harbora.Domain.Backups;

/// <summary>Where a copy of a finished backup is sent.</summary>
public enum BackupDeliveryChannel
{
    Telegram = 0,
    Email = 1
}

/// <summary>
/// A channel that receives a copy of every backup in the workspace, scheduled or manual.
///
/// Deliberately separate from <see cref="BackupDestination"/>. A destination is where the artifact
/// *lives* — restore reads from it, retention deletes from it. A chat or a mailbox can do neither:
/// nothing can fetch a file back out of a sent email, and an unsent Telegram message is not a
/// reliable thing to promise. So this sends a copy, and the destination stays the copy that can
/// actually be restored from.
/// </summary>
public class BackupDelivery : BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public BackupDeliveryChannel Channel { get; set; }

    /// <summary>Channel settings as JSON, encrypted: a bot token and chat id, or SMTP credentials.</summary>
    public string EncryptedConfig { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Largest artifact this channel will be asked to carry, or 0 for the channel's own ceiling.
    /// Configurable because a self-hosted mail server may allow far more, or far less, than the
    /// default assumption.
    /// </summary>
    public long MaxSizeBytes { get; set; }

    /// <summary>When this channel was last used — blank means it has never carried a backup.</summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>
    /// Why the last attempt failed, or null if it worked. On the row rather than only in the logs,
    /// because a backup channel that quietly stopped working is worse than one that was never set up.
    /// </summary>
    public string? LastError { get; set; }
}
