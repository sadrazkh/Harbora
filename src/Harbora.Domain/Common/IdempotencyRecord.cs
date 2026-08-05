

namespace Harbora.Domain.Common;

/// <summary>
/// Remembers what an <c>Idempotency-Key</c> already produced.
///
/// <para>
/// A row in the database rather than a process-local cache, because the panel can run more than one
/// instance: a retry that lands on a different replica must get the SAME answer, not a second
/// restore. An in-memory table would look correct on one machine and start duplicating destructive
/// work the moment the deployment scaled.
/// </para>
/// <para>
/// Scoped by workspace as well as by key. The key is chosen by the client, so two tenants can pick
/// the same string, and one must never see the other's result.
/// </para>
/// </summary>
public class IdempotencyRecord : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>Client-supplied key. Bounded so a hostile client cannot store megabytes.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Which operation the key was used for. Part of the identity so one key reused against a
    /// different endpoint is a new request rather than a confusing replay of an unrelated result.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The entity the original call created — the snapshot or restore job id.</summary>
    public Guid ResultId { get; set; }

    /// <summary>
    /// After this, the key may be reused.
    ///
    /// <para>
    /// Bounded rather than kept forever: an idempotency table that only grows is a table that
    /// eventually needs an outage to clean up. A day is far longer than any client retry window.
    /// </para>
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddDays(1);
}
