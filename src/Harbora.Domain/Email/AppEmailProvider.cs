using Harbora.Domain.Apps;
using Harbora.Domain.Common;

namespace Harbora.Domain.Email;

/// <summary>
/// An app's attachment to an <see cref="EmailProvider"/> (F6, 2026-08-21 functions-and-services
/// plan). Mirrors <see cref="Harbora.Domain.Storage.AppStorageBucket"/> exactly, down to the field
/// names: a provider's env vars are fixed names (<see cref="EmailProviderEnvKeys"/>), so attaching a
/// second provider would silently overwrite the first one's values under the same keys — met here
/// with the same config-groups-style merge answer F5 gave buckets
/// (<see cref="Apps.ConfigGroupMerge"/>), rather than reinvented.
/// </summary>
public class AppEmailProvider : BaseEntity
{
    public Guid AppId { get; set; }
    public App? App { get; set; }

    public Guid EmailProviderId { get; set; }
    public EmailProvider? EmailProvider { get; set; }

    /// <summary>Attachment order for this app's providers — current max + 1 when attached, never
    /// reused. Among providers, the higher order (attached later) wins on a shared key.</summary>
    public int AttachOrder { get; set; }

    /// <summary>
    /// The <see cref="Apps.AppConfigGroup.HasUnpublishedChanges"/> idiom, reused rather than
    /// reinvented. True whenever this app's running container might not carry this provider's
    /// current env (just attached, or credentials rotated since); cleared only when a deployment for
    /// this app succeeds and assembles the container's environment from the provider's current row.
    /// </summary>
    public bool HasUnpublishedChanges { get; set; } = true;
}
