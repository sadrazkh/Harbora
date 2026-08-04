using Harbora.Domain.Common;

namespace Harbora.Domain.Ai;

/// <summary>
/// A key a customer uses against Harbora's AI endpoint.
///
/// It is Harbora's key, not a provider's. The customer never holds a provider token: requests are
/// made server-side, so the provider sees Harbora's infrastructure and never the customer's address,
/// and revoking access here really revokes it rather than leaving a working upstream token in
/// somebody's environment file.
///
/// Only a hash is stored. The secret is shown once at creation — a key that can be read back from
/// the panel is a key that a support screenshot can leak.
/// </summary>
public class AiUserApiKey : BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>What the person calls it — "staging", "my laptop".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// The visible beginning, e.g. <c>har_a1b2c3</c>. Enough to tell two keys apart in a list and
    /// to find one in a log, and useless on its own.
    /// </summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Salted hash of the whole secret.</summary>
    public string KeyHash { get; set; } = string.Empty;

    public bool IsRevoked { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Optional CIDR allowlist, same rules as database access.</summary>
    public string? AllowedIps { get; set; }

    /// <summary>Reserved for narrowing a key later. Empty means the plan's full surface.</summary>
    public string? Scopes { get; set; }
}
