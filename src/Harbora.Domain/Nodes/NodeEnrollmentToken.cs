using Harbora.Domain.Common;

namespace Harbora.Domain.Nodes;

/// <summary>
/// A short-lived, single-use ticket that lets one machine become one node.
///
/// <para>
/// Only a hash is stored, for the same reason API tokens are: a database dump should not be a list
/// of working credentials. The prefix is kept in clear so an admin can tell two outstanding tokens
/// apart in the UI without the panel having to hold either of them.
/// </para>
/// </summary>
public class NodeEnrollmentToken : BaseEntity
{
    /// <summary>First few characters of the token, for display. Not enough to use.</summary>
    public string Prefix { get; set; } = string.Empty;

    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set the moment the token is spent. A second attempt with it is refused.</summary>
    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>The node this token became, once it was spent.</summary>
    public string? UsedByNodeId { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public Guid CreatedByUserId { get; set; }

    /// <summary>Name suggested when the token was minted; the installer may override it.</summary>
    public string? NodeNameHint { get; set; }

    public string? Region { get; set; }
    public string? Environment { get; set; }

    /// <summary>JSON object of labels applied to whatever node this token creates.</summary>
    public string LabelsJson { get; set; } = "{}";

    /// <summary>
    /// JSON array of scopes the resulting node's credential will carry.
    ///
    /// <para>
    /// Pinned at mint time rather than at enrollment. The admin who creates a token is the one who
    /// decides what the node may be asked to do, and deciding it later would mean the answer could
    /// change between "an admin approved this" and "a machine showed up".
    /// </para>
    /// </summary>
    public string ScopesJson { get; set; } = "[]";

    public bool IsSpent => UsedAt is not null;
    public bool IsRevoked => RevokedAt is not null;

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public bool IsUsable(DateTimeOffset now) => !IsSpent && !IsRevoked && !IsExpired(now);
}
