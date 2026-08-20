using Harbora.Domain.Apps;
using Harbora.Domain.Common;

namespace Harbora.Domain.Storage;

/// <summary>
/// An app's attachment to a <see cref="StorageBucket"/> (F5, 2026-08-21 functions-and-services
/// plan). Mirrors <see cref="Harbora.Domain.Apps.AppConfigGroup"/> exactly, down to the field names:
/// a bucket's env vars are fixed names (<see cref="BucketEnvKeys"/>), so attaching a second bucket
/// would silently overwrite the first one's values under the same keys — the same "second database"
/// problem <c>AttachKeys</c> solves for managed services, met here with the config-groups answer
/// instead, because the app's own env page already knows how to show "which one currently wins, and
/// why" for a merge (<see cref="Apps.ConfigGroupMerge"/>).
/// </summary>
public class AppStorageBucket : BaseEntity
{
    public Guid AppId { get; set; }
    public App? App { get; set; }

    public Guid StorageBucketId { get; set; }
    public StorageBucket? StorageBucket { get; set; }

    /// <summary>
    /// Attachment order for this app's buckets — current max + 1 when attached, never reused. Among
    /// buckets, the higher order (attached later) wins on a shared key; the app's own
    /// <see cref="EnvironmentVariable"/> rows always win over any bucket, exactly as they do over any
    /// <see cref="ConfigGroup"/>.
    /// </summary>
    public int AttachOrder { get; set; }

    /// <summary>
    /// The <see cref="AppConfigGroup.HasUnpublishedChanges"/> idiom, reused rather than reinvented.
    /// True whenever this app's running container might not carry this bucket's current env (just
    /// attached, or credentials rotated since); cleared only when a deployment for this app succeeds
    /// and assembles the container's environment from the bucket's current row.
    /// </summary>
    public bool HasUnpublishedChanges { get; set; } = true;
}
