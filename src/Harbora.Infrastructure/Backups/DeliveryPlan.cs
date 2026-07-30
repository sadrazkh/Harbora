using System.Globalization;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;

namespace Harbora.Infrastructure.Backups;

/// <summary>
/// The decisions about sending a backup that do not need a network: whether it will fit, what the
/// file should be called, and what the message should say.
///
/// Size is the one that matters. Telegram refuses documents over 50 MB and mail servers refuse
/// attachments long before that, so a backup channel that simply tries and fails is a channel that
/// looks configured and silently protects nothing. Checking first turns that into a sentence someone
/// can act on.
/// </summary>
public static class DeliveryPlan
{
    /// <summary>Telegram's own ceiling for a document sent by a bot.</summary>
    public const long TelegramMaxBytes = 50L * 1024 * 1024;

    /// <summary>A conservative default for mail; most servers reject somewhere between 10 and 25 MB.</summary>
    public const long EmailMaxBytes = 20L * 1024 * 1024;

    public static long DefaultLimitFor(BackupDeliveryChannel channel) => channel switch
    {
        BackupDeliveryChannel.Telegram => TelegramMaxBytes,
        _ => EmailMaxBytes
    };

    /// <summary>The limit in force: the configured one when set, otherwise the channel's own.</summary>
    public static long LimitFor(BackupDeliveryChannel channel, long configured) =>
        configured > 0 ? configured : DefaultLimitFor(channel);

    /// <summary>
    /// Whether this artifact can be sent, and if not, why — naming both numbers and a way forward,
    /// because the answer is never "make the backup smaller".
    /// </summary>
    public static string? RejectionReason(BackupDeliveryChannel channel, long configured, long sizeBytes)
    {
        var limit = LimitFor(channel, configured);
        if (sizeBytes <= limit) return null;

        return $"The backup is {Describe(sizeBytes)}, and {channel} accepts at most {Describe(limit)}. " +
               "Keep using a storage destination (local or S3) for artifacts this size — this channel " +
               "is for copies small enough to carry.";
    }

    /// <summary>Sizes people can read, in the invariant calendar-free way a log needs.</summary>
    public static string Describe(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? (bytes / 1024d / 1024 / 1024).ToString("0.#", CultureInfo.InvariantCulture) + " GB"
        : bytes >= 1024L * 1024 ? (bytes / 1024d / 1024).ToString("0.#", CultureInfo.InvariantCulture) + " MB"
        : bytes >= 1024 ? (bytes / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " KB"
        : bytes + " B";

    /// <summary>
    /// What the recipient sees. Says which instance, which target and when, because a chat that
    /// receives backups from several places is otherwise a pile of identical-looking files.
    /// </summary>
    public static string Caption(string instance, BackupType type, string target, long sizeBytes, DateTimeOffset at) =>
        $"Harbora backup — {type}" +
        (string.IsNullOrWhiteSpace(target) ? "" : $" · {target}") +
        $"\n{instance}\n{at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC · {Describe(sizeBytes)}";
}
