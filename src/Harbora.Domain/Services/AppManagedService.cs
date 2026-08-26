using Harbora.Domain.Apps;
using Harbora.Domain.Common;

namespace Harbora.Domain.Services;

/// <summary>
/// An app's attachment to a <see cref="ManagedService"/> (C1, 2026-08-22 config-delivery plan).
/// Mirrors <see cref="Harbora.Domain.Storage.AppStorageBucket"/> exactly, down to the field names —
/// with one addition: <see cref="Alias"/>, because a bucket's env vars are a single fixed set
/// (<c>S3_*</c>) that a second attach is allowed to silently outrank, while a customer routinely
/// wants two databases attached to the same app at once and reachable by different names. The
/// "second one wins" answer <see cref="AppStorageBucket"/> gives buckets is wrong here — see
/// <see cref="AppManagedServiceAlias"/> for how a collision is made impossible instead of latent.
///
/// This is deliberately a second, additional way for an app to receive a database's env, alongside
/// the older per-app <c>EnvironmentVariable</c> copies <c>DatabasesController.Attach</c> has written
/// since 2026-08-16 (mirrored/rewritten in place by <c>ManagedServiceEngine.RotatePasswordAsync</c>).
/// That path still works and is not touched by this one. The two do not conflict: an app's own
/// <see cref="EnvironmentVariable"/> row always outranks anything this join contributes
/// (<see cref="Apps.ConfigGroupMerge"/>), so a legacy attach's materialized copy simply continues to
/// win on the same key, and this join's live-computed value is what a customer sees for any key that
/// is not already claimed that way — in particular the alias-prefixed names, which the legacy path
/// never wrote.
/// </summary>
public class AppManagedService : BaseEntity
{
    public Guid AppId { get; set; }
    public App? App { get; set; }

    public Guid ManagedServiceId { get; set; }
    public ManagedService? ManagedService { get; set; }

    /// <summary>
    /// The logical database inside <see cref="ManagedService"/> this attachment actually points at
    /// (D1, 2026-08-25 shared-databases plan), or null for an attachment this platform never
    /// re-pointed: one made before logical databases existed, or one whose engine cannot open a
    /// second database at all (Redis, RabbitMQ, NATS — see <c>DatabaseGrantSql.Supports</c>). Null
    /// resolves to the instance's own admin database exactly as every attachment did before this
    /// shipped (<c>AttachedServiceConnectionResolver</c>, <c>ManagedServiceAttachEnv</c>) — the
    /// fallback a migration backfills away for every engine that has a logical database at all, but
    /// which a test seeding a <see cref="ManagedService"/> directly (bypassing
    /// <c>DatabasesController.Create</c>) still exercises, deliberately.
    /// </summary>
    public Guid? ManagedServiceDatabaseId { get; set; }
    public ManagedServiceDatabase? Database { get; set; }

    /// <summary>
    /// The customer-facing name for this one attachment — defaults to the service's own slug, but is
    /// never allowed to collide with another attachment already on this app
    /// (<see cref="AppManagedServiceAlias.Resolve"/>). This is the prefix an app's second (or third)
    /// database is reached under: <c>{ALIAS}_DATABASE_URL</c>, <c>{ALIAS}_PGHOST</c>, and so on.
    /// </summary>
    public string Alias { get; set; } = string.Empty;

    /// <summary>
    /// Attachment order for this app's services — current max + 1 when attached, never reused. Among
    /// services of the same engine sharing an unprefixed/"magic" key (see
    /// <see cref="Apps.AttachedDatabaseEnv"/>), the higher order (attached later) wins on that shared
    /// key — exactly the rule <see cref="AppStorageBucket.AttachOrder"/> already documents for
    /// buckets. The alias-prefixed keys never collide regardless of order.
    /// </summary>
    public int AttachOrder { get; set; }

    /// <summary>
    /// The <see cref="AppStorageBucket.HasUnpublishedChanges"/> idiom, reused rather than reinvented.
    /// True whenever this app's running container might not carry this service's current connection
    /// string (just attached, or credentials rotated since — see
    /// <c>ManagedServiceEngine.RotatePasswordAsync</c>, which sets this true on every attachment of a
    /// service whose password it just changed); cleared only when a deployment for this app succeeds
    /// and assembles the container's environment from the service's current credentials.
    /// </summary>
    public bool HasUnpublishedChanges { get; set; } = true;
}
